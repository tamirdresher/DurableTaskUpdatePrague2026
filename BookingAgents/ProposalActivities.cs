using BookingDemo;
using Microsoft.DurableTask;

namespace BookingAgents;

// Freezes the agents' proposal as version 1 with the fixed total, and records it in the ledger.
// Its result is saved in the history, so every replay works with the same proposal.
public sealed class RecordItinerary(BookingLedger ledger) : TaskActivity<ProposalDraft, ItineraryProposal>
{
    public override Task<ItineraryProposal> RunAsync(TaskActivityContext context, ProposalDraft input)
    {
        ItineraryProposal proposal = new(input.Id, 1, input.Trip, input.Planner, input.Reviewer,
            MockBookingClient.TotalEstimate, input.CreatedUtc, input.DeadlineUtc);
        ledger.RecordProposal(proposal);
        return Task.FromResult(proposal);
    }
}

// Checks an approval signal's receipt against the frozen proposal. As an activity it may read files and the
// clock, and its verdict is saved in the history, so a replay doesn't check again with a later clock.
public sealed class ValidateTripApproval(BookingLedger ledger) : TaskActivity<ApprovalCheck, ApprovalVerdict>
{
    public override Task<ApprovalVerdict> RunAsync(TaskActivityContext context, ApprovalCheck input)
    {
        ApprovalVerdict verdict = ledger.Validate(input, DateTime.UtcNow);
        ledger.Audit("validate-trip-approval", new { input.InstanceId, verdict });
        return Task.FromResult(verdict);
    }
}
