namespace SafePassage.Api.Contracts.Subscriptions;

public sealed record SubscriptionPlanResponse(
    string PlanCode,
    string Name,
    string Description,
    string PriceDisplay,
    string Interval,
    bool IsConfigured);

public sealed record UserSubscriptionResponse(
    bool HasActiveEntitlement,
    string Status,
    string? PlanCode,
    string? PlanName,
    string? PayPalSubscriptionId,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? CancelledAt,
    bool CancelAtPeriodEnd,
    string? PendingApprovalUrl);

public sealed record CreateCheckoutRequest(string PlanCode);

public sealed record CreateCheckoutResponse(
    string PayPalSubscriptionId,
    string ApprovalUrl,
    string PlanCode,
    bool ReusedPending);

public sealed record ConfirmSubscriptionRequest(string SubscriptionId);

public sealed record AbandonSubscriptionRequest(string SubscriptionId);

public sealed record CancelSubscriptionRequest(string? Reason = null);
