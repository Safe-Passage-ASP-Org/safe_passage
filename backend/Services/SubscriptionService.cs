using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Contracts.Subscriptions;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public enum SubscriptionFailure
{
    None = 0,
    UserUnavailable,
    InvalidPlan,
    PlanNotConfigured,
    AlreadySubscribed,
    NotFound,
    OwnershipMismatch,
    InvalidState,
    InvalidWebhookSignature,
    ProviderError
}

public sealed record SubscriptionResult<T>(
    SubscriptionFailure Failure,
    T? Data = default,
    string? ErrorDetail = null);

public interface ISubscriptionService
{
    IReadOnlyList<SubscriptionPlanResponse> GetPlans();
    Task<UserSubscriptionResponse> GetUserSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> HasActiveEntitlementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<CreateCheckoutResponse>> CreateCheckoutAsync(Guid userId, string planCode, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<UserSubscriptionResponse>> ConfirmSubscriptionAsync(Guid userId, string subscriptionId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<bool>> AbandonPendingAsync(Guid userId, string subscriptionId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<UserSubscriptionResponse>> CancelSubscriptionAsync(Guid userId, string? reason, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<bool>> ProcessWebhookAsync(PayPalWebhookHeaders headers, string rawBody, CancellationToken cancellationToken = default);
}

public sealed class SubscriptionService(
    ApplicationDbContext dbContext,
    IPayPalClient payPalClient,
    IOptions<PayPalOptions> optionsAccessor,
    IConfiguration? configuration = null) : ISubscriptionService
{
    public IReadOnlyList<SubscriptionPlanResponse> GetPlans()
    {
        var options = optionsAccessor.Value;
        return options.Plans
            .Select(kvp => new SubscriptionPlanResponse(
                kvp.Key,
                string.IsNullOrWhiteSpace(kvp.Value.Name) ? kvp.Key : kvp.Value.Name,
                kvp.Value.Description,
                kvp.Value.PriceDisplay,
                kvp.Value.Interval,
                options.HasCredentials && !string.IsNullOrWhiteSpace(kvp.Value.PayPalPlanId)))
            .ToList();
    }

    public async Task<UserSubscriptionResponse> GetUserSubscriptionAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var current = SelectCurrentSubscription(subscriptions, now);
        return current is null
            ? ToFreeResponse()
            : ToResponse(current, now);
    }

    public async Task<bool> HasActiveEntitlementAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetUserSubscriptionAsync(userId, cancellationToken);
        return current.HasActiveEntitlement;
    }

    public async Task<SubscriptionResult<CreateCheckoutResponse>> CreateCheckoutAsync(
        Guid userId,
        string planCode,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
            return new(SubscriptionFailure.UserUnavailable, ErrorDetail: "The account is not active.");

        var options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(planCode) || !options.Plans.TryGetValue(planCode.Trim(), out var plan))
            return new(SubscriptionFailure.InvalidPlan, ErrorDetail: "The requested subscription plan does not exist.");

        if (!options.HasCredentials || string.IsNullOrWhiteSpace(plan.PayPalPlanId))
            return new(SubscriptionFailure.PlanNotConfigured, ErrorDetail: "PayPal Sandbox credentials or plan ID are not configured on the server.");

        var normalizedPlanCode = options.Plans.Keys.First(k => string.Equals(k, planCode.Trim(), StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;

        var existing = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        if (existing.Any(sub => HasActiveEntitlement(sub, now)))
            return new(SubscriptionFailure.AlreadySubscribed, ErrorDetail: "You already have an active subscription.");

        var ttl = TimeSpan.FromMinutes(Math.Max(5, options.PendingCheckoutTtlMinutes));
        var pending = existing
            .Where(sub => sub.Status == SubscriptionStatus.ApprovalPending)
            .OrderByDescending(sub => sub.UpdatedAt)
            .FirstOrDefault();

        if (pending is not null)
        {
            var isFresh = now - pending.UpdatedAt < ttl;
            if (isFresh
                && string.Equals(pending.PlanCode, normalizedPlanCode, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pending.ApprovalUrl))
            {
                return new(SubscriptionFailure.None, new CreateCheckoutResponse(
                    pending.PayPalSubscriptionId,
                    pending.ApprovalUrl,
                    pending.PlanCode,
                    ReusedPending: true));
            }

            foreach (var stale in existing.Where(sub => sub.Status == SubscriptionStatus.ApprovalPending))
            {
                stale.Status = SubscriptionStatus.Expired;
                stale.UpdatedAt = now;
            }
        }

        PayPalCreateSubscriptionResult created;
        try
        {
            var (returnUrl, cancelUrl) = ResolveCallbackUrls(options);
            created = await payPalClient.CreateSubscriptionAsync(
                plan.PayPalPlanId,
                userId.ToString(),
                returnUrl,
                cancelUrl,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new(SubscriptionFailure.ProviderError, ErrorDetail: ex.Message);
        }

        var record = new UserSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PayPalSubscriptionId = created.SubscriptionId,
            PayPalPlanId = plan.PayPalPlanId,
            PlanCode = normalizedPlanCode,
            Status = SubscriptionStatus.ApprovalPending,
            ApprovalUrl = created.ApprovalUrl,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.UserSubscriptions.Add(record);
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = "SubscriptionCheckoutCreated",
            Resource = $"{normalizedPlanCode}:{created.SubscriptionId}",
            Status = ActivityLogStatus.Success,
            Timestamp = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new(SubscriptionFailure.None, new CreateCheckoutResponse(
            created.SubscriptionId,
            created.ApprovalUrl,
            normalizedPlanCode,
            ReusedPending: false));
    }

    public async Task<SubscriptionResult<UserSubscriptionResponse>> ConfirmSubscriptionAsync(
        Guid userId,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return new(SubscriptionFailure.NotFound, ErrorDetail: "Subscription ID is required.");

        var trimmedId = subscriptionId.Trim();
        PayPalSubscriptionDetails details;
        try
        {
            details = await payPalClient.GetSubscriptionAsync(trimmedId, cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return new(SubscriptionFailure.NotFound, ErrorDetail: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new(SubscriptionFailure.ProviderError, ErrorDetail: ex.Message);
        }

        var now = DateTimeOffset.UtcNow;

        // Enforce server-side binding: PayPal custom_id must match the authenticated user.
        if (!Guid.TryParse(details.CustomId, out var ownerId) || ownerId != userId)
        {
            dbContext.ActivityLogs.Add(new ActivityLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Action = "SubscriptionOwnershipMismatch",
                Resource = trimmedId,
                Status = ActivityLogStatus.Denied,
                Timestamp = now
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "This PayPal subscription does not belong to the signed-in user.");
        }

        var local = await dbContext.UserSubscriptions
            .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == trimmedId, cancellationToken);

        if (local is null)
        {
            local = new UserSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PayPalSubscriptionId = trimmedId,
                PayPalPlanId = details.PlanId,
                PlanCode = ResolvePlanCode(details.PlanId),
                Status = SubscriptionStatus.ApprovalPending,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.UserSubscriptions.Add(local);
        }
        else if (local.UserId != userId)
        {
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "This subscription belongs to another account.");
        }

        var mappedStatus = MapPayPalStatus(details.Status);
        ApplyProviderStatus(local, mappedStatus, details.StartTime, details.NextBillingTime, now);

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = "SubscriptionConfirmed",
            Resource = $"{local.PlanCode}:{trimmedId}:{local.Status}",
            Status = ActivityLogStatus.Success,
            Timestamp = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(SubscriptionFailure.None, ToResponse(local, now));
    }

    public async Task<SubscriptionResult<bool>> AbandonPendingAsync(
        Guid userId,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return new(SubscriptionFailure.NotFound, ErrorDetail: "Subscription ID is required.");

        var local = await dbContext.UserSubscriptions
            .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == subscriptionId.Trim(), cancellationToken);

        if (local is null)
            return new(SubscriptionFailure.NotFound, ErrorDetail: "Pending subscription was not found.");

        if (local.UserId != userId)
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "This subscription belongs to another account.");

        if (local.Status == SubscriptionStatus.ApprovalPending)
        {
            local.Status = SubscriptionStatus.Expired;
            local.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new(SubscriptionFailure.None, true);
    }

    public async Task<SubscriptionResult<UserSubscriptionResponse>> CancelSubscriptionAsync(
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        var active = subscriptions
            .Where(sub => sub.Status == SubscriptionStatus.Active)
            .OrderByDescending(sub => sub.UpdatedAt)
            .FirstOrDefault();

        if (active is null)
            return new(SubscriptionFailure.InvalidState, ErrorDetail: "No active subscription was found to cancel.");

        var cancelReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by user in Safe Passage" : reason.Trim();
        try
        {
            await payPalClient.CancelSubscriptionAsync(active.PayPalSubscriptionId, cancelReason, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new(SubscriptionFailure.ProviderError, ErrorDetail: ex.Message);
        }

        var now = DateTimeOffset.UtcNow;
        active.Status = SubscriptionStatus.Cancelled;
        active.CancelledAt ??= now;
        active.UpdatedAt = now;

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = "SubscriptionCancelled",
            Resource = $"{active.PlanCode}:{active.PayPalSubscriptionId}",
            Status = ActivityLogStatus.Success,
            Timestamp = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(SubscriptionFailure.None, ToResponse(active, now));
    }

    public async Task<SubscriptionResult<bool>> ProcessWebhookAsync(
        PayPalWebhookHeaders headers,
        string rawBody,
        CancellationToken cancellationToken = default)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return new(SubscriptionFailure.InvalidWebhookSignature, ErrorDetail: "Invalid JSON payload.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var verified = await payPalClient.VerifyWebhookSignatureAsync(headers, root, cancellationToken);
            if (!verified)
                return new(SubscriptionFailure.InvalidWebhookSignature, ErrorDetail: "PayPal webhook signature verification failed.");

            var eventId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var eventType = root.TryGetProperty("event_type", out var typeProp) ? typeProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType))
                return new(SubscriptionFailure.None, true);

            var alreadyProcessed = await dbContext.SubscriptionWebhookEvents
                .AnyAsync(evt => evt.PayPalEventId == eventId, cancellationToken);
            if (alreadyProcessed)
                return new(SubscriptionFailure.None, true);

            string? subscriptionId = null;
            string? customId = null;
            string? resourceStatus = null;
            DateTimeOffset? nextBillingTime = null;

            if (root.TryGetProperty("resource", out var resource))
            {
                if (resource.TryGetProperty("id", out var resId))
                    subscriptionId = resId.GetString();
                if (resource.TryGetProperty("custom_id", out var resCustomId))
                    customId = resCustomId.GetString();
                if (resource.TryGetProperty("status", out var resStatus))
                    resourceStatus = resStatus.GetString();
                if (resource.TryGetProperty("billing_info", out var billingInfo)
                    && billingInfo.TryGetProperty("next_billing_time", out var nextBillingProp)
                    && nextBillingProp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(nextBillingProp.GetString(), out var parsedNextBilling))
                {
                    nextBillingTime = parsedNextBilling;
                }
            }

            var now = DateTimeOffset.UtcNow;
            dbContext.SubscriptionWebhookEvents.Add(new SubscriptionWebhookEvent
            {
                Id = Guid.NewGuid(),
                PayPalEventId = eventId,
                EventType = eventType,
                PayPalSubscriptionId = subscriptionId,
                ProcessedAt = now,
                PayloadSummary = $"{eventType} ({resourceStatus ?? "n/a"})"
            });

            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                var local = await dbContext.UserSubscriptions
                    .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == subscriptionId, cancellationToken);

                if (local is not null)
                {
                    var isOwnerValid = string.IsNullOrWhiteSpace(customId)
                        || (Guid.TryParse(customId, out var parsedOwner) && parsedOwner == local.UserId);

                    if (isOwnerValid)
                    {
                        var mapped = MapWebhookEventToStatus(eventType, resourceStatus);
                        if (mapped.HasValue)
                        {
                            ApplyProviderStatus(local, mapped.Value, null, nextBillingTime, now);
                            dbContext.ActivityLogs.Add(new ActivityLog
                            {
                                Id = Guid.NewGuid(),
                                UserId = local.UserId,
                                Action = $"Webhook:{eventType}",
                                Resource = $"{local.PlanCode}:{subscriptionId}:{local.Status}",
                                Status = ActivityLogStatus.Success,
                                Timestamp = now
                            });
                        }
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return new(SubscriptionFailure.None, true);
        }
    }

    private static void ApplyProviderStatus(
        UserSubscription local,
        SubscriptionStatus incomingStatus,
        DateTimeOffset? startTime,
        DateTimeOffset? nextBillingTime,
        DateTimeOffset now)
    {
        // Guard against out-of-order downgrade from Active/Cancelled/Suspended back to ApprovalPending.
        if (incomingStatus == SubscriptionStatus.ApprovalPending && local.Status != SubscriptionStatus.ApprovalPending)
            return;

        local.Status = incomingStatus;
        local.UpdatedAt = now;

        if (startTime.HasValue && !local.CurrentPeriodStart.HasValue)
            local.CurrentPeriodStart = startTime.Value;

        if (incomingStatus == SubscriptionStatus.Active)
        {
            local.CurrentPeriodStart ??= now;
            if (nextBillingTime.HasValue)
                local.CurrentPeriodEnd = nextBillingTime.Value;
            local.CancelledAt = null;
        }
        else if (incomingStatus == SubscriptionStatus.Cancelled)
        {
            local.CancelledAt ??= now;
            if (nextBillingTime.HasValue)
                local.CurrentPeriodEnd ??= nextBillingTime.Value;
        }
    }

    private static SubscriptionStatus MapPayPalStatus(string status) => status.ToUpperInvariant() switch
    {
        "ACTIVE" => SubscriptionStatus.Active,
        "APPROVAL_PENDING" or "APPROVED" => SubscriptionStatus.ApprovalPending,
        "SUSPENDED" => SubscriptionStatus.Suspended,
        "CANCELLED" => SubscriptionStatus.Cancelled,
        "EXPIRED" => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.ApprovalPending
    };

    private static SubscriptionStatus? MapWebhookEventToStatus(string eventType, string? resourceStatus) =>
        eventType.ToUpperInvariant() switch
        {
            "BILLING.SUBSCRIPTION.ACTIVATED" or "BILLING.SUBSCRIPTION.RE-ACTIVATED" => SubscriptionStatus.Active,
            "BILLING.SUBSCRIPTION.SUSPENDED" or "PAYMENT.SALE.DENIED" => SubscriptionStatus.Suspended,
            "BILLING.SUBSCRIPTION.CANCELLED" => SubscriptionStatus.Cancelled,
            "BILLING.SUBSCRIPTION.EXPIRED" => SubscriptionStatus.Expired,
            _ => string.IsNullOrWhiteSpace(resourceStatus) ? null : MapPayPalStatus(resourceStatus)
        };

    private string ResolvePlanCode(string payPalPlanId)
    {
        var match = optionsAccessor.Value.Plans
            .FirstOrDefault(kvp => string.Equals(kvp.Value.PayPalPlanId, payPalPlanId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match.Key) ? payPalPlanId : match.Key;
    }

    private static bool HasActiveEntitlement(UserSubscription subscription, DateTimeOffset now) =>
        subscription.Status == SubscriptionStatus.Active
        || (subscription.Status == SubscriptionStatus.Cancelled
            && subscription.CurrentPeriodEnd.HasValue
            && subscription.CurrentPeriodEnd.Value > now);

    private static UserSubscription? SelectCurrentSubscription(IReadOnlyList<UserSubscription> subscriptions, DateTimeOffset now)
    {
        return subscriptions
            .OrderByDescending(sub => sub.Status == SubscriptionStatus.Active)
            .ThenByDescending(sub => HasActiveEntitlement(sub, now))
            .ThenByDescending(sub => sub.Status == SubscriptionStatus.ApprovalPending)
            .ThenByDescending(sub => sub.UpdatedAt)
            .FirstOrDefault();
    }

    private UserSubscriptionResponse ToResponse(UserSubscription subscription, DateTimeOffset now)
    {
        optionsAccessor.Value.Plans.TryGetValue(subscription.PlanCode, out var planOption);
        var activeEntitlement = HasActiveEntitlement(subscription, now);
        var cancelAtPeriodEnd = subscription.Status == SubscriptionStatus.Cancelled && activeEntitlement;

        return new UserSubscriptionResponse(
            activeEntitlement,
            subscription.Status.ToString(),
            subscription.PlanCode,
            planOption?.Name ?? subscription.PlanCode,
            subscription.PayPalSubscriptionId,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.CancelledAt,
            cancelAtPeriodEnd,
            subscription.Status == SubscriptionStatus.ApprovalPending ? subscription.ApprovalUrl : null);
    }

    private (string ReturnUrl, string CancelUrl) ResolveCallbackUrls(PayPalOptions options)
    {
        var configuredFrontend = configuration?["Frontend:BaseUrl"];
        if (string.IsNullOrWhiteSpace(configuredFrontend))
        {
            var loginUrl = configuration?["OpenIddict:LoginUrl"];
            if (Uri.TryCreate(loginUrl, UriKind.Absolute, out var loginUri)
                && !loginUri.IsLoopback)
            {
                configuredFrontend = loginUri.GetLeftPart(UriPartial.Authority);
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredFrontend)
            && Uri.TryCreate(configuredFrontend.TrimEnd('/'), UriKind.Absolute, out var frontendUri))
        {
            var returnUrl = IsUnsetOrLoopback(options.ReturnUrl)
                ? new Uri(frontendUri, "/subscription/paypal-callback?status=success").ToString()
                : options.ReturnUrl;
            var cancelUrl = IsUnsetOrLoopback(options.CancelUrl)
                ? new Uri(frontendUri, "/subscription/paypal-callback?status=cancel").ToString()
                : options.CancelUrl;
            return (returnUrl, cancelUrl);
        }

        return (options.ReturnUrl, options.CancelUrl);
    }

    private static bool IsUnsetOrLoopback(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback);

    private static UserSubscriptionResponse ToFreeResponse() =>
        new(
            HasActiveEntitlement: false,
            Status: "Free",
            PlanCode: null,
            PlanName: "Free",
            PayPalSubscriptionId: null,
            CurrentPeriodStart: null,
            CurrentPeriodEnd: null,
            CancelledAt: null,
            CancelAtPeriodEnd: false,
            PendingApprovalUrl: null);
}
