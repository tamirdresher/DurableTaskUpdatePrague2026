using BookingDemo;
using Microsoft.Extensions.AI;

namespace BookingAgents;

// Actual MAF integration, but these responses are OFFLINE SCRIPTED FIXTURES, not LLM inference.
public sealed class ScriptedTripChatClient(string specialist, BookingLedger ledger) : IChatClient
{
    public const string PlannerText = "OFFLINE SCRIPTED TRIP PLANNER: SFO to Seattle, 2026-11-10 to 2026-11-14; MOCK flight $420, hotel $600, car $180; total $1200.";
    public const string ReviewerText = "OFFLINE SCRIPTED BUDGET REVIEWER: fixture itinerary $1200 is under the $1500 sample budget; require bound traveler approval; MOCK providers only.";
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ledger.Audit("fixture-model-call", new { specialist, model = "OFFLINE SCRIPTED FIXTURE" });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            specialist == "TripPlanner" ? PlannerText : ReviewerText))
        {
            ResponseId = Guid.NewGuid().ToString("N"),
            ModelId = "OFFLINE-SCRIPTED-NOT-AN-LLM"
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
