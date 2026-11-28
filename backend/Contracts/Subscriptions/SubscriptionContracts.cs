namespace SafePassage.Api.Contracts.Subscriptions;

public sealed record SubscriptionPlanResponse(
    string PlanCode,
    string Name,
    string Description,
    string PriceDisplay,
    string Interval,
    bool IsConfigured,
    string Mode = "paymongo");

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

public sealed record SubscriptionEntitlementsResponse(
    string PlanCode,
    bool IsPro,
    int? MaxTrustedDevices,
    int? MaxSavedSites,
    int ActivityHistoryDays,
    bool CanGenerateTotpCodes);

public sealed record CreateCheckoutRequest(string PlanCode);

public sealed record CreateCheckoutResponse(
    string PayPalSubscriptionId,
    string ApprovalUrl,
    string PlanCode,
    bool ReusedPending);

public sealed record ConfirmSubscriptionRequest(string SubscriptionId);

public sealed record AbandonSubscriptionRequest(string SubscriptionId);

public sealed record CancelSubscriptionRequest(string? Reason = null);

public sealed record CompleteDemoCheckoutRequest(
    string SessionId,
    string PaymentMethod,
    string Outcome,
    string? DemoToken = null);

public sealed record SubscriptionHistoryItemResponse(
    string SubscriptionId,
    string PlanCode,
    string PlanName,
    string Status,
    string PriceDisplay,
    string PaymentMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? CancelledAt);

public sealed record ResendReceiptResponse(
    bool Sent,
    string Message);
