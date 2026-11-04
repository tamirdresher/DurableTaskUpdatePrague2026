using Azure.Identity;
using BookingDemo;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;

namespace BookingIntro;

// Configuration-only: compiled/DI-tested, never cloud-executed or provisioned by this demo.
public static class ManagedHostingExample
{
    public static void ConfigureWorker(IServiceCollection services, string endpoint, string taskHub, BookingLedger ledger)
    {
        services.AddSingleton(ledger);
        services.AddSingleton<MockBookingClient>();
        var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        services.AddDurableTaskWorker().AddTasks(registry =>
        {
            registry.AddOrchestrator<BookTrip>();
            registry.AddActivity<BookFlight>();
            registry.AddActivity<BookHotel>();
            registry.AddActivity<BookCar>();
        }).UseDurableTaskScheduler(endpoint, taskHub, credential);
    }

    public static void ConfigureClient(IServiceCollection services, string endpoint, string taskHub)
    {
        var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(endpoint, taskHub, credential));
    }
}
