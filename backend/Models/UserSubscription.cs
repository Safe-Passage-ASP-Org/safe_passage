namespace SafePassage.Api.Models;

public enum SubscriptionStatus
{
    ApprovalPending = 0,
    Active = 1,
    Suspended = 2,
    Cancelled = 3,
    Expired = 4
}

public sealed class UserSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string PayPalSubscriptionId { get; set; } = string.Empty;
    public string PayPalPlanId { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.ApprovalPending;
    public string? ApprovalUrl { get; set; }
    public DateTimeOffset? CurrentPeriodStart { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset? ExpirationReminderPeriodEnd { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
}
