using BookingDemo;
using Microsoft.DurableTask;

namespace BookingIntro;

// NaiveTrip's three awaits, made durable: the scheduler saves each activity's result in the history.
// After a crash, a new worker replays RunAsync from the top. Awaits that already have a saved result
// return it at once, and the first await without one schedules its activity.
public sealed class BookTrip : TaskOrchestrator<Trip, TripResult>
{
    public override async Task<TripResult> RunAsync(TaskOrchestrationContext context, Trip trip)
    {
        // Stable operation IDs, "<instance>/<service>": a retried activity sends the provider the same key,
        // so the provider returns the existing booking instead of booking twice.
        BookingReceipt flight = await context.CallActivityAsync<BookingReceipt>(nameof(BookFlight),
            new BookingCommand($"{context.InstanceId}/flight", "flight", trip));
        BookingReceipt hotel = await context.CallActivityAsync<BookingReceipt>(nameof(BookHotel),
            new BookingCommand($"{context.InstanceId}/hotel", "hotel", trip));
        BookingReceipt car = await context.CallActivityAsync<BookingReceipt>(nameof(BookCar),
            new BookingCommand($"{context.InstanceId}/car", "car", trip));
        return new("Booked", [flight, hotel, car]);
    }
}
