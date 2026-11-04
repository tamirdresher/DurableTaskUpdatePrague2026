using BookingDemo;
using Microsoft.DurableTask;

namespace BookingIntro;

public sealed class BookTrip : TaskOrchestrator<Trip, TripResult>
{
    public override async Task<TripResult> RunAsync(TaskOrchestrationContext context, Trip trip)
    {
        BookingReceipt flight = await context.CallActivityAsync<BookingReceipt>(nameof(BookFlight),
            new BookingCommand($"{context.InstanceId}/flight", "flight", trip));
        BookingReceipt hotel = await context.CallActivityAsync<BookingReceipt>(nameof(BookHotel),
            new BookingCommand($"{context.InstanceId}/hotel", "hotel", trip));
        BookingReceipt car = await context.CallActivityAsync<BookingReceipt>(nameof(BookCar),
            new BookingCommand($"{context.InstanceId}/car", "car", trip));
        return new("Booked", [flight, hotel, car]);
    }
}
