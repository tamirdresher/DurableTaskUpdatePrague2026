using System.Text.Json;

namespace BookingDemo;

// Local fake provider state, NOT workflow history, a real booking provider or production authorization.
// Everything is a file under Root: bookings, proposals, approval receipts, crash markers and audit.jsonl.
public sealed class BookingLedger
{
    public string Root { get; }
    public BookingLedger(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    // One booking per operation ID, like a real provider's idempotency key: the same key returns the
    // existing booking, and reusing a key for different work throws.
    public BookingReceipt Book(BookingCommand command, DateTime now)
    {
        decimal price = MockBookingClient.Price(command.Service);
        using FileStream gate = Lock();
        string path = Path.Combine(Root, $"booking-{BookingCommand.Hash(command.OperationId)}.json");
        BookingReceipt? existing = Read<BookingReceipt>(path);
        if (existing is not null)
        {
            if (existing.CommandDigest != command.Digest)
                throw new InvalidOperationException("Operation ID reused for different booking work.");
            AuditUnlocked("booking-attempt", new { command.OperationId, command.Service, created = false, receipt = existing });
            return existing; // No new effect, even when a redelivery occurs after approval expiry.
        }

        // Agent bookings carry their approval, and it's checked again here, right before the booking takes
        // effect. The operation ID must also belong to the approved proposal version.
        if (command.Approval is { } check)
        {
            ApprovalVerdict verdict = ValidateUnlocked(check, now);
            if (!verdict.Authorized || !verdict.Approved || check.Proposal.Trip != command.Trip ||
                command.OperationId != $"{check.InstanceId}/{check.Proposal.Id}/v{check.Proposal.Version}/{command.Service}")
                throw new InvalidOperationException("MOCK booking denied: approval/operation binding failed.");
        }

        BookingReceipt receipt = new(command.OperationId, command.Service, command.Trip.Id, command.Digest,
            $"MOCK-{command.Service.ToUpperInvariant()}-{BookingCommand.Hash(command.OperationId)[..12]}", price);
        WriteAtomically(path, receipt);
        AuditUnlocked("booking-attempt", new { command.OperationId, command.Service, created = true, receipt });
        return receipt;
    }

    // Everything the provider has booked: one file per operation ID.
    public BookingReceipt[] Bookings()
    {
        using FileStream gate = Lock();
        return Directory.GetFiles(Root, "booking-*.json").Select(path => Read<BookingReceipt>(path)!).ToArray();
    }

    // Proposals are immutable: recording the same one again changes nothing, and different content
    // under the same ID throws.
    public void RecordProposal(ItineraryProposal proposal)
    {
        using FileStream gate = Lock();
        string path = Path.Combine(Root, $"proposal-{Guid.Parse(proposal.Id):N}.json");
        ItineraryProposal? existing = Read<ItineraryProposal>(path);
        if (existing is not null)
        {
            if (existing.Digest != proposal.Digest)
                throw new InvalidOperationException("Immutable itinerary collision.");
            return;
        }
        WriteAtomically(path, proposal);
        AuditUnlocked("record-itinerary", new { proposal.Id, proposal.Version, proposal.Digest });
    }

    // The traveler's decision, as a receipt bound to this instance and this exact proposal (ID, version
    // and digest). Only the approve and deny commands issue receipts.
    public ApprovalReceipt IssueReceipt(string instanceId, ItineraryProposal proposal, bool approved)
    {
        ApprovalReceipt receipt = new(Guid.NewGuid().ToString("N"), instanceId, proposal.Id, proposal.Version,
            proposal.Digest, "demo-traveler", "local-mock-trip-approver", approved, DateTime.UtcNow);
        using FileStream gate = Lock();
        WriteAtomically(ReceiptPath(receipt.ReceiptId), receipt);
        return receipt;
    }

    public ApprovalVerdict Validate(ApprovalCheck check, DateTime now)
    {
        using FileStream gate = Lock();
        return ValidateUnlocked(check, now);
    }

    private ApprovalVerdict ValidateUnlocked(ApprovalCheck check, DateTime now)
    {
        // Receipt IDs must be GUIDs, so a signal can't point the lookup at some other file.
        if (!Guid.TryParse(check.Signal.ReceiptId, out Guid id))
            return new(false, false, "Malformed receipt ID.");
        return ApprovalPolicy.Evaluate(check, Read<ApprovalReceipt>(ReceiptPath(id.ToString("N"))), now);
    }

    // CrashHook's marker: true the first time a checkpoint fires for an operation, false after that.
    public bool MarkCrashOnce(string checkpoint, string operationId)
    {
        using FileStream gate = Lock();
        string path = Path.Combine(Root, $"crash-{BookingCommand.Hash(new { checkpoint, operationId })}.json");
        if (File.Exists(path))
            return false;
        WriteAtomically(path, new { checkpoint, operationId, pid = Environment.ProcessId });
        AuditUnlocked("controlled-worker-crash", new { checkpoint, operationId, pid = Environment.ProcessId });
        return true;
    }

    public void Audit(string kind, object details)
    {
        using FileStream gate = Lock();
        AuditUnlocked(kind, details);
    }

    // audit.jsonl gets a line for every booking attempt, recorded itinerary, model call, approval check and
    // crash. Stage.ps1 reads it to count model calls and provider attempts.
    private void AuditUnlocked(string kind, object details) => File.AppendAllText(Path.Combine(Root, "audit.jsonl"),
        JsonSerializer.Serialize(new { utc = DateTime.UtcNow, pid = Environment.ProcessId, kind, details }) + Environment.NewLine);
    private string ReceiptPath(string id) => Path.Combine(Root, $"receipt-{Guid.Parse(id):N}.json");
    private static T? Read<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), DemoJson.Options) : default;

    // Atomic write: write a staging file, flush it to disk, then move it into place, so readers see the
    // whole file or nothing. The move never overwrites, so an existing booking can't be replaced.
    private static void WriteAtomically<T>(string path, T value)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            using (FileStream stream = new(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, DemoJson.Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    // An exclusive lock file coordinates the processes that share this folder (workers and CLI commands).
    // If another process holds it, retry for about two seconds.
    private FileStream Lock()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(20);
            }
        }
    }
}
