using System.Diagnostics;

namespace BookingDemo;

public sealed class MockBookingClient(BookingLedger ledger)
{
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
        CrashHook.Hit(ledger, "after-hotel-effect", command.OperationId);
        return Task.FromResult(receipt);
    }

    public Task<BookingReceipt> BookCarAsync(BookingCommand command)
    {
        CrashHook.Hit(ledger, "before-car", command.OperationId);
        return Task.FromResult(Book(command, "car"));
    }

    private BookingReceipt Book(BookingCommand command, string expectedService)
    {
        if (command.Service != expectedService) throw new ArgumentException("Wrong booking activity/service.");
        return ledger.Book(command, DateTime.UtcNow);
    }
}

public static class CrashHook
{
    public static void Hit(BookingLedger ledger, string checkpoint, string operationId)
    {
        if (Environment.GetEnvironmentVariable("PRAGUE_BOOKING_CRASH") != checkpoint ||
            !ledger.MarkCrashOnce(checkpoint, operationId)) return;
        Console.WriteLine($"CONTROLLED CRASH: {checkpoint}; owned PID={Environment.ProcessId}; {operationId}");
        Console.Out.Flush();
        // Kill this task-owned process only. The Scheduler emulator and provider ledger stay alive.
        Process.GetCurrentProcess().Kill();
        throw new InvalidOperationException("Controlled self-kill did not terminate the process.");
    }
}

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
