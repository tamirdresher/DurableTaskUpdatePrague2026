using Azure.Identity;
using BookingDemo;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;

namespace BookingIntro;

// The Azure version of the worker and client setup: the same tasks, connected to a Durable Task Scheduler
// in Azure with a managed identity instead of the emulator's local, no-auth connection.
// Configuration-only: compiled/DI-tested, never cloud-executed or provisioned by this demo.
public static class ManagedHostingExample
{
    public static void ConfigureWorker(IServiceCollection services, string endpoint, string taskHub, BookingLedger ledger)
    {
        services.AddSingleton(ledger);
        services.AddSingleton<MockBookingClient>();
        // A system-assigned managed identity: no connection secret to store or rotate.
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
        // Clients (start, status, raise events) authenticate the same way.
        var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(endpoint, taskHub, credential));
    }
}
