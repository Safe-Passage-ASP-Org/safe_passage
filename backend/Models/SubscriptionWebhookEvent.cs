namespace SafePassage.Api.Models;

public sealed class SubscriptionWebhookEvent
{
    public Guid Id { get; set; }
    public string PayPalEventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? PayPalSubscriptionId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PayloadSummary { get; set; }
}
