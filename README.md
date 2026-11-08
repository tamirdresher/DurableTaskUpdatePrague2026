# Code That Survives the Crash

Demos and slides from my Update Conference Prague 2026 session, *Code That Survives the Crash: Durable Task Scheduler for Workflows and AI Agents*.

The demos follow one scenario: an app books a flight, a hotel and a car, and the process dies between the hotel and the car. What got booked? How do you finish the trip without booking anything twice?

## What's inside

| Path | What it is |
|---|---|
| `BookingIntro/` | Plain [Durable Task](https://learn.microsoft.com/azure/durable-task/sdks/durable-task-overview): the `BookTrip` orchestrator, three booking activities, and a worker/client CLI |
| `BookingAgents/` | Durable agents with [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/hosting/durable-extension): a planner, a budget reviewer, and human approval with a deadline |
| `BookingDemo.Core/` | Shared contracts and a fake booking provider that deduplicates by operation ID |
| `PragueDemo.Tests/` | Unit tests |
| `python/` | The same `BookTrip` workflow in Python |
| `Stage.ps1` | The script I run on stage |
| `Rehearse.ps1` | Runs six scenarios end to end and checks every result |
| `slides/` | The slides as a PDF |

Everything runs locally against the [Durable Task Scheduler emulator](https://learn.microsoft.com/azure/durable-task/scheduler/develop-with-durable-task-scheduler). Bookings are fake, and the agents use a scripted `IChatClient`, so nothing calls a real provider, model or Azure.

## Prerequisites

- .NET 10 SDK
- Docker running Linux containers
- PowerShell 7.2 or later (`pwsh`)
- The emulator image these demos were tested with:

  ```powershell
  docker pull mcr.microsoft.com/dts/dts-emulator@sha256:361323065a608d605f9d3ae56b854eb11f1c47fcc16845a37f948f03ca9c5fac
  ```

  To try a newer emulator, pass `-EmulatorImage <image>` to `Stage.ps1` or `Rehearse.ps1`.

## Run the demos

```powershell
./Stage.ps1 setup -OpenDashboard   # builds, starts the emulator, opens the dashboard
./Stage.ps1 naive                  # three plain awaits: crash, rerun, two flights and two hotels
./Stage.ps1 crash                  # durable worker dies between hotel and car, a new worker finishes the trip
./Stage.ps1 ackgap                 # hotel is booked, the worker dies before reporting back: two attempts, one hotel
./Stage.ps1 agents                 # agents plan, the worker is killed, a fake approval is rejected, then the traveler approves
./Stage.ps1 cleanup                # stops the workers and the emulator it started
```

Each step waits for Enter. To check everything at once, run `./Stage.ps1 rehearse` before `setup`: it starts its own emulator, runs every demo without pauses, cleans up, and exits with code 1 if any result is unexpected.

In the dashboard (`http://localhost:18082`), open the `default` task hub, choose an instance such as `trip-1`, and look at **Timeline** and **History**.

## Python

The same workflow with [durabletask-python](https://github.com/microsoft/durabletask-python), against the same emulator. Start it with `./Stage.ps1 setup` first.

```powershell
cd python
python -m venv .venv
./.venv/Scripts/python -m pip install -r requirements.txt --index-url https://packagefeedproxy.microsoft.io/pypi/simple/

# Terminal 1: a worker that dies right before the car booking
$env:PRAGUE_BOOKING_CRASH = 'before-car'
./.venv/Scripts/python book_trip.py worker

# Terminal 2: book a trip
./.venv/Scripts/python book_trip.py start py-trip-1

# Terminal 1 again: a new worker resumes the same trip
Remove-Item Env:PRAGUE_BOOKING_CRASH
./.venv/Scripts/python book_trip.py worker

# Terminal 2
./.venv/Scripts/python book_trip.py wait py-trip-1
./.venv/Scripts/python book_trip.py ledger     # flight 1, hotel 1, car 1
```

On macOS or Linux, use `.venv/bin/python`.

## Use a real model

`BookingAgents/Program.cs` registers `ScriptedTripChatClient` for both agents. Replace it with any `IChatClient` (Azure OpenAI, OpenAI, Ollama, and others) and the durable workflow stays the same.

## Good to know

- Package versions are pinned with lock files. `NuGet.Config` points at Microsoft's package feed proxy.
- The Agent Framework durable extension used here is a preview package.
- The demos crash only the worker. The emulator keeps its state in memory, so don't restart it in the middle of a demo.

## Links

- [Durable Task Scheduler](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler)
- [Durable Task .NET SDK](https://github.com/microsoft/durabletask-dotnet)
- [Orchestrator code constraints](https://learn.microsoft.com/azure/durable-task/common/durable-task-code-constraints)
- [Orchestration versioning](https://learn.microsoft.com/azure/durable-task/common/durable-orchestration-versioning)
- [Agent Framework durable extension](https://learn.microsoft.com/agent-framework/hosting/durable-extension)
- [The blog post behind the booking experiment](https://www.tamirdresher.com/blog/2026/10/16/copilot-workflows-durability)
