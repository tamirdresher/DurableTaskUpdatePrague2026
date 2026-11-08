using Microsoft.DurableTask;

namespace BookingDemo;

// Activities are ordinary classes, created through dependency injection; this is where the I/O happens.
// The scheduler runs an activity at least once: if the worker dies before the result is saved, it runs
// again. That's why every BookingCommand carries a stable operation ID for the provider.
public sealed class BookFlight(MockBookingClient booking) : TaskActivity<BookingCommand, BookingReceipt>
{
    public override Task<BookingReceipt> RunAsync(TaskActivityContext context, BookingCommand input) =>
        booking.BookFlightAsync(input);
}

public sealed class BookHotel(MockBookingClient booking) : TaskActivity<BookingCommand, BookingReceipt>
{
    public override Task<BookingReceipt> RunAsync(TaskActivityContext context, BookingCommand input) =>
        booking.BookHotelAsync(input);
}

public sealed class BookCar(MockBookingClient booking) : TaskActivity<BookingCommand, BookingReceipt>
{
    public override Task<BookingReceipt> RunAsync(TaskActivityContext context, BookingCommand input) =>
        booking.BookCarAsync(input);
}
