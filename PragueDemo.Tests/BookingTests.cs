using Azure.Identity;
using BookingAgents;
using Microsoft.DurableTask;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using B = BookingDemo;

namespace PragueDemo.Tests;

// Each test gets its own ledger folder, deleted in Dispose.
public sealed class BookingTests : IDisposable
{
    private readonly B.BookingLedger ledger = new(Path.Combine(Environment.CurrentDirectory, "test-data-booking", Guid.NewGuid().ToString("N")));
    private readonly DateTime now = DateTime.UtcNow;

    // A valid proposal, a matching receipt from the fixture approver, and a check and booking command bound to them.
    private B.ItineraryProposal Proposal() => new(Guid.NewGuid().ToString(), 1, new("trip"), "SCRIPTED planner",
        "SCRIPTED reviewer", B.MockBookingClient.TotalEstimate, now.AddMinutes(-1), now.AddMinutes(2));
    private B.ApprovalReceipt Receipt(B.ItineraryProposal p) => new(Guid.NewGuid().ToString("N"), "instance",
        p.Id, p.Version, p.Digest, "demo-traveler", "local-mock-trip-approver", true, now);
    private static B.ApprovalCheck Check(B.ItineraryProposal p, B.ApprovalReceipt r) => new("instance", p, new(r.ReceiptId));
    private static B.BookingCommand Command(B.ApprovalCheck check, string service) =>
        new($"{check.InstanceId}/{check.Proposal.Id}/v{check.Proposal.Version}/{service}", service, check.Proposal.Trip, check);

    [Fact]
    public async Task ThreeBookingActivitiesOnlyCreateLabelledMockReceipts()
    {
        B.MockBookingClient client = new(ledger);
        B.Trip trip = new("trip");
        var flight = await client.BookFlightAsync(new("trip/flight", "flight", trip));
        var hotel = await client.BookHotelAsync(new("trip/hotel", "hotel", trip));
        var car = await client.BookCarAsync(new("trip/car", "car", trip));
        Assert.All(new[] { flight, hotel, car }, receipt =>
        {
            Assert.StartsWith("MOCK-", receipt.Confirmation);
            Assert.Contains("NOT A REAL RESERVATION", receipt.Provider);
        });
        Assert.Equal(B.MockBookingClient.TotalEstimate, flight.Price + hotel.Price + car.Price);
        Assert.Equal(3, ledger.Bookings().Length);
    }

    [Fact]
    public void SameOperationSurvivesProviderRecreationWithoutDuplicateEffect()
    {
        // A new ledger over the same folder plays a restarted provider: same key, same booking, two audited attempts.
        B.BookingCommand command = new("stable/hotel", "hotel", new("trip"));
        B.BookingReceipt first = ledger.Book(command, now);
        B.BookingLedger restarted = new(ledger.Root);
        Assert.Equal(first, restarted.Book(command, now.AddDays(1)));
        Assert.Single(restarted.Bookings());
        Assert.Equal(2, File.ReadLines(Path.Combine(ledger.Root, "audit.jsonl")).Count());
    }

    [Fact]
    public void NewNaiveAttemptIdentityDuplicatesAlreadyBookedWork()
    {
        // The naive demo's problem: the same work under a new key is booked again.
        B.Trip trip = new("same-trip");
        ledger.Book(new("attempt-1/flight", "flight", trip), now);
        ledger.Book(new("attempt-2/flight", "flight", trip), now);
        Assert.Equal(2, ledger.Bookings().Length);
    }

