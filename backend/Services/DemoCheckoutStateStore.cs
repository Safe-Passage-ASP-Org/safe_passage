using Microsoft.Extensions.Caching.Memory;

namespace SafePassage.Api.Services;

public sealed record DemoCheckoutOutcome(
    Guid UserId,
    string SessionId,
    string Outcome,
    string PaymentMethod,
    DateTimeOffset RecordedAt);

/// <summary>
/// Thread-safe in-memory store for Sandbox/Demo checkout outcomes keyed by demo session ID.
/// </summary>
public sealed class DemoCheckoutStateStore(IMemoryCache cache)
{
    private static string CacheKey(string sessionId) => $"demo-checkout:{sessionId}";

    public void SetOutcome(Guid userId, string sessionId, string outcome, string paymentMethod, DateTimeOffset recordedAt)
    {
        var entry = new DemoCheckoutOutcome(userId, sessionId, outcome, paymentMethod, recordedAt);
        cache.Set(CacheKey(sessionId), entry, TimeSpan.FromHours(2));
    }

    public bool TryGetOutcome(string sessionId, out DemoCheckoutOutcome? outcome) =>
        cache.TryGetValue(CacheKey(sessionId), out outcome);
}
