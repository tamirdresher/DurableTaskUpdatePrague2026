#Requires -Version 7.2
<#
.SYNOPSIS
    Stage driver for "Code That Survives the Crash" (Update Conference Prague 2026).

.DESCRIPTION
    Each act is one live demo. Every step waits for Enter and prints short output that reads
    well on a projector. The emulator container, the run folder and the PIDs of the workers this
    script started are kept in .stage\state.json, so each act can run at a different point of
    the talk from the same terminal.

    Everything is local: the Durable Task Scheduler emulator in Docker, fake flight/hotel/car
    providers and scripted agent models. Nothing calls a real provider, model or Azure.

.EXAMPLE
    .\Stage.ps1 rehearse               # before setup: every demo without pauses on its own emulator; exit code 1 on any surprise
    .\Stage.ps1 setup -OpenDashboard   # before the session starts
    .\Stage.ps1 naive                  # three plain awaits: crash, rerun, pay twice
    .\Stage.ps1 crash                  # durable worker dies between hotel and car, then resumes
    .\Stage.ps1 ackgap                 # optional: hotel booked, worker dies before reporting back
    .\Stage.ps1 agents                 # durable agents: plan, kill the worker, approve, book
    .\Stage.ps1 status                 # what is running
    .\Stage.ps1 cleanup                # after the session
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'naive', 'crash', 'ackgap', 'agents', 'status', 'cleanup', 'rehearse')]
    [string] $Act = 'status',
    [switch] $NoPause,
    [switch] $OpenDashboard,
    [ValidateRange(60, 3600)]
    [int] $ApprovalSeconds = 900,
    [int] $GrpcPort = 18080,
    [int] $DashboardPort = 18082,
    [string] $EmulatorImage = 'mcr.microsoft.com/dts/dts-emulator@sha256:361323065a608d605f9d3ae56b854eb11f1c47fcc16845a37f948f03ca9c5fac'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
$StatePath = Join-Path $Root '.stage\state.json'
$IntroDll = Join-Path $Root 'BookingIntro\bin\Debug\net10.0\BookingIntro.dll'
$AgentsDll = Join-Path $Root 'BookingAgents\bin\Debug\net10.0\BookingAgents.dll'
$script:Pause = -not ($NoPause -or $Act -eq 'rehearse')
$script:Failures = [Collections.Generic.List[string]]::new()
# The acts set these variables for the demo CLIs; the finally block at the end restores them.
$savedEnv = @{}
foreach ($name in 'PRAGUE_BOOKING_DATA', 'PRAGUE_DTS_ENDPOINT', 'PRAGUE_BOOKING_CRASH') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}

# ---------- output ----------

function Say([string] $Text, [ConsoleColor] $Color = 'Gray') {
    Write-Host "  $Text" -ForegroundColor $Color
}

function Title([string] $Text) {
    Write-Host ''
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * $Text.Length)) -ForegroundColor DarkCyan
}

# One stage step: waits for Enter, or just prints the step with -NoPause and during rehearse.
function Next([string] $Text) {
    Write-Host ''
    if (-not $script:Pause) {
        Say "> $Text" DarkGray
        return
    }
    Write-Host "  [Enter] $Text " -ForegroundColor Yellow -NoNewline
    [void] (Read-Host)
}

# A failed check doesn't stop the act: it's shown on screen, and the script exits with code 1 at the end.
function Expect([bool] $Condition, [string] $Message) {
    if ($Condition) {
        return
    }
    $script:Failures.Add($Message)
    Say "UNEXPECTED: $Message" Red
    Say 'Fallback: show the captured run (appendix slide "Use real captures").' DarkGray
}

# ---------- state ----------

# .stage\state.json: the emulator container, ports, run folder, instance counter and the workers we started.
function Get-State {
    if (-not (Test-Path $StatePath)) {
        return $null
    }
    return Get-Content $StatePath -Raw | ConvertFrom-Json -AsHashtable
}

function Save-State([hashtable] $State) {
    New-Item -ItemType Directory -Force -Path (Split-Path $StatePath) | Out-Null
    $State | ConvertTo-Json -Depth 6 | Set-Content -Path $StatePath -Encoding utf8
}

function Test-ContainerRunning([string] $Id) {
    if (-not $Id) {
        return $false
    }
    $running = & docker inspect --format '{{.State.Running}}' $Id 2>$null
    return $LASTEXITCODE -eq 0 -and "$running".Trim() -eq 'true'
}

