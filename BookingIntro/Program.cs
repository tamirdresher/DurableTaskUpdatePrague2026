// One CLI plays both roles. "worker" hosts the BookTrip orchestrator and its three activities;
// "start", "status", "wait" and "history" are clients of the scheduler ("naive" and "ledger" don't use it).
// Stage.ps1 drives these commands in the talk.

using System.Text.Json;
using BookingDemo;
using BookingIntro;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Commands: worker | naive <trip-id> | start <instance-id> [trip-id] | status|wait|history <instance-id> | ledger");
        return 2;
    }
    string command = args[0].ToLowerInvariant();
    // The fake provider keeps its bookings in files, so they outlive any process that crashes.
    BookingLedger ledger = new(Environment.GetEnvironmentVariable("PRAGUE_BOOKING_DATA") ?? Path.Combine(Environment.CurrentDirectory, "data", "booking"));
    MockBookingClient booking = new(ledger);

    // "naive": the same three bookings as plain async code (NaiveTrip), no Durable Task.
    if (command == "naive")
    {
        if (args.Length < 2)
            throw new ArgumentException("Supply a trip ID.");
        // A new attempt ID on every run means new operation IDs, so the provider can't tell a rerun
        // from a new trip: a rerun books everything again.
        string attemptId = "naive-" + Guid.NewGuid().ToString("N");
        Console.WriteLine($"NAIVE ordinary async attempt={attemptId}; MOCK providers only.");
        Print(await NaiveTrip.BookAsync(booking, new Trip(args[1]), attemptId));
        return 0;
    }

    // "ledger": what the fake provider has booked, read straight from its files.
    if (command == "ledger")
    {
        Print(ledger.Bookings());
        return 0;
    }

    // Only the local emulator: plain HTTP on loopback, task hub "default", no authentication.
    // ManagedHostingExample.cs shows the Azure version of this connection.
    string endpoint = Environment.GetEnvironmentVariable("PRAGUE_DTS_ENDPOINT") ?? "http://localhost:18080";
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback || uri.Scheme != "http")
        throw new InvalidOperationException("Runnable booking demos accept only a local HTTP emulator.");
    string connection = $"Endpoint={endpoint};TaskHub=default;Authentication=None";

    HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
    // The worker logs one line per event at Information level, because Stage.ps1 follows that log to show
    // progress. Client commands log errors only, which keeps their JSON output clean.
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Logging.SetMinimumLevel(command == "worker" ? LogLevel.Information : LogLevel.Error);
    // The activities get the provider client through dependency injection.
    builder.Services.AddSingleton(ledger);
    builder.Services.AddSingleton(booking);

    if (command == "worker")
    {
        // Register BookTrip and its three activities. UseDurableTaskScheduler connects to the scheduler,
        // which stores each orchestration's history and hands out the work.
        builder.Services.AddDurableTaskWorker()
            .AddTasks(registry =>
            {
                registry.AddOrchestrator<BookTrip>();
                registry.AddActivity<BookFlight>();
                registry.AddActivity<BookHotel>();
                registry.AddActivity<BookCar>();
            })
            .UseDurableTaskScheduler(connection);
        using IHost worker = builder.Build();
        Console.WriteLine($"PLAIN DURABLE TASK; owned worker PID={Environment.ProcessId}; MOCK bookings only.");
        // Runs until the process stops or dies. The history stays in the scheduler,
        // so a new worker carries on where this one left off.
        await worker.RunAsync();
        return 0;
    }

    // Everything below is a client: it talks to the scheduler, never to a worker directly.
    builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connection));
    using IHost app = builder.Build();
    DurableTaskClient client = app.Services.GetRequiredService<DurableTaskClient>();
    // "wait" may have to outlast a worker restart; the other calls return quickly.
    using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(command == "wait" ? 240 : 30));
    if (args.Length < 2)
        throw new ArgumentException("Supply an instance ID.");
    string id = args[1];
    switch (command)
    {
        case "start":
            // The caller chooses the instance ID. BookTrip builds the provider keys from it,
            // and status, wait, history and the dashboard find the trip by it.
            Trip trip = new(args.Length > 2 ? args[2] : id);
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(BookTrip), trip,
                new StartOrchestrationOptions { InstanceId = id }, cancellation.Token);
            Print(new { instanceId, trip, provider = "LOCAL MOCK; not real reservations." });
            break;
        case "status":
            // The instance's current state (Running, Completed, Failed...) and, once it has completed, its result.
            PrintStatus(await client.GetInstanceAsync(id, true, cancellation.Token)
                ?? throw new InvalidOperationException("Instance not found."));
            break;
        case "wait":
            // Blocks until the instance finishes; the exit code is 1 unless it completed.
            OrchestrationMetadata completed = await client.WaitForInstanceCompletionAsync(id, true, cancellation.Token);
            PrintStatus(completed);
            if (completed.RuntimeStatus != OrchestrationRuntimeStatus.Completed)
                return 1;
            break;
        case "history":
            // The history the scheduler keeps for this instance: each activity scheduled and each result saved.
            // Stage.ps1 prints it after the crash to show what survived.
            var history = await client.GetOrchestrationHistoryAsync(id, cancellation.Token);
            Print(history.Select(item => JsonSerializer.SerializeToElement(item, item.GetType(), DemoJson.Options)));
            break;
        default:
            throw new ArgumentException("Unknown command: " + command);
    }
    return 0;
}
catch (Exception error)
{
    // One short line; Stage.ps1 and Rehearse.ps1 check the exit code.
    Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
    return 1;
}

// JSON output, so Stage.ps1 and Rehearse.ps1 can parse it.
static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, DemoJson.Options));
static void PrintStatus(OrchestrationMetadata metadata) => Print(new
{
    metadata.InstanceId,
    runtimeStatus = metadata.RuntimeStatus.ToString(),
    result = metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed ? metadata.ReadOutputAs<TripResult>() : null,
    failure = metadata.FailureDetails?.ErrorMessage
});
