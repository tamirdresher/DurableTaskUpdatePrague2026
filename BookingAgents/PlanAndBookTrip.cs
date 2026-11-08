using System.Text.Json;
using BookingDemo;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;

namespace BookingAgents;

// Two agents plan and review the trip, the proposal is frozen, the traveler has until a fixed deadline to
// approve, and only then is anything booked. Each step's result is saved in the history, so after a crash
// a new worker replays to where the old one stopped.
public sealed class PlanAndBookTrip : TaskOrchestrator<TripInput, PlannedTripResult>
{
    // The external event that the approve, deny and signal commands raise.
    public const string ApprovalEvent = "TripApproval";
    public override async Task<PlannedTripResult> RunAsync(TaskOrchestrationContext context, TripInput input)
    {
        if (input.ApprovalSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(input));

        // Calls through DurableAIAgent are checkpointed: each runs in the agent's durable entity, and the
        // response is saved in the history. After a restart, completed calls don't call the model again.
        DurableAIAgent planner = context.GetAgent("TripPlanner");
        AgentSession plannerSession = await planner.CreateSessionAsync();
        AgentResponse itinerary = await planner.RunAsync(JsonSerializer.Serialize(input.Trip), plannerSession);
        DurableAIAgent reviewer = context.GetAgent("BudgetReviewer");
        AgentSession reviewerSession = await reviewer.CreateSessionAsync();
        AgentResponse review = await reviewer.RunAsync(itinerary.Text, reviewerSession);

        // RecordItinerary freezes the proposal, including its fixed deadline. CurrentUtcDateTime and NewGuid
        // are replay-safe: every replay sees the same values.
        DateTime created = context.CurrentUtcDateTime;
        ItineraryProposal proposal = await context.CallActivityAsync<ItineraryProposal>(nameof(RecordItinerary),
            new ProposalDraft(context.NewGuid().ToString(), input.Trip, itinerary.Text, review.Text,
                created, created.AddSeconds(input.ApprovalSeconds)));
        // The custom status is what the client polls: the phase, and the proposal waiting for approval.
        context.SetCustomStatus(new BookingStatus("AwaitingApproval", proposal));

        // A durable timer for the fixed deadline, raced against the approval event. The timer is created
        // once, so invalid events don't reset it.
        using CancellationTokenSource timerCancellation = new();
        using CancellationTokenSource eventCancellation = new();
        Task timeout = context.CreateTimer(proposal.DeadlineUtc, timerCancellation.Token);
        while (ApprovalPolicy.IsBeforeDeadline(context.CurrentUtcDateTime, proposal.DeadlineUtc))
        {
            Task<ApprovalSignal> notification = context.WaitForExternalEvent<ApprovalSignal>(ApprovalEvent, eventCancellation.Token);
            if (await Task.WhenAny(notification, timeout) == timeout)
                break;
            ApprovalCheck check = new(context.InstanceId, proposal, await notification);
            // Validation runs in an activity: it reads the receipt files, and its verdict is saved in the
            // history, so a replay gets the same answer.
            ApprovalVerdict verdict = await context.CallActivityAsync<ApprovalVerdict>(nameof(ValidateTripApproval), check);
            if (!verdict.Authorized)
            {
                // Rejected: show why in the status, and keep waiting for the same deadline.
                context.SetCustomStatus(new BookingStatus("AwaitingApproval", proposal, verdict.Reason));
                continue; // One immutable deadline; invalid notifications do not reset the timer.
            }
            // If validation finished after the deadline, even a valid answer is too late.
            if (!ApprovalPolicy.IsBeforeDeadline(context.CurrentUtcDateTime, proposal.DeadlineUtc))
                break;
            timerCancellation.Cancel();
            if (!verdict.Approved)
            {
                context.SetCustomStatus(new BookingStatus("Denied", proposal));
                return new("Denied", proposal, []);
            }

            // Booking keys, "<instance>/<proposal>/v<version>/<service>", are bound to the approved proposal
            // version, and the ledger checks the approval again before each booking takes effect.
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

        // The deadline passed without a valid decision: stop listening for the event. Nothing was booked.
        eventCancellation.Cancel();
        context.SetCustomStatus(new BookingStatus("TimedOut", proposal));
        return new("TimedOut", proposal, []);
    }
}
