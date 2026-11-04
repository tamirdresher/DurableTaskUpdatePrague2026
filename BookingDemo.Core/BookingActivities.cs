using Microsoft.DurableTask;

namespace BookingDemo;

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
