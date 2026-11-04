using System.Text.Json;
using BookingAgents;
using BookingDemo;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Commands: worker | start <instance-id> [approval-seconds] | status|wait|history|approve|deny <instance-id> | signal <instance-id> <receipt-id>");
        return 2;
    }
    string command = args[0].ToLowerInvariant();
    BookingLedger ledger = new(Environment.GetEnvironmentVariable("PRAGUE_BOOKING_DATA") ?? Path.Combine(Environment.CurrentDirectory, "data", "booking"));
    string endpoint = Environment.GetEnvironmentVariable("PRAGUE_DTS_ENDPOINT") ?? "http://localhost:18080";
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback || uri.Scheme != "http")
        throw new InvalidOperationException("Runnable booking demos accept only a local HTTP emulator.");
    string connection = $"Endpoint={endpoint};TaskHub=default;Authentication=None";
    HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Logging.SetMinimumLevel(command == "worker" ? LogLevel.Warning : LogLevel.Error);
    builder.Services.AddSingleton(ledger);
    builder.Services.AddSingleton<MockBookingClient>();
    if (command == "worker")
    {
        builder.Services.ConfigureDurableAgents(
            options =>
            {
                options.AddAIAgent(new ScriptedTripChatClient("TripPlanner", ledger).AsAIAgent(
                    instructions: "OFFLINE scripted trip planning; MOCK bookings only.", name: "TripPlanner"));
                options.AddAIAgent(new ScriptedTripChatClient("BudgetReviewer", ledger).AsAIAgent(
                    instructions: "OFFLINE scripted budget review, no approval authority.", name: "BudgetReviewer"));
            },
            workerBuilder: worker =>
            {
                worker.UseDurableTaskScheduler(connection);
                worker.AddTasks(registry =>
                {
                    registry.AddOrchestrator<PlanAndBookTrip>();
                    registry.AddActivity<RecordItinerary>();
                    registry.AddActivity<ValidateTripApproval>();
                    registry.AddActivity<BookFlight>();
                    registry.AddActivity<BookHotel>();
                    registry.AddActivity<BookCar>();
                });
            },
            clientBuilder: client => client.UseDurableTaskScheduler(connection));
        using IHost workerHost = builder.Build();
        Console.WriteLine($"REAL MAF + DTS; OFFLINE SCRIPTED agents, MOCK bookings; owned PID={Environment.ProcessId}");
        await workerHost.RunAsync();
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
            TripInput input = new(new Trip(id), args.Length > 2 ? int.Parse(args[2]) : 120);
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(PlanAndBookTrip), input,
                new StartOrchestrationOptions { InstanceId = id }, cancellation.Token);
            Print(new { instanceId, fixtures = "OFFLINE SCRIPTED; LOCAL MOCK ONLY" });
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
        case "approve":
        case "deny":
            OrchestrationMetadata metadata = await client.GetInstanceAsync(id, true, cancellation.Token)
                ?? throw new InvalidOperationException("Instance not found.");
            BookingStatus status = ReadStatus(metadata) ?? throw new InvalidOperationException("Itinerary not recorded yet.");
            if (status.Phase != "AwaitingApproval" || status.Proposal is null ||
                !ApprovalPolicy.IsBeforeDeadline(DateTime.UtcNow, status.Proposal.DeadlineUtc))
                throw new InvalidOperationException("Not awaiting an unexpired itinerary approval.");
            ApprovalReceipt receipt = ledger.IssueReceipt(id, status.Proposal, command == "approve");
            await client.RaiseEventAsync(id, PlanAndBookTrip.ApprovalEvent, new ApprovalSignal(receipt.ReceiptId), cancellation.Token);
            Print(new { receipt, authority = "LOCAL FIXTURE; NOT production authentication." });
            break;
        case "signal":
            if (args.Length != 3) throw new ArgumentException("Supply a receipt ID; unknown receipts fail independent validation.");
            await client.RaiseEventAsync(id, PlanAndBookTrip.ApprovalEvent, new ApprovalSignal(args[2]), cancellation.Token);
            Print(new { signaled = id, authorization = "NONE: notifications are not authorization." });
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
static BookingStatus? ReadStatus(OrchestrationMetadata metadata) =>
    metadata.SerializedCustomStatus is null ? null : JsonSerializer.Deserialize<BookingStatus>(metadata.SerializedCustomStatus, DemoJson.Options);
static void PrintStatus(OrchestrationMetadata metadata) => Print(new
{
    metadata.InstanceId,
    runtimeStatus = metadata.RuntimeStatus.ToString(),
    status = ReadStatus(metadata),
    result = metadata.SerializedOutput is null ? null : JsonSerializer.Deserialize<PlannedTripResult>(metadata.SerializedOutput, DemoJson.Options),
    failure = metadata.FailureDetails?.ErrorMessage
});
