using System.Text.Json;

namespace SafePassage.Api.Services;

public sealed record PayPalCreateSubscriptionResult(
    string SubscriptionId,
    string Status,
    string ApprovalUrl);

public sealed record PayPalSubscriptionDetails(
    string SubscriptionId,
    string Status,
    string PlanId,
    string? CustomId,
    DateTimeOffset? StartTime,
    DateTimeOffset? NextBillingTime,
    DateTimeOffset? StatusUpdateTime);

public sealed record PayPalWebhookHeaders(
    string TransmissionId,
    string TransmissionTime,
    string TransmissionSig,
    string CertUrl,
    string AuthAlgo);

public interface IPayPalClient
{
    Task<PayPalCreateSubscriptionResult> CreateSubscriptionAsync(
        string payPalPlanId,
        string customId,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);

    Task<PayPalSubscriptionDetails> GetSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task CancelSubscriptionAsync(
        string subscriptionId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyWebhookSignatureAsync(
        PayPalWebhookHeaders headers,
        JsonElement rawWebhookEvent,
        CancellationToken cancellationToken = default);
}
