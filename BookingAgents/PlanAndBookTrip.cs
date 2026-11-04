using System.Text.Json;
using BookingDemo;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;

namespace BookingAgents;

public sealed class PlanAndBookTrip : TaskOrchestrator<TripInput, PlannedTripResult>
{
    public const string ApprovalEvent = "TripApproval";
    public override async Task<PlannedTripResult> RunAsync(TaskOrchestrationContext context, TripInput input)
    {
        if (input.ApprovalSeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(input));
        DurableAIAgent planner = context.GetAgent("TripPlanner");
        AgentSession plannerSession = await planner.CreateSessionAsync();
        AgentResponse itinerary = await planner.RunAsync(JsonSerializer.Serialize(input.Trip), plannerSession);
        DurableAIAgent reviewer = context.GetAgent("BudgetReviewer");
        AgentSession reviewerSession = await reviewer.CreateSessionAsync();
        AgentResponse review = await reviewer.RunAsync(itinerary.Text, reviewerSession);
        DateTime created = context.CurrentUtcDateTime;
        ItineraryProposal proposal = await context.CallActivityAsync<ItineraryProposal>(nameof(RecordItinerary),
            new ProposalDraft(context.NewGuid().ToString(), input.Trip, itinerary.Text, review.Text,
                created, created.AddSeconds(input.ApprovalSeconds)));
        context.SetCustomStatus(new BookingStatus("AwaitingApproval", proposal));

        using CancellationTokenSource timerCancellation = new();
        using CancellationTokenSource eventCancellation = new();
        Task timeout = context.CreateTimer(proposal.DeadlineUtc, timerCancellation.Token);
        while (ApprovalPolicy.IsBeforeDeadline(context.CurrentUtcDateTime, proposal.DeadlineUtc))
        {
            Task<ApprovalSignal> notification = context.WaitForExternalEvent<ApprovalSignal>(ApprovalEvent, eventCancellation.Token);
            if (await Task.WhenAny(notification, timeout) == timeout) break;
            ApprovalCheck check = new(context.InstanceId, proposal, await notification);
            ApprovalVerdict verdict = await context.CallActivityAsync<ApprovalVerdict>(nameof(ValidateTripApproval), check);
            if (!verdict.Authorized)
            {
                context.SetCustomStatus(new BookingStatus("AwaitingApproval", proposal, verdict.Reason));
                continue; // One immutable deadline; invalid notifications do not reset the timer.
            }
            if (!ApprovalPolicy.IsBeforeDeadline(context.CurrentUtcDateTime, proposal.DeadlineUtc)) break;
            timerCancellation.Cancel();
            if (!verdict.Approved)
            {
                context.SetCustomStatus(new BookingStatus("Denied", proposal));
                return new("Denied", proposal, []);
            }
            context.SetCustomStatus(new BookingStatus("Booking", proposal));
            string operation = $"{context.InstanceId}/{proposal.Id}/v{proposal.Version}";
            BookingReceipt flight = await context.CallActivityAsync<BookingReceipt>(nameof(BookFlight),
                new BookingCommand($"{operation}/flight", "flight", proposal.Trip, check));
            BookingReceipt hotel = await context.CallActivityAsync<BookingReceipt>(nameof(BookHotel),
                new BookingCommand($"{operation}/hotel", "hotel", proposal.Trip, check));
            BookingReceipt car = await context.CallActivityAsync<BookingReceipt>(nameof(BookCar),
                new BookingCommand($"{operation}/car", "car", proposal.Trip, check));
            context.SetCustomStatus(new BookingStatus("Booked", proposal));
            return new("Booked", proposal, [flight, hotel, car]);
        }
        eventCancellation.Cancel();
        context.SetCustomStatus(new BookingStatus("TimedOut", proposal));
        return new("TimedOut", proposal, []);
    }
}
