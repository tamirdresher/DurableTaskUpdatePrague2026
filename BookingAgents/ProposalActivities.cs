using BookingDemo;
using Microsoft.DurableTask;

namespace BookingAgents;

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
public sealed class ValidateTripApproval(BookingLedger ledger) : TaskActivity<ApprovalCheck, ApprovalVerdict>
{
    public override Task<ApprovalVerdict> RunAsync(TaskActivityContext context, ApprovalCheck input)
    {
        ApprovalVerdict verdict = ledger.Validate(input, DateTime.UtcNow);
        ledger.Audit("validate-trip-approval", new { input.InstanceId, verdict });
        return Task.FromResult(verdict);
    }
}
