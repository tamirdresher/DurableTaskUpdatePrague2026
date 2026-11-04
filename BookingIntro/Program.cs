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
    BookingLedger ledger = new(Environment.GetEnvironmentVariable("PRAGUE_BOOKING_DATA") ?? Path.Combine(Environment.CurrentDirectory, "data", "booking"));
    MockBookingClient booking = new(ledger);
    if (command == "naive")
    {
        if (args.Length < 2) throw new ArgumentException("Supply a trip ID.");
        string attemptId = "naive-" + Guid.NewGuid().ToString("N");
        Console.WriteLine($"NAIVE ordinary async attempt={attemptId}; MOCK providers only.");
        Print(await NaiveTrip.BookAsync(booking, new Trip(args[1]), attemptId));
        return 0;
    }
    if (command == "ledger") { Print(ledger.Bookings()); return 0; }
    string endpoint = Environment.GetEnvironmentVariable("PRAGUE_DTS_ENDPOINT") ?? "http://localhost:18080";
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback || uri.Scheme != "http")
        throw new InvalidOperationException("Runnable booking demos accept only a local HTTP emulator.");
    string connection = $"Endpoint={endpoint};TaskHub=default;Authentication=None";
    HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Logging.SetMinimumLevel(command == "worker" ? LogLevel.Information : LogLevel.Error);
    builder.Services.AddSingleton(ledger);
    builder.Services.AddSingleton(booking);
    if (command == "worker")
    {
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
        await worker.RunAsync();
        return 0;
    }
    builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connection));
    using IHost app = builder.Build();
    DurableTaskClient client = app.Services.GetRequiredService<DurableTaskClient>();
    using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(command == "wait" ? 240 : 30));
    if (args.Length < 2) throw new ArgumentException("Supply an instance ID.");
    string id = args[1];
    switch (command)
    {
        case "start":
            Trip trip = new(args.Length > 2 ? args[2] : id);
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(BookTrip), trip,
                new StartOrchestrationOptions { InstanceId = id }, cancellation.Token);
            Print(new { instanceId, trip, provider = "LOCAL MOCK; not real reservations." });
            break;
        case "status":
            PrintStatus(await client.GetInstanceAsync(id, true, cancellation.Token)
                ?? throw new InvalidOperationException("Instance not found."));
            break;
        case "wait":
            OrchestrationMetadata completed = await client.WaitForInstanceCompletionAsync(id, true, cancellation.Token);
            PrintStatus(completed);
            if (completed.RuntimeStatus != OrchestrationRuntimeStatus.Completed) return 1;
            break;
        case "history":
            var history = await client.GetOrchestrationHistoryAsync(id, cancellation.Token);
            Print(history.Select(item => JsonSerializer.SerializeToElement(item, item.GetType(), DemoJson.Options)));
            break;
        default: throw new ArgumentException("Unknown command: " + command);
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
    return 1;
}

static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, DemoJson.Options));
static void PrintStatus(OrchestrationMetadata metadata) => Print(new
{
    metadata.InstanceId,
    runtimeStatus = metadata.RuntimeStatus.ToString(),
    result = metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed ? metadata.ReadOutputAs<TripResult>() : null,
    failure = metadata.FailureDetails?.ErrorMessage
});