# Loads the state for an act and points the demo CLIs at this session's emulator.
function Use-State([switch] $WithoutEmulator) {
    $state = Get-State
    if ($null -eq $state) {
        throw 'Not set up yet. Run: .\Stage.ps1 setup'
    }
    if (-not $WithoutEmulator -and -not (Test-ContainerRunning $state.containerId)) {
        throw 'The emulator container is not running. Run .\Stage.ps1 cleanup, then .\Stage.ps1 setup'
    }
    $env:PRAGUE_DTS_ENDPOINT = "http://localhost:$($state.grpcPort)"
    return $state
}

# trip-1, trip-2, ...: every run of an act gets a fresh instance ID.
function New-InstanceId([hashtable] $State, [string] $Prefix) {
    $State.counter = [int] $State.counter + 1
    Save-State $State
    return "$Prefix-$($State.counter)"
}

function Assert-Built {
    if (-not (Test-Path $IntroDll) -or -not (Test-Path $AgentsDll)) {
        throw 'Demos are not built. Run: .\Stage.ps1 setup'
    }
}

# ---------- demo CLIs and owned workers ----------

# Runs one demo CLI command and parses its JSON output.
function Invoke-Demo([string] $Dll, [string[]] $Arguments) {
    $text = & dotnet $Dll @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $(Split-Path $Dll -Leaf) $($Arguments -join ' ') failed: $text"
    }
    return $text | ConvertFrom-Json
}

