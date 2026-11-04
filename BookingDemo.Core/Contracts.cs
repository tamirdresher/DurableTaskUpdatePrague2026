using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookingDemo;

public sealed record Trip(string Id, string From = "SFO", string Destination = "Seattle",
    string Departure = "2026-11-10", string Return = "2026-11-14", decimal Budget = 1500m);

public sealed record BookingCommand(string OperationId, string Service, Trip Trip, ApprovalCheck? Approval = null)
{
    public string Digest => Hash(new { Service, Trip, ProposalDigest = Approval?.Proposal.Digest });
    internal static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

public sealed record BookingReceipt(string OperationId, string Service, string TripId,
    string CommandDigest, string Confirmation, decimal Price, string Provider = "LOCAL MOCK - NOT A REAL RESERVATION");
public sealed record TripResult(string Outcome, BookingReceipt[] Bookings);
public sealed record TripInput(Trip Trip, int ApprovalSeconds = 120);
public sealed record ItineraryProposal(string Id, int Version, Trip Trip, string PlannerOutput,
    string ReviewerOutput, decimal TotalEstimate, DateTime CreatedUtc, DateTime DeadlineUtc)
{
    public string Digest => BookingCommand.Hash(new
    {
        Id, Version, Trip, PlannerOutput, ReviewerOutput, TotalEstimate, CreatedUtc, DeadlineUtc
    });
}
public sealed record ProposalDraft(string Id, Trip Trip, string Planner, string Reviewer,
    DateTime CreatedUtc, DateTime DeadlineUtc);
public sealed record ApprovalSignal(string ReceiptId);
public sealed record ApprovalReceipt(string ReceiptId, string InstanceId, string ProposalId, int Version,
    string Digest, string Actor, string Role, bool Approved, DateTime IssuedUtc);
public sealed record ApprovalCheck(string InstanceId, ItineraryProposal Proposal, ApprovalSignal Signal);
public sealed record ApprovalVerdict(bool Authorized, bool Approved, string Reason);
public sealed record BookingStatus(string Phase, ItineraryProposal? Proposal = null, string? Detail = null);
public sealed record PlannedTripResult(string Outcome, ItineraryProposal Proposal, BookingReceipt[] Bookings);

public static class ApprovalPolicy
{
    public static bool IsBeforeDeadline(DateTime now, DateTime deadline) => now < deadline;
    public static ApprovalVerdict Evaluate(ApprovalCheck check, ApprovalReceipt? receipt, DateTime now)
    {
        if (receipt is null || receipt.ReceiptId != check.Signal.ReceiptId)
            return new(false, false, "Unknown receipt: an event is not authorization.");
        ItineraryProposal p = check.Proposal;
        if (receipt.InstanceId != check.InstanceId || receipt.ProposalId != p.Id ||
            receipt.Version != p.Version || receipt.Digest != p.Digest)
            return new(false, false, "Receipt does not bind this instance and immutable itinerary.");
        if (receipt.Actor != "demo-traveler" || receipt.Role != "local-mock-trip-approver")
            return new(false, false, "Actor is not the LOCAL FIXTURE approver.");
        if (receipt.IssuedUtc < p.CreatedUtc || !IsBeforeDeadline(receipt.IssuedUtc, p.DeadlineUtc) ||
            !IsBeforeDeadline(now, p.DeadlineUtc))
            return new(false, false, "Approval deadline expired.");
        if (p.TotalEstimate != MockBookingClient.TotalEstimate || p.Trip.Budget < p.TotalEstimate)
            return new(false, false, "Host budget policy rejected the proposal.");
        return new(true, receipt.Approved, receipt.Approved ? "Approved by LOCAL FIXTURE authority." : "Denied.");
    }
}

public static class DemoJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
