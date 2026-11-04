[CmdletBinding()]
param(
    [int] $GrpcPort = 18080,
    [int] $DashboardPort = 18082,
    [string] $EmulatorImage = 'mcr.microsoft.com/dts/dts-emulator@sha256:361323065a608d605f9d3ae56b854eb11f1c47fcc16845a37f948f03ca9c5fac'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
$previous = @{ data = $env:PRAGUE_BOOKING_DATA; endpoint = $env:PRAGUE_DTS_ENDPOINT; crash = $env:PRAGUE_BOOKING_CRASH }
$run = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$artifactPath = Join-Path $PSScriptRoot "artifacts\booking-rehearsal-$run"
$dataPath = Join-Path $artifactPath 'data'
$introDll = Join-Path $PSScriptRoot 'BookingIntro\bin\Debug\net10.0\BookingIntro.dll'
$agentsDll = Join-Path $PSScriptRoot 'BookingAgents\bin\Debug\net10.0\BookingAgents.dll'
$script:Worker = $null
$script:WorkerNumber = 0
$baseline = $null
$containerId = $null
$cases = [Collections.Generic.List[object]]::new()
$script:Crashes = [Collections.Generic.List[object]]::new()

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Invoke-Booking([string] $Dll, [string[]] $DemoArgs) {
    $text = & dotnet $Dll @DemoArgs | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $($DemoArgs -join ' '): $text" }
    return ($text | ConvertFrom-Json)
}
function Start-Worker([string] $Dll, [string] $Crash = '') {
    $script:WorkerNumber++
    $env:PRAGUE_BOOKING_CRASH = $Crash
    try {
        $script:Worker = Start-Process dotnet -ArgumentList @("`"$Dll`"", 'worker') -PassThru `
            -RedirectStandardOutput (Join-Path $artifactPath "worker-$($script:WorkerNumber).stdout.log") `
            -RedirectStandardError (Join-Path $artifactPath "worker-$($script:WorkerNumber).stderr.log")
    } finally { $env:PRAGUE_BOOKING_CRASH = $null }
    Start-Sleep -Seconds 2
    Assert-True (-not $script:Worker.HasExited) 'Owned booking worker exited during startup.'
}
function Stop-Worker {
    if ($null -ne $script:Worker -and -not $script:Worker.HasExited) {
        Stop-Process -Id $script:Worker.Id -Force
        $script:Worker.WaitForExit()
    }
}
function Wait-ControlledCrash([string] $Checkpoint) {
    $ownedPid = $script:Worker.Id
    Assert-True ($script:Worker.WaitForExit(60000)) "No controlled worker crash at $Checkpoint."
    Assert-True ($script:Worker.ExitCode -ne 0) 'Crash hook did not kill the worker.'
    $script:Crashes.Add([ordered]@{ checkpoint = $Checkpoint; killedWorkerPid = $ownedPid; mechanism = 'self-PID kill'; utc = [DateTime]::UtcNow })
}
function Read-Audit {
    $path = Join-Path $dataPath 'audit.jsonl'
    if (Test-Path $path) { Get-Content $path | ForEach-Object { $_ | ConvertFrom-Json } }
}
function Bookings-For([string] $TripId) {
    @(Get-ChildItem $dataPath -Filter 'booking-*.json' | ForEach-Object {
        Get-Content $_.FullName -Raw | ConvertFrom-Json
    } | Where-Object TripId -eq $TripId)
}
function Assert-Boundary([string] $TripId) {
    $bookings = @(Bookings-For $TripId)
    Assert-True (@($bookings | Where-Object Service -eq 'flight').Count -eq 1) 'Expected flight=1 at crash.'
    Assert-True (@($bookings | Where-Object Service -eq 'hotel').Count -eq 1) 'Expected hotel=1 at crash.'
    Assert-True (@($bookings | Where-Object Service -eq 'car').Count -eq 0) 'Expected car=0 at crash.'
}
function Wait-Phase([string] $Id, [string] $Phase, [int] $Seconds = 90) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $status = Invoke-Booking $agentsDll @('status', $Id)
        if ($status.runtimeStatus -eq 'Failed') { throw "Workflow $Id failed: $($status.failure)" }
        if ($null -ne $status.status -and $status.status.Phase -eq $Phase) { return $status }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $until)
    throw "Workflow $Id did not reach phase $Phase."
}
function Export-Instance([string] $Dll, [string] $Id, [string] $Suffix = '') {
    $status = Invoke-Booking $Dll @('status', $Id)
    $status | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $artifactPath "$Id$Suffix.status.json")
    $history = Invoke-Booking $Dll @('history', $Id)
    $history | ConvertTo-Json -Depth 60 | Set-Content (Join-Path $artifactPath "$Id$Suffix.history.json")
    Assert-True (@($history).Count -gt 3) "Real Scheduler history missing for $Id."
    return $history
}
function Wait-ClockCatchUp($Status) {
    # Itineraries are stamped with the emulator's (Docker VM) clock and receipts with this machine's.
    # Docker Desktop's clock can run ahead; a receipt must not look older than its itinerary.
    $created = ([datetime] $Status.status.Proposal.CreatedUtc).ToUniversalTime()
    $wait = [Math]::Ceiling(($created - [DateTime]::UtcNow).TotalSeconds + 1)
    if ($wait -gt 120) { throw "Docker's clock is $wait s ahead of this machine. Run wsl --shutdown, restart Docker Desktop and retry." }
    if ($wait -gt 0) { Start-Sleep -Seconds $wait }
}

try {
    Assert-True ((Test-Path $introDll) -and (Test-Path $agentsDll)) 'Build the solution before rehearsing.'
    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
    $env:PRAGUE_BOOKING_DATA = $dataPath
    $env:PRAGUE_DTS_ENDPOINT = "http://localhost:$GrpcPort"
    $env:PRAGUE_BOOKING_CRASH = $null

    # Ordinary async baseline: provider receipts survive, but the next process starts a new attempt.
    $baselineData = Join-Path $artifactPath 'baseline-data'
    $env:PRAGUE_BOOKING_DATA = $baselineData
    $env:PRAGUE_BOOKING_CRASH = 'before-car'
    $baseline = Start-Process dotnet -ArgumentList @("`"$introDll`"", 'naive', 'naive-seattle') -PassThru `
        -RedirectStandardOutput (Join-Path $artifactPath 'naive-crash.stdout.log') `
        -RedirectStandardError (Join-Path $artifactPath 'naive-crash.stderr.log')
    $env:PRAGUE_BOOKING_CRASH = $null
    Assert-True ($baseline.WaitForExit(30000)) 'Naive baseline did not reach controlled crash.'
    Assert-True ($baseline.ExitCode -ne 0) 'Naive baseline was not killed.'
    $baselineBefore = Invoke-Booking $introDll @('ledger')
    $baselineBefore | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $artifactPath 'naive-before-restart.json')
    Assert-True (@($baselineBefore | Where-Object Service -eq 'flight').Count -eq 1) 'Naive flight count is not 1.'
    Assert-True (@($baselineBefore | Where-Object Service -eq 'hotel').Count -eq 1) 'Naive hotel count is not 1.'
    Assert-True (@($baselineBefore | Where-Object Service -eq 'car').Count -eq 0) 'Naive car count is not 0.'
    & dotnet $introDll naive naive-seattle | Set-Content (Join-Path $artifactPath 'naive-rerun.stdout.log')
    Assert-True ($LASTEXITCODE -eq 0) 'Naive fresh attempt failed.'
    $baselineAfter = Invoke-Booking $introDll @('ledger')
    $baselineAfter | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $artifactPath 'naive-after-restart.json')
    Assert-True (@($baselineAfter).Count -eq 5) 'Expected two flights, two hotels and one car on naive fresh-attempt rerun.'
    $cases.Add([ordered]@{ case = 'naive-three-await-crash-fresh-attempt'; realScheduler = $false; passed = $true;
        crashCounts = @{ flight = 1; hotel = 1; car = 0 }; rerunCounts = @{ flight = 2; hotel = 2; car = 1 };
        killedPid = $baseline.Id; caveat = 'This naive method chooses new operation IDs on restart; stable business keys plus manual recovery could prevent duplicates.' })
    $env:PRAGUE_BOOKING_DATA = $dataPath

    & docker image inspect $EmulatorImage --format '{{.Id}}' | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Pinned emulator image unavailable locally; no registry pull attempted.'
    $containerId = (& docker run --pull never --name "prague-booking-$run" -d `
        -p "127.0.0.1:${GrpcPort}:8080" -p "127.0.0.1:${DashboardPort}:8082" $EmulatorImage | Out-String).Trim()
    Assert-True ($LASTEXITCODE -eq 0) 'Could not start task-owned emulator.'
    $http = [System.Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    $ready = $false
    try {
        for ($i = 0; $i -lt 30; $i++) {
            try {
                $response = $http.GetAsync("http://127.0.0.1:$DashboardPort").GetAwaiter().GetResult()
                $ready = $response.IsSuccessStatusCode
                $response.Dispose()
                if ($ready) { break }
            } catch { }
            Start-Sleep -Seconds 1
        }
    } finally { $http.Dispose() }
    Assert-True $ready 'Emulator dashboard did not become responsive.'

    $introId = "booking-intro-$run"
    Start-Worker $introDll 'before-car'
    Invoke-Booking $introDll @('start', $introId) | Out-Null
    Wait-ControlledCrash 'flight=1 hotel=1 car=0; car activity held before MOCK reservation'
    Assert-Boundary $introId
    $beforeHistory = Export-Instance $introDll $introId '-before-restart'
    Assert-True (@($beforeHistory | Where-Object EventType -eq 'TaskCompleted').Count -eq 2) 'Flight/hotel successful outcomes were not both recorded before car crash.'
    Start-Worker $introDll
    $intro = Invoke-Booking $introDll @('wait', $introId)
    Assert-True ($intro.runtimeStatus -eq 'Completed' -and @($intro.result.Bookings).Count -eq 3) 'Intro did not resume the same instance.'
    $attempts = @(Read-Audit | Where-Object { $_.kind -eq 'booking-attempt' -and $_.details.receipt.TripId -eq $introId })
    Assert-True ($attempts.Count -eq 3) 'Recorded flight/hotel were unnecessarily invoked again.'
    Export-Instance $introDll $introId | Out-Null
    $cases.Add([ordered]@{ case = 'plain-sdk-blog-boundary-crash-resume'; realScheduler = $true; passed = $true;
        instanceId = $introId; crashCounts = @{ flight = 1; hotel = 1; car = 0 }; finalCounts = @{ flight = 1; hotel = 1; car = 1 };
        recordedCompletionsBeforeCrash = 2; bookingAttemptsAfterRecovery = $attempts.Count })
    Stop-Worker

    $gapId = "booking-ack-gap-$run"
    Start-Worker $introDll 'after-hotel-effect'
    Invoke-Booking $introDll @('start', $gapId) | Out-Null
    Wait-ControlledCrash 'hotel MOCK reservation committed; hotel activity acknowledgement absent'
    Assert-Boundary $gapId
    $gapBefore = Export-Instance $introDll $gapId '-before-restart'
    Assert-True (@($gapBefore | Where-Object EventType -eq 'TaskCompleted').Count -eq 1) 'Expected only flight acknowledgement before hotel effect/ack gap.'
    Start-Worker $introDll
    $gap = Invoke-Booking $introDll @('wait', $gapId)
    Assert-True ($gap.runtimeStatus -eq 'Completed') 'Effect/ack-gap run did not recover.'
    $hotelAttempts = @(Read-Audit | Where-Object { $_.kind -eq 'booking-attempt' -and $_.details.OperationId -eq "$gapId/hotel" })
    Assert-True ($hotelAttempts.Count -eq 2) 'Expected two hotel activity attempts.'
    Assert-True (@($hotelAttempts | Where-Object { $_.details.created }).Count -eq 1) 'Duplicate hotel reservation was created.'
    Assert-True (@(Bookings-For $gapId).Count -eq 3) 'Expected one MOCK booking per service after effect-gap recovery.'
    Export-Instance $introDll $gapId | Out-Null
    $cases.Add([ordered]@{ case = 'hotel-effect-ack-gap-idempotent-redelivery'; realScheduler = $true; passed = $true;
        instanceId = $gapId; hotelAttempts = 2; actualHotelBookings = 1; sameOperationId = "$gapId/hotel" })
    Stop-Worker

    Start-Worker $agentsDll
    $agentsId = "booking-agents-$run"
    Invoke-Booking $agentsDll @('start', $agentsId, '300') | Out-Null
    $before = Wait-Phase $agentsId 'AwaitingApproval'
    $before | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $artifactPath 'agents-before-crash.json')
    Assert-True (@(Bookings-For $agentsId).Count -eq 0) 'Bookings occurred before approval.'
    $callsBefore = @(Read-Audit | Where-Object kind -eq 'fixture-model-call').Count
    Assert-True ($callsBefore -eq 2) 'Expected exactly two MAF specialist fixture calls.'
    $ownedPid = $script:Worker.Id
    Stop-Worker
    $script:Crashes.Add([ordered]@{ checkpoint = 'Both MAF specialist results and immutable itinerary recorded; AwaitingApproval';
        killedWorkerPid = $ownedPid; mechanism = 'Stop-Process -Id ownedPID'; utc = [DateTime]::UtcNow })
    Start-Worker $agentsDll
    Invoke-Booking $agentsDll @('signal', $agentsId, [Guid]::NewGuid().ToString('N')) | Out-Null
    $until = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $after = Wait-Phase $agentsId 'AwaitingApproval'
        if ($null -ne $after.status.Detail) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $until)
    Assert-True ($null -ne $after.status.Detail) 'Forged notification did not fail independent receipt validation.'
    Assert-True ($after.status.Proposal.Digest -eq $before.status.Proposal.Digest) 'Itinerary changed on restart.'
    Assert-True ($after.status.Proposal.DeadlineUtc -eq $before.status.Proposal.DeadlineUtc) 'Invalid notification reset the deadline.'
    $callsAfter = @(Read-Audit | Where-Object kind -eq 'fixture-model-call').Count
    Assert-True ($callsAfter -eq 2) 'Recorded MAF specialist result was recomputed.'
    $after | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $artifactPath 'agents-after-crash.json')
    Wait-ClockCatchUp $after
    $authorized = Invoke-Booking $agentsDll @('approve', $agentsId)
    $completed = Invoke-Booking $agentsDll @('wait', $agentsId)
    Assert-True ($completed.status.Phase -eq 'Booked' -and @($completed.result.Bookings).Count -eq 3) 'Approved agent itinerary did not book.'
    Export-Instance $agentsDll $agentsId | Out-Null
    $cases.Add([ordered]@{ case = 'maf-specialists-crash-immutable-itinerary-bound-approval'; realScheduler = $true; passed = $true;
        instanceId = $agentsId; fixtureCallsBefore = 2; fixtureCallsAfter = 2;
        proposalDigest = $before.status.Proposal.Digest; deadlineUtc = $before.status.Proposal.DeadlineUtc })

    $timeoutId = "booking-timeout-$run"
    Invoke-Booking $agentsDll @('start', $timeoutId, '6') | Out-Null
    $pending = Wait-Phase $timeoutId 'AwaitingApproval'
    Invoke-Booking $agentsDll @('signal', $timeoutId, [Guid]::NewGuid().ToString('N')) | Out-Null
    $timeout = Invoke-Booking $agentsDll @('wait', $timeoutId)
    Assert-True ($timeout.status.Phase -eq 'TimedOut') 'Fixed durable timeout failed.'
    Assert-True ($timeout.status.Proposal.DeadlineUtc -eq $pending.status.Proposal.DeadlineUtc) 'Timeout deadline moved.'
    Assert-True (@(Bookings-For $timeoutId).Count -eq 0) 'Timeout produced a booking.'
    Export-Instance $agentsDll $timeoutId | Out-Null
    $cases.Add([ordered]@{ case = 'approval-fixed-timeout-no-bookings'; realScheduler = $true; passed = $true; instanceId = $timeoutId })

    $deniedId = "booking-denied-$run"
    Invoke-Booking $agentsDll @('start', $deniedId, '90') | Out-Null
    Wait-Phase $deniedId 'AwaitingApproval' | Out-Null
    Invoke-Booking $agentsDll @('signal', $deniedId, $authorized.receipt.ReceiptId) | Out-Null
    $until = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $wrong = Wait-Phase $deniedId 'AwaitingApproval'
        if ($null -ne $wrong.status.Detail) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $until)
    Assert-True ($null -ne $wrong.status.Detail) 'Receipt for another itinerary was not rejected.'
    Wait-ClockCatchUp $wrong
    Invoke-Booking $agentsDll @('deny', $deniedId) | Out-Null
    $denied = Invoke-Booking $agentsDll @('wait', $deniedId)
    Assert-True ($denied.status.Phase -eq 'Denied') 'Denial was not respected.'
    Assert-True (@(Bookings-For $deniedId).Count -eq 0) 'Denial produced a booking.'
    Export-Instance $agentsDll $deniedId | Out-Null
    $cases.Add([ordered]@{ case = 'cross-itinerary-receipt-rejected-denial-no-bookings'; realScheduler = $true; passed = $true; instanceId = $deniedId })

    [ordered]@{
        passed = $true; utc = [DateTime]::UtcNow; emulatorImage = $EmulatorImage; containerId = $containerId;
        blog = 'https://www.tamirdresher.com/blog/2026/10/16/copilot-workflows-durability';
        adaptation = 'C# sequential flight/hotel/car reproduces the observed flight=1/hotel=1/car=0 boundary; not the parallel Copilot native runtime/resume protocol.';
        fixtures = 'LOCAL MOCK reservations; OFFLINE SCRIPTED MAF models; LOCAL FIXTURE authority';
        cases = $cases; workerCrashes = $script:Crashes; artifacts = $artifactPath;
        persistence = 'Real Scheduler histories exported before stopping the in-memory emulator. Worker crash only; no backend/container-loss durability claim.'
    } | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $artifactPath 'results.json')
    Write-Output "PASS: $($cases.Count) cases (1 naive + 5 real Scheduler); evidence: $artifactPath"
}
catch {
    [ordered]@{ passed = $false; error = $_.Exception.Message; cases = $cases; workerCrashes = $script:Crashes } |
        ConvertTo-Json -Depth 30 | Set-Content (Join-Path $artifactPath 'results.json')
    throw
}
finally {
    Stop-Worker
    if ($null -ne $baseline -and -not $baseline.HasExited) { Stop-Process -Id $baseline.Id -Force; $baseline.WaitForExit() }
    if ($containerId) { & docker stop $containerId | Out-Null }
    $env:PRAGUE_BOOKING_DATA = $previous.data
    $env:PRAGUE_DTS_ENDPOINT = $previous.endpoint
    $env:PRAGUE_BOOKING_CRASH = $previous.crash
    Pop-Location
}