# Starts a worker in the background, logging to the run folder. Its PID and start time go into the state,
# so cleanup can tell our workers from other processes that later reuse the PID.
function Start-Worker([hashtable] $State, [string] $Dll, [string] $Data, [string] $Name, [string] $Crash = '') {
    $State.workerCount = [int] $State.workerCount + 1
    $log = Join-Path $State.runDir ('{0:00}-{1}.log' -f $State.workerCount, $Name)
    $env:PRAGUE_BOOKING_DATA = $Data
    $env:PRAGUE_BOOKING_CRASH = $Crash
    try {
        $process = Start-Process dotnet -ArgumentList @("`"$Dll`"", 'worker') -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    }
    finally {
        $env:PRAGUE_BOOKING_CRASH = $null
    }
    $State.workers = @($State.workers) + @(@{
        pid = $process.Id
        startTicks = $process.StartTime.ToUniversalTime().Ticks
        name = $Name
    })
    Save-State $State
    return [pscustomobject] @{ Process = $process; Log = $log; Name = $Name }
}

function Remove-WorkerRecord([hashtable] $State, [int] $ProcessId) {
    $State.workers = @(@($State.workers) | Where-Object { $_.pid -ne $ProcessId })
    Save-State $State
}

function Stop-Worker([hashtable] $State, $Worker) {
    if (-not $Worker.Process.HasExited) {
        Stop-Process -Id $Worker.Process.Id -Force
        $Worker.Process.WaitForExit()
    }
    Remove-WorkerRecord $State $Worker.Process.Id
}

function Stop-OwnedWorkers([hashtable] $State) {
    foreach ($record in @($State.workers)) {
        $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        # Only a process with the recorded PID *and* start time is ours; PIDs get reused.
        if ($null -ne $process -and $process.StartTime.ToUniversalTime().Ticks -eq [long] $record.startTicks) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
            Say "stopped worker $($process.Id) ($($record.name))" DarkGray
        }
    }
    $State.workers = @()
    Save-State $State
}

# Returns the complete lines added to a log since the last call; a partial last line waits for the next call.
function Read-NewLines([string] $Path, [ref] $Offset) {
    if (-not (Test-Path $Path)) {
        return @()
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -le $Offset.Value) {
            return @()
        }
        [void] $stream.Seek($Offset.Value, [IO.SeekOrigin]::Begin)
        $buffer = [byte[]]::new($stream.Length - $Offset.Value)
        $read = $stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }
    $end = [Array]::LastIndexOf($buffer, [byte] 10, $read - 1)
    if ($end -lt 0) {
        return @()
    }
    $Offset.Value += $end + 1
    $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $end + 1)
    return @($text -split "`r?`n" | Where-Object { $_ })
}

# Turns a worker log line into the short line the audience sees; other lines are ignored.
function Show-WorkerLine([string] $Line) {
    switch -Regex ($Line) {
        "'(\w+)' orchestration with ID '[^']+' started" {
            Say ('worker   {0} running' -f $Matches[1]) White
            return
        }
        "'(\w+)' activity of orchestration ID '[^']+' started" {
            Say ('worker   {0,-11} started' -f $Matches[1]) DarkYellow
            return
        }
        "'(\w+)' activity of orchestration ID '[^']+' completed" {
            Say ('worker   {0,-11} done' -f $Matches[1]) Green
            return
        }
        "'(\w+)' orchestration with ID '[^']+' completed" {
            Say ('worker   {0} completed' -f $Matches[1]) Green
            return
        }
        'CONTROLLED CRASH: ([\w-]+); owned PID=(\d+)' {
            Say ('worker   CRASH: process {0} died ({1})' -f $Matches[2], $Matches[1]) Red
            return
        }
    }
}

# Shows the worker's log as it grows, until $Done says the step is over ($true) or the timeout passes ($false).
function Watch-Worker($Worker, [scriptblock] $Done, [int] $TimeoutSeconds, [string] $Waiting) {
    $offset = [long] 0
    $lines = [Collections.Generic.List[string]]::new()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $notice = [DateTime]::UtcNow.AddSeconds(8)
    while ($true) {
        $finished = [bool] (& $Done $Worker $lines)
        foreach ($line in (Read-NewLines $Worker.Log ([ref] $offset))) {
            $lines.Add($line)
            Show-WorkerLine $line
        }
        if ($finished -or [bool] (& $Done $Worker $lines)) {
            return $true
        }
        if ([DateTime]::UtcNow -gt $deadline) {
            return $false
        }
        if ([DateTime]::UtcNow -gt $notice) {
            Say $Waiting DarkGray
            $notice = [DateTime]::UtcNow.AddSeconds(8)
        }
        Start-Sleep -Milliseconds 200
    }
}

# A worker is ready once its log shows the streaming connection to the scheduler.
function Wait-Connected($Worker) {
    $connected = Watch-Worker $Worker {
        param($w, $lines)
        $w.Process.HasExited -or @($lines -match 'streaming connection established').Count -gt 0
    } 30 'worker connecting...'
    if (-not $connected -or $Worker.Process.HasExited) {
        throw "Worker $($Worker.Name) did not connect. Log: $($Worker.Log)"
    }
    Say "worker   process $($Worker.Process.Id) connected to the scheduler" DarkGray
}

# ---------- what the audience sees ----------

# Bookings and the audit log are read straight from the fake provider's files.
function Get-Bookings([string] $Data) {
    return @(Get-ChildItem -Path $Data -Filter 'booking-*.json' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
}

function Get-Audit([string] $Data) {
    $path = Join-Path $Data 'audit.jsonl'
    if (-not (Test-Path $path)) {
        return @()
    }
    return @(Get-Content $path | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

# Prints the booked count per service (green for one, red for duplicates) and returns the counts.
function Show-Bookings([string] $Data) {
    $bookings = Get-Bookings $Data
    $counts = @{}
    Write-Host ''
    Write-Host '  booked:  ' -ForegroundColor White -NoNewline
    foreach ($service in 'flight', 'hotel', 'car') {
        $count = @($bookings | Where-Object Service -eq $service).Count
        $counts[$service] = $count
        $color = switch ($count) {
            0 {
                'DarkYellow'
            }
            1 {
                'Green'
            }
            default {
                'Red'
            }
        }
        Write-Host ('  {0} {1} ' -f $service, $count) -ForegroundColor $color -NoNewline
    }
    Write-Host ''
    return $counts
}

function Test-Counts([hashtable] $Counts, [int] $Flight, [int] $Hotel, [int] $Car) {
    return $Counts.flight -eq $Flight -and $Counts.hotel -eq $Hotel -and $Counts.car -eq $Car
}

# Reads the scheduler's history and shows which activities already have a saved result.
function Show-History([string] $Id) {
    $events = @(Invoke-Demo $IntroDll @('history', $Id))
    $completed = @{}
    foreach ($event in $events) {
        if ($event.EventType -eq 'TaskCompleted') {
            $completed[[int] $event.TaskScheduledId] = $true
        }
    }
    $saved = 0
    $finished = $false
    Write-Host ''
    Say 'history (kept by the scheduler):' Cyan
    foreach ($event in $events) {
        if ($event.EventType -eq 'TaskScheduled') {
            if ($completed.ContainsKey([int] $event.EventId)) {
                $saved++
                Say ('  {0,-11} result saved' -f $event.Name) Green
            }
            else {
                Say ('  {0,-11} scheduled, no result' -f $event.Name) DarkYellow
            }
        }
        elseif ($event.EventType -eq 'ExecutionCompleted') {
            $finished = $true
            Say '  trip        completed' Green
        }
    }
    return @{ saved = $saved; finished = $finished }
}

# Every provider call from the audit log: a new booking, or a repeated key that returned the existing booking.
function Show-Attempts([string] $Data, [string] $Service = '') {
    $attempts = @(Get-Audit $Data | Where-Object {
            $_.kind -eq 'booking-attempt' -and (-not $Service -or $_.details.Service -eq $Service)
        })
    Write-Host ''
    Say 'provider log:' Cyan
    foreach ($attempt in $attempts) {
        $what = $attempt.details.created ? 'new booking' : 'same key, returned the existing booking'
        Say ('  process {0,-6} {1,-7} {2}' -f $attempt.pid, $attempt.details.Service, $what) ($attempt.details.created ? 'Green' : 'Yellow')
    }
    return $attempts
}

# Scripted model calls, counted from the audit log.
function Get-ModelCalls([string] $Data) {
    return @(Get-Audit $Data | Where-Object kind -eq 'fixture-model-call').Count
}

# Polls the agents workflow until $Until is true; returns $null on timeout and throws if the workflow failed.
function Wait-Agents([string] $Id, [scriptblock] $Until, [int] $Seconds, [string] $Waiting) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    $notice = [DateTime]::UtcNow.AddSeconds(8)
    while ($true) {
        $status = Invoke-Demo $AgentsDll @('status', $Id)
        if ($status.runtimeStatus -eq 'Failed') {
            throw "Workflow $Id failed: $($status.failure)"
        }
        if ([bool] (& $Until $status)) {
            return $status
        }
        if ([DateTime]::UtcNow -gt $deadline) {
            return $null
        }
        if ([DateTime]::UtcNow -gt $notice) {
            Say $Waiting DarkGray
            $notice = [DateTime]::UtcNow.AddSeconds(8)
        }
        Start-Sleep -Milliseconds 300
    }
}

# Strips the fixture labels, so the projector shows just the plan.
function Format-ModelText([string] $Text) {
    return (($Text -replace '^OFFLINE SCRIPTED [A-Z ]+:\s*', '') -replace ';\s*MOCK providers only\.?$', '.') -replace '\bMOCK\s+', ''
}

# Seconds the emulator's clock is ahead of this machine's (negative: behind), or $null if it can't be read.
function Get-ClockSkew([string] $ContainerId) {
    # Docker Desktop's VM clock can drift from Windows' (often after sleep). The scheduler stamps
    # itineraries with the VM clock, while approvals are stamped by this machine.
    $before = [DateTimeOffset]::UtcNow
    $vm = (& docker exec $ContainerId date -u +%s.%N 2>$null | Out-String).Trim()
    $after = [DateTimeOffset]::UtcNow
    if ($LASTEXITCODE -ne 0 -or $vm -notmatch '^\d+(\.\d+)?$') {
        return $null
    }
    return [double] $vm - ($before.ToUnixTimeMilliseconds() + $after.ToUnixTimeMilliseconds()) / 2000.0
}

function Show-ClockSkew($Skew) {
    if ($null -eq $Skew) {
        Say 'Could not read the emulator clock; skipping the clock check.' DarkGray
        return
    }
    if ([Math]::Abs($Skew) -le 2) {
        Say ('Docker clock is in sync ({0:+0.0;-0.0} s).' -f $Skew) DarkGray
        return
    }
    Say ('Docker clock is {0:N1} s {1} this machine.' -f [Math]::Abs($Skew), ($Skew -gt 0 ? 'ahead of' : 'behind')) Yellow
    Say 'The agents demo waits it out before approving, but fix it before the talk:' Yellow
    Say '  .\Stage.ps1 cleanup; wsl --shutdown; restart Docker Desktop; .\Stage.ps1 setup' Yellow
}

function Wait-ClockCatchUp($Proposal) {
    # The approval receipt must not look older than the itinerary it approves.
    $created = ([datetime] $Proposal.CreatedUtc).ToUniversalTime()
    $wait = [Math]::Ceiling(($created - [DateTime]::UtcNow).TotalSeconds + 1)
    if ($wait -le 0) {
        return
    }
    if ($wait -gt 120) {
        throw "Docker's clock is $wait s ahead. Run cleanup, then wsl --shutdown, restart Docker Desktop, and run setup again."
    }
    Say "waiting $wait s for this machine's clock to pass the itinerary time (Docker clock drift)..." DarkGray
    Start-Sleep -Seconds $wait
}

# ---------- acts ----------

# setup: builds the demos if needed, starts a fresh emulator container, waits for its dashboard, checks its
# clock and warms up the CLIs. If a setup is already running, it just shows the status.
function Invoke-Setup {
    $existing = Get-State
    if ($null -ne $existing -and (Test-ContainerRunning $existing.containerId)) {
        Say "Already set up (run $($existing.run))." Green
        Show-Status $existing
        return $existing
    }
    if ($null -ne $existing) {
        Say 'Found state from an earlier run; cleaning it up first.' DarkGray
        Invoke-Cleanup $existing
    }
    Title 'Stage setup'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET 10 SDK not found.'
    }
    if (-not (Test-Path $IntroDll) -or -not (Test-Path $AgentsDll)) {
        Say 'building the demos...' DarkGray
        & dotnet build (Join-Path $Root 'PragueDurableTask.sln') -nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw 'Build failed.'
        }
    }
    $null = & docker info --format '{{.ServerVersion}}' 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker is not running. Start Docker Desktop (Linux containers).'
    }
    $null = & docker image inspect $EmulatorImage --format '{{.Id}}' 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Emulator image is not on this machine. Before the talk run: docker pull $EmulatorImage"
    }
    # Fail early if a port is taken, before starting anything.
    foreach ($port in $GrpcPort, $DashboardPort) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        try {
            $listener.Start()
        }
        catch {
            throw "Port $port is in use. Choose others with -GrpcPort / -DashboardPort."
        }
        finally {
            $listener.Stop()
        }
    }

    $run = Get-Date -Format 'yyyyMMdd-HHmmss'
    $runDir = Join-Path $Root "artifacts\stage-$run"
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    $containerName = "prague-stage-$run"
    $containerId = (& docker run --pull never --name $containerName -d `
            -p "127.0.0.1:${GrpcPort}:8080" -p "127.0.0.1:${DashboardPort}:8082" $EmulatorImage | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $containerId) {
        throw 'Could not start the emulator container.'
    }
    $state = @{
        run = $run; runDir = $runDir; containerId = $containerId; containerName = $containerName
        grpcPort = $GrpcPort; dashboardPort = $DashboardPort; counter = 0; workerCount = 0; workers = @()
    }
    Save-State $state
    Say "emulator container $containerName started" DarkGray

    # The emulator is ready when its dashboard answers.
    $http = [Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    $ready = $false
    try {
        for ($i = 0; $i -lt 40 -and -not $ready; $i++) {
            try {
                $response = $http.GetAsync("http://127.0.0.1:$DashboardPort/").GetAwaiter().GetResult()
                $ready = $response.IsSuccessStatusCode
                $response.Dispose()
            }
            catch {
                # Not listening yet; try again.
            }
            if (-not $ready) {
                Start-Sleep -Seconds 1
            }
        }
    }
    finally {
        $http.Dispose()
    }
    if (-not $ready) {
        throw 'The emulator dashboard did not respond. Run .\Stage.ps1 cleanup and try again.'
    }
    $state.clockSkew = Get-ClockSkew $containerId
    Save-State $state
    Show-ClockSkew $state.clockSkew

    # Warm the .NET CLIs so the first demo step on stage starts fast.
    $env:PRAGUE_BOOKING_DATA = Join-Path $runDir 'warmup'
    $env:PRAGUE_DTS_ENDPOINT = "http://localhost:$GrpcPort"
    $null = & dotnet $IntroDll ledger
    $null = & dotnet $AgentsDll status warmup-check 2>$null

    Say "Ready. Scheduler emulator: localhost:$GrpcPort   Dashboard: http://localhost:$DashboardPort" Green
    Say 'Acts: naive | crash | ackgap | agents     Afterwards: cleanup' DarkGray
    if ($OpenDashboard) {
        Start-Process "http://localhost:$DashboardPort"
    }
    return $state
}

# naive: three plain awaits, no scheduler. The run dies before the car, and the rerun books the flight
# and the hotel again.
function Invoke-Naive([hashtable] $State) {
    Assert-Built
    $data = Join-Path $State.runDir ('naive-{0}' -f (Get-Date -Format 'HHmmss'))
    $env:PRAGUE_BOOKING_DATA = $data
    Title 'Three plain awaits: book a flight, a hotel and a car'
    Next 'Run it. The process will die right before the car.'
    $env:PRAGUE_BOOKING_CRASH = 'before-car'
    try {
        $output = & dotnet $IntroDll naive seattle-trip 2>&1 | Out-String
    }
    finally {
        $env:PRAGUE_BOOKING_CRASH = $null
    }
    if ($output -match 'owned PID=(\d+)') {
        Say "CRASH: process $($Matches[1]) died before booking the car" Red
    }
    $counts = Show-Bookings $data
    Expect (Test-Counts $counts 1 1 0) 'expected flight 1, hotel 1, car 0 after the crash'

    Next 'Run the same code again.'
    $null = & dotnet $IntroDll naive seattle-trip 2>&1 | Out-String
    Expect ($LASTEXITCODE -eq 0) 'the second run failed'
    $counts = Show-Bookings $data
    Expect (Test-Counts $counts 2 2 1) 'expected flight 2, hotel 2, car 1 after the rerun'
    Write-Host ''
    Say 'Two flights. Two hotels. Nothing remembered what already happened.' Red
}

# crash: the durable worker dies before the car. A new worker replays the history and books only the car.
function Invoke-Crash([hashtable] $State) {
    Assert-Built
    $id = New-InstanceId $State 'trip'
    $data = Join-Path $State.runDir $id
    Title "Durable trip '$id': pull the plug between hotel and car"
    Next 'Start a worker. It will die right before it books the car.'
    $first = Start-Worker $State $IntroDll $data 'crash-before-car' 'before-car'
    Wait-Connected $first

    Next "Book trip '$id'."
    $env:PRAGUE_BOOKING_DATA = $data
    $null = Invoke-Demo $IntroDll @('start', $id)
    Say "client   started '$id'" White
    $crashed = Watch-Worker $first {
        param($w, $lines)
        $w.Process.HasExited
    } 60 'still booking...'
    Expect $crashed 'the worker did not reach the crash point'
    Remove-WorkerRecord $State $first.Process.Id
    $counts = Show-Bookings $data
    Expect (Test-Counts $counts 1 1 0) 'expected flight 1, hotel 1, car 0 at the crash'
    $history = Show-History $id
    Expect ($history.saved -eq 2) 'expected two saved results (flight and hotel) in the history'

    Next 'Start a new worker. Same trip, same history.'
    $second = Start-Worker $State $IntroDll $data 'resume'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $done = Watch-Worker $second {
        param($w, $lines)
        @($lines -match "orchestration with ID '$id' completed").Count -gt 0
    } 120 'the scheduler is handing the unfinished car booking to the new worker...'
    if (-not $done) {
        $null = Invoke-Demo $IntroDll @('wait', $id)
    }
    Say ('resumed and finished in {0:N0} s' -f $timer.Elapsed.TotalSeconds) DarkGray
    $status = Invoke-Demo $IntroDll @('status', $id)
    Expect ($status.runtimeStatus -eq 'Completed') "trip '$id' did not complete"
    $counts = Show-Bookings $data
    Expect (Test-Counts $counts 1 1 1) 'expected flight 1, hotel 1, car 1 after the restart'
    $attempts = @(Show-Attempts $data)
    Expect ($attempts.Count -eq 3) 'expected exactly three provider calls'
    $history = Show-History $id
    Expect $history.finished 'the history does not show a completed trip'
    Write-Host ''
    Say "Same instance '$id'. Flight and hotel came back from history; only the car was booked." Green
    Say "Dashboard: http://localhost:$($State.dashboardPort)  (task hub 'default', instance '$id')" DarkGray
    Stop-Worker $State $second
}

# ackgap: the hotel is booked, but the worker dies before reporting back. The hotel activity runs again,
# and the provider's operation ID keeps it to one hotel.
function Invoke-AckGap([hashtable] $State) {
    Assert-Built
    $id = New-InstanceId $State 'trip'
    $data = Join-Path $State.runDir $id
    Title "Durable trip '$id': the hotel is booked, then the worker dies before reporting back"
    Next 'Start a worker. It will die right after the hotel booking.'
    $first = Start-Worker $State $IntroDll $data 'crash-after-hotel' 'after-hotel-effect'
    Wait-Connected $first

    Next "Book trip '$id'."
    $env:PRAGUE_BOOKING_DATA = $data
    $null = Invoke-Demo $IntroDll @('start', $id)
    Say "client   started '$id'" White
    $crashed = Watch-Worker $first {
        param($w, $lines)
        $w.Process.HasExited
    } 60 'still booking...'
    Expect $crashed 'the worker did not reach the crash point'
    Remove-WorkerRecord $State $first.Process.Id
    $counts = Show-Bookings $data
    Expect (Test-Counts $counts 1 1 0) 'expected flight 1, hotel 1, car 0 at the crash'
    $history = Show-History $id
    Expect ($history.saved -eq 1) 'expected only the flight result in the history'
    Say 'The provider booked the hotel. The scheduler never heard back.' Yellow

    Next 'Start a new worker. The hotel call will run again.'
    $second = Start-Worker $State $IntroDll $data 'resume'
    $done = Watch-Worker $second {
        param($w, $lines)
        @($lines -match "orchestration with ID '$id' completed").Count -gt 0
    } 120 'the scheduler is handing the unacknowledged hotel booking to the new worker...'
    if (-not $done) {
        $null = Invoke-Demo $IntroDll @('wait', $id)
    }
    $hotel = @(Show-Attempts $data 'hotel')
    Expect ($hotel.Count -eq 2 -and @($hotel | Where-Object { $_.details.created }).Count -eq 1) 'expected two hotel attempts and one hotel booking'
    $counts = Show-Bookings $data
    Expect (Test-Counts $counts 1 1 1) 'expected flight 1, hotel 1, car 1 after the restart'
    Write-Host ''
    Say "Two hotel attempts, one hotel: the provider deduplicated key '$id/hotel'." Green
    Stop-Worker $State $second
}

# agents: two agents plan, the worker is killed, a fake approval is rejected, then the traveler approves
# and the trip is booked. The models must not be called again after the restart.
function Invoke-Agents([hashtable] $State) {
    Assert-Built
    # The agents worker registers a different set of tasks on the same task hub.
    Stop-OwnedWorkers $State
    $id = New-InstanceId $State 'agents'
    $data = Join-Path $State.runDir $id
    Title "Durable agents '$id': plan, crash, approve, book"
    $first = Start-Worker $State $AgentsDll $data 'agents'
    Start-Sleep -Seconds 2
    if ($first.Process.HasExited) {
        throw "The agents worker exited. Log: $($first.Log)"
    }
    Say "worker   process $($first.Process.Id) started (TripPlanner + BudgetReviewer agents)" DarkGray

    Next 'Ask the agents to plan the trip.'
    $env:PRAGUE_BOOKING_DATA = $data
    $null = Invoke-Demo $AgentsDll @('start', $id, "$ApprovalSeconds")
    $before = Wait-Agents $id {
        param($s)
        $null -ne $s.status -and $s.status.Phase -eq 'AwaitingApproval'
    } 90 'agents are planning...'
    if ($null -eq $before) {
        throw "'$id' never reached AwaitingApproval."
    }
    $proposal = $before.status.Proposal
    Write-Host ''
    Say ('TripPlanner     {0}' -f (Format-ModelText $proposal.PlannerOutput)) White
    Say ('BudgetReviewer  {0}' -f (Format-ModelText $proposal.ReviewerOutput)) White
    $calls = Get-ModelCalls $data
    Write-Host ''
    Say ('model calls {0}   bookings {1}   waiting for the traveler until {2:HH:mm}' -f `
            $calls, @(Get-Bookings $data).Count, ([datetime] $proposal.DeadlineUtc).ToLocalTime()) Cyan
    Expect ($calls -eq 2) 'expected two model calls (planner and reviewer)'

    Next 'Kill the worker.'
    Stop-Worker $State $first
    Say "CRASH: worker process $($first.Process.Id) killed" Red

    Next 'Start a new worker.'
    $second = Start-Worker $State $AgentsDll $data 'agents-restart'
    Start-Sleep -Seconds 2
    if ($second.Process.HasExited) {
        throw "The agents worker exited. Log: $($second.Log)"
    }
    Say "worker   process $($second.Process.Id) started" DarkGray

    Next 'Send an approval event with no real receipt behind it.'
    $null = Invoke-Demo $AgentsDll @('signal', $id, [Guid]::NewGuid().ToString('N'))
    $after = Wait-Agents $id {
        param($s)
        $null -ne $s.status -and $null -ne $s.status.Detail
    } 90 'the new worker is replaying the workflow...'
    if ($null -eq $after) {
        throw "'$id' did not process the event."
    }
    Say "rejected: $($after.status.Detail)" Red
    $calls = Get-ModelCalls $data
    Say "model calls $calls   (planner and reviewer results came back from history)" Green
    Expect ($calls -eq 2) 'the models were called again after the restart'
    Expect ($after.status.Proposal.Digest -eq $proposal.Digest) 'the itinerary changed after the restart'

    Next 'Approve as the traveler.'
    Wait-ClockCatchUp $proposal
    $null = Invoke-Demo $AgentsDll @('approve', $id)
    $final = Invoke-Demo $AgentsDll @('wait', $id)
    Say "status: $($final.status.Phase)" ($final.status.Phase -eq 'Booked' ? 'Green' : 'Red')
    $counts = Show-Bookings $data
    Expect ($final.status.Phase -eq 'Booked' -and (Test-Counts $counts 1 1 1)) 'expected one flight, hotel and car after approval'
    Say "model calls $(Get-ModelCalls $data)" DarkGray
    Stop-Worker $State $second
}

# status: the emulator, the dashboard, the run folder and the workers this session started.
function Show-Status($State) {
    if ($null -eq $State) {
        Say 'Not set up. Run: .\Stage.ps1 setup' Yellow
        return
    }
    $running = Test-ContainerRunning $State.containerId
    Say ("emulator   {0}  ({1})" -f ($running ? 'running' : 'NOT running'), $State.containerName) ($running ? 'Green' : 'Red')
    if ($running) {
        Show-ClockSkew (Get-ClockSkew $State.containerId)
    }
    Say "dashboard  http://localhost:$($State.dashboardPort)" Gray
    Say "artifacts  $($State.runDir)" Gray
    foreach ($record in @($State.workers)) {
        $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        $alive = $null -ne $process -and $process.StartTime.ToUniversalTime().Ticks -eq [long] $record.startTicks
        Say ("worker     {0} {1} ({2})" -f $record.pid, ($alive ? 'running' : 'gone'), $record.name) DarkGray
    }
}

# cleanup: stops the workers this script started and removes the emulator container; the artifacts stay.
function Invoke-Cleanup($State) {
    if ($null -eq $State) {
        Say 'Nothing to clean up.' DarkGray
        return
    }
    Stop-OwnedWorkers $State
    if ($State.containerId) {
        & docker rm -f $State.containerId 2>$null | Out-Null
        Say "removed emulator container $($State.containerName)" DarkGray
    }
    Remove-Item -Path $StatePath -Force -ErrorAction SilentlyContinue
    Say "Artifacts kept in $($State.runDir)" DarkGray
}

# rehearse: every act without pauses on a fresh emulator, then cleanup and a timing summary.
function Invoke-Rehearse {
    if ($null -ne (Get-State)) {
        throw 'A stage session is already set up. Rehearse before setup, or run .\Stage.ps1 cleanup first.'
    }
    $null = Invoke-Setup
    $times = [ordered] @{}
    try {
        foreach ($name in 'naive', 'crash', 'ackgap', 'agents') {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            switch ($name) {
                'naive' {
                    Invoke-Naive (Use-State -WithoutEmulator)
                }
                'crash' {
                    Invoke-Crash (Use-State)
                }
                'ackgap' {
                    Invoke-AckGap (Use-State)
                }
                'agents' {
                    Invoke-Agents (Use-State)
                }
            }
            $times[$name] = $timer.Elapsed.TotalSeconds
        }
    }
    finally {
        Invoke-Cleanup (Get-State)
    }
    Title 'Rehearsal summary (machine time only, without talking)'
    foreach ($entry in $times.GetEnumerator()) {
        Say ('{0,-7} {1,5:N0} s' -f $entry.Key, $entry.Value) Gray
    }
}

# ---------- main ----------

try {
    switch ($Act) {
        'setup' {
            $null = Invoke-Setup
        }
        'naive' {
            Invoke-Naive (Use-State -WithoutEmulator)
        }
        'crash' {
            Invoke-Crash (Use-State)
        }
        'ackgap' {
            Invoke-AckGap (Use-State)
        }
        'agents' {
            Invoke-Agents (Use-State)
        }
        'status' {
            Show-Status (Get-State)
        }
        'cleanup' {
            Invoke-Cleanup (Get-State)
        }
        'rehearse' {
            Invoke-Rehearse
        }
    }
}
finally {
    foreach ($name in $savedEnv.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnv[$name])
    }
}

# Any unexpected result makes the script exit with code 1 (rehearse relies on this).
if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Say "$($script:Failures.Count) unexpected result(s):" Red
    foreach ($failure in $script:Failures) {
        Say "- $failure" Red
    }
    exit 1
}
