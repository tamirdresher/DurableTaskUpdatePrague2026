using System.Diagnostics;

namespace BookingDemo;

// A fake booking provider client: nothing is really booked. Bookings are files in BookingLedger,
// and every receipt says "LOCAL MOCK - NOT A REAL RESERVATION".
public sealed class MockBookingClient(BookingLedger ledger)
{
    // Fixed fake prices that add up to TotalEstimate; ApprovalPolicy rejects a proposal with any other total.
    public const decimal TotalEstimate = 1200m;
    public static decimal Price(string service) => service switch
    {
        "flight" => 420m,
        "hotel" => 600m,
        "car" => 180m,
        _ => throw new ArgumentException("Only MOCK flight, hotel and car are supported.")
    };

    public Task<BookingReceipt> BookFlightAsync(BookingCommand command) =>
        Task.FromResult(Book(command, "flight"));

    public Task<BookingReceipt> BookHotelAsync(BookingCommand command)
    {
        BookingReceipt receipt = Book(command, "hotel");
        // Crash checkpoint "after-hotel-effect": the hotel is booked, but the worker dies before the scheduler
        // hears back. The hotel activity runs again, and its operation ID returns the same booking.
        CrashHook.Hit(ledger, "after-hotel-effect", command.OperationId);
        return Task.FromResult(receipt);
    }

    public Task<BookingReceipt> BookCarAsync(BookingCommand command)
    {
        // Crash checkpoint "before-car": the worker dies before the car is booked.
        CrashHook.Hit(ledger, "before-car", command.OperationId);
        return Task.FromResult(Book(command, "car"));
    }

    private BookingReceipt Book(BookingCommand command, string expectedService)
    {
        if (command.Service != expectedService)
            throw new ArgumentException("Wrong booking activity/service.");
        return ledger.Book(command, DateTime.UtcNow);
    }
}

// Simulates a crash at the checkpoint named in PRAGUE_BOOKING_CRASH. The ledger keeps a marker, so each
// crash happens once per operation, and a restarted worker can finish even with the same setting.
public static class CrashHook
{
    public static void Hit(BookingLedger ledger, string checkpoint, string operationId)
    {
        if (Environment.GetEnvironmentVariable("PRAGUE_BOOKING_CRASH") != checkpoint ||
            !ledger.MarkCrashOnce(checkpoint, operationId))
            return;
        // Stage.ps1 looks for this line in the worker's log, so flush it before the process dies.
        Console.WriteLine($"CONTROLLED CRASH: {checkpoint}; owned PID={Environment.ProcessId}; {operationId}");
        Console.Out.Flush();
        // Kill this task-owned process only. The Scheduler emulator and provider ledger stay alive.
        Process.GetCurrentProcess().Kill();
        throw new InvalidOperationException("Controlled self-kill did not terminate the process.");
    }
}

// Where the talk starts: three plain awaits. Progress lives only in local variables, so a crash loses it,
// and the new attempt ID on every run gives the provider new keys, so it can't deduplicate.
public static class NaiveTrip
{
    public static async Task<TripResult> BookAsync(MockBookingClient booking, Trip trip, string attemptId)
    {
        BookingReceipt flight = await booking.BookFlightAsync(new($"{attemptId}/flight", "flight", trip));
        BookingReceipt hotel = await booking.BookHotelAsync(new($"{attemptId}/hotel", "hotel", trip));
        BookingReceipt car = await booking.BookCarAsync(new($"{attemptId}/car", "car", trip));
        return new("Booked", [flight, hotel, car]);
    }
}
