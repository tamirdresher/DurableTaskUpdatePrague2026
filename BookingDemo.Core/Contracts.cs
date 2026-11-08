using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookingDemo;

// The trip to book; the defaults keep the demo commands short.
public sealed record Trip(string Id, string From = "SFO", string Destination = "Seattle",
    string Departure = "2026-11-10", string Return = "2026-11-14", decimal Budget = 1500m);

// The input of every booking activity. OperationId is the provider's idempotency key. Agent bookings
// also carry their approval, so the provider can check it again before booking.
public sealed record BookingCommand(string OperationId, string Service, Trip Trip, ApprovalCheck? Approval = null)
{
    // A fingerprint of the work itself (service, trip, approved proposal). The ledger uses it to refuse
    // an operation ID that is reused for different work.
    public string Digest => Hash(new { Service, Trip, ProposalDigest = Approval?.Proposal.Digest });
    // SHA-256 of the value's JSON, as hex; used for digests and file names.
    internal static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

// What the provider returns for one booking; the history saves it as the activity's result.
public sealed record BookingReceipt(string OperationId, string Service, string TripId,
    string CommandDigest, string Confirmation, decimal Price, string Provider = "LOCAL MOCK - NOT A REAL RESERVATION");

// BookTrip's output.
public sealed record TripResult(string Outcome, BookingReceipt[] Bookings);

// PlanAndBookTrip's input: the trip, and how many seconds the traveler has to approve.
public sealed record TripInput(Trip Trip, int ApprovalSeconds = 120);

// The agents' proposal, frozen by RecordItinerary. Digest is the itinerary's fingerprint over every field,
// so an approval binds to exactly this proposal: change anything and the digest no longer matches.
public sealed record ItineraryProposal(string Id, int Version, Trip Trip, string PlannerOutput,
    string ReviewerOutput, decimal TotalEstimate, DateTime CreatedUtc, DateTime DeadlineUtc)
{
    public string Digest => BookingCommand.Hash(new
    {
        Id, Version, Trip, PlannerOutput, ReviewerOutput, TotalEstimate, CreatedUtc, DeadlineUtc
    });
}

// What the orchestrator hands RecordItinerary: the agents' output and the approval window.
public sealed record ProposalDraft(string Id, Trip Trip, string Planner, string Reviewer,
    DateTime CreatedUtc, DateTime DeadlineUtc);

// The approval event's payload: only a receipt ID. The event proves nothing by itself; the receipt is checked.
public sealed record ApprovalSignal(string ReceiptId);

// Issued by the approve and deny commands: who decided what, for which instance and which exact proposal.
public sealed record ApprovalReceipt(string ReceiptId, string InstanceId, string ProposalId, int Version,
    string Digest, string Actor, string Role, bool Approved, DateTime IssuedUtc);

// What gets validated: a signal, against this instance and its frozen proposal.
public sealed record ApprovalCheck(string InstanceId, ItineraryProposal Proposal, ApprovalSignal Signal);

// Authorized: the receipt is genuine and valid. Approved: the traveler said yes.
public sealed record ApprovalVerdict(bool Authorized, bool Approved, string Reason);

// PlanAndBookTrip's custom status, which clients poll: the phase, the proposal, and why the last
// signal was rejected.
public sealed record BookingStatus(string Phase, ItineraryProposal? Proposal = null, string? Detail = null);

// PlanAndBookTrip's output.
public sealed record PlannedTripResult(string Outcome, ItineraryProposal Proposal, BookingReceipt[] Bookings);

// The approval rules. The validation activity applies them, and the ledger applies them again before booking.
public static class ApprovalPolicy
{
    // The deadline is exclusive: at the deadline itself, it's too late.
    public static bool IsBeforeDeadline(DateTime now, DateTime deadline) => now < deadline;
    public static ApprovalVerdict Evaluate(ApprovalCheck check, ApprovalReceipt? receipt, DateTime now)
    {
        // The signal must name a receipt that was actually issued.
        if (receipt is null || receipt.ReceiptId != check.Signal.ReceiptId)
            return new(false, false, "Unknown receipt: an event is not authorization.");
        ItineraryProposal p = check.Proposal;
        // The receipt must be for this instance and this exact proposal: ID, version and digest.
        if (receipt.InstanceId != check.InstanceId || receipt.ProposalId != p.Id ||
            receipt.Version != p.Version || receipt.Digest != p.Digest)
            return new(false, false, "Receipt does not bind this instance and immutable itinerary.");
        // Only the local fixture approver may decide.
        if (receipt.Actor != "demo-traveler" || receipt.Role != "local-mock-trip-approver")
            return new(false, false, "Actor is not the LOCAL FIXTURE approver.");
        // Issued after the proposal was created and before its deadline, and the deadline hasn't passed now.
        if (receipt.IssuedUtc < p.CreatedUtc || !IsBeforeDeadline(receipt.IssuedUtc, p.DeadlineUtc) ||
            !IsBeforeDeadline(now, p.DeadlineUtc))
            return new(false, false, "Approval deadline expired.");
        // The total must match the fixed fake prices and fit the trip's budget.
        if (p.TotalEstimate != MockBookingClient.TotalEstimate || p.Trip.Budget < p.TotalEstimate)
            return new(false, false, "Host budget policy rejected the proposal.");
        // A valid receipt: authorized, and approved or denied as the traveler chose.
        return new(true, receipt.Approved, receipt.Approved ? "Approved by LOCAL FIXTURE authority." : "Denied.");
    }
}

// JSON settings for the ledger's files and the CLI output: indented, case-insensitive, enums as strings.
public static class DemoJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