    [Fact]
    public void OperationKeyCannotBeReusedForDifferentTripOrService()
    {
        B.BookingCommand command = new("stable", "flight", new("trip"));
        ledger.Book(command, now);
        Assert.Throws<InvalidOperationException>(() => ledger.Book(command with { Trip = new("other") }, now));
        Assert.Throws<InvalidOperationException>(() => ledger.Book(command with { Service = "hotel" }, now));
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("proposal")]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("actor")]
    [InlineData("role")]
    [InlineData("receipt")]
    public void ApprovalRequiresExactBindingAndFixtureAuthority(string mutation)
    {
        // Changing any one field of a valid receipt must make it unauthorized.
        B.ItineraryProposal p = Proposal();
        B.ApprovalReceipt r = Receipt(p);
        B.ApprovalCheck check = Check(p, r);
        r = mutation switch
        {
            "instance" => r with { InstanceId = "other" },
            "proposal" => r with { ProposalId = "other" },
            "version" => r with { Version = 2 },
            "digest" => r with { Digest = "other" },
            "actor" => r with { Actor = "untrusted-event-sender" },
            "role" => r with { Role = "reader" },
            _ => r with { ReceiptId = "other" }
        };
        Assert.False(B.ApprovalPolicy.Evaluate(check, r, now).Authorized);
    }

    [Fact]
    public void NotificationAloneAndMalformedReceiptCannotAuthorize()
    {
        // A receipt ID that isn't a GUID (here, a path) is rejected before any file is read,
        // and a signal without a receipt authorizes nothing.
        B.ItineraryProposal p = Proposal();
        B.ApprovalCheck check = new("instance", p, new("..\\forged"));
        Assert.False(ledger.Validate(check, now).Authorized);
        Assert.False(B.ApprovalPolicy.Evaluate(check, null, now).Authorized);
        Assert.Empty(ledger.Bookings());
    }

    [Fact]
    public void DeadlineIsExclusiveAndBackdatedReceiptRejected()
    {
        // Rejected: checked exactly at the deadline, issued exactly at the deadline, or issued before the proposal existed.
        B.ItineraryProposal p = Proposal();
        B.ApprovalReceipt r = Receipt(p);
        Assert.False(B.ApprovalPolicy.Evaluate(Check(p, r), r, p.DeadlineUtc).Authorized);
        Assert.False(B.ApprovalPolicy.Evaluate(Check(p, r), r with { IssuedUtc = p.DeadlineUtc }, now).Authorized);
        Assert.False(B.ApprovalPolicy.Evaluate(Check(p, r), r with { IssuedUtc = p.CreatedUtc.AddTicks(-1) }, now).Authorized);
    }

    [Fact]
    public void ImmutableItineraryDigestBindsContentDatesAndBudget()
    {
        // Recording the same proposal twice is fine. Changing the budget or the deadline changes the digest,
        // and a different proposal under the same ID throws.
        B.ItineraryProposal p = Proposal();
        ledger.RecordProposal(p);
        ledger.RecordProposal(p);
        Assert.NotEqual(p.Digest, (p with { Trip = p.Trip with { Budget = 1400 } }).Digest);
        Assert.NotEqual(p.Digest, (p with { DeadlineUtc = p.DeadlineUtc.AddMinutes(1) }).Digest);
        Assert.Throws<InvalidOperationException>(() => ledger.RecordProposal(p with { Version = 2 }));
    }

    [Fact]
    public void ApprovedBookingBindsOperationAndReturnsOriginalAfterExpiredRedelivery()
    {
        // A redelivery after the deadline returns the original booking; keys that don't match the approved
        // proposal version are refused.
        B.ItineraryProposal p = Proposal();
        B.ApprovalReceipt r = ledger.IssueReceipt("instance", p, true);
        B.BookingCommand command = Command(Check(p, r), "hotel");
        B.BookingReceipt first = ledger.Book(command, now);
        Assert.Equal(first, new B.BookingLedger(ledger.Root).Book(command, p.DeadlineUtc.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => ledger.Book(command with { OperationId = "forged-operation" }, now));
        Assert.Throws<InvalidOperationException>(() => ledger.Book(
            command with { OperationId = command.OperationId + "-different", Trip = new("other") }, now));
        Assert.Single(ledger.Bookings());
    }

    [Fact]
    public void DeniedExpiredAndOverBudgetProposalsCannotCreateBooking()
    {
        B.ItineraryProposal p = Proposal();
        var denied = ledger.IssueReceipt("instance", p, false);
        Assert.Throws<InvalidOperationException>(() => ledger.Book(Command(Check(p, denied), "flight"), now));
        var approved = ledger.IssueReceipt("instance", p, true);
        Assert.Throws<InvalidOperationException>(() => ledger.Book(Command(Check(p, approved), "flight"), p.DeadlineUtc));
        B.ItineraryProposal overBudget = p with { Trip = p.Trip with { Budget = 100 } };
        var over = ledger.IssueReceipt("instance", overBudget, true);
        Assert.Throws<InvalidOperationException>(() => ledger.Book(Command(Check(overBudget, over), "hotel"), now));
        Assert.Empty(ledger.Bookings());
    }

    [Fact]
    public void CrashGateIsConsumedOnceForSameOperationAcrossRestart()
    {
        // CrashHook's marker: once per checkpoint and operation, even for a new ledger over the same folder.
        Assert.True(ledger.MarkCrashOnce("before-car", "trip/car"));
        Assert.False(new B.BookingLedger(ledger.Root).MarkCrashOnce("before-car", "trip/car"));
        Assert.True(ledger.MarkCrashOnce("before-car", "different-trip/car"));
    }

    [Fact]
    public void ManagedBookingConfigurationIsOnlyRegisteredWithoutTokenOrCloudCall()
    {
        // Builds the Azure configuration and reads the options back; no token is requested and nothing calls Azure.
        ServiceCollection services = new();
        services.AddLogging();
        BookingIntro.ManagedHostingExample.ConfigureWorker(services, "https://scheduler.example.invalid", "existing-hub", ledger);
        BookingIntro.ManagedHostingExample.ConfigureClient(services, "https://scheduler.example.invalid", "existing-hub");
        using ServiceProvider provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IOptionsMonitor<DurableTaskSchedulerWorkerOptions>>().Get(string.Empty);
        var client = provider.GetRequiredService<IOptionsMonitor<DurableTaskSchedulerClientOptions>>().Get(string.Empty);
        Assert.Equal("existing-hub", worker.TaskHubName);
        Assert.Equal(worker.EndpointAddress, client.EndpointAddress);
        Assert.IsType<ManagedIdentityCredential>(worker.Credential);
        Assert.IsType<ManagedIdentityCredential>(client.Credential);
    }

    [Theory]
    [InlineData("TripPlanner", "OFFLINE SCRIPTED TRIP PLANNER")]
    [InlineData("BudgetReviewer", "OFFLINE SCRIPTED BUDGET REVIEWER")]
    public async Task DurableAgentStreamingIsConspicuouslyScripted(string name, string label)
    {
        // The streaming path returns the same labelled, scripted text.
        using ScriptedTripChatClient client = new(name, ledger);
        List<Microsoft.Extensions.AI.ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync([]))
            updates.Add(update);
        Assert.Contains(updates, update => update.Text.Contains(label));
    }

    public void Dispose() => Directory.Delete(ledger.Root, recursive: true);
}
