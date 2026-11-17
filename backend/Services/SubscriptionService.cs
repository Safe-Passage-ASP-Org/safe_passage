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
    Task<SubscriptionResult<bool>> ProcessWebhookAsync(string signatureHeader, string rawBody, CancellationToken cancellationToken = default);
}

public sealed class SubscriptionService(
    ApplicationDbContext dbContext,
    IPayMongoClient payMongoClient,
    IOptions<PayMongoOptions> optionsAccessor,
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
                options.HasCredentials && kvp.Value.AmountCentavos >= 2000))
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

        if (!options.HasCredentials || plan.AmountCentavos < 2000)
            return new(SubscriptionFailure.PlanNotConfigured, ErrorDetail: "PayMongo Test Mode secret key is not configured on the server.");

        var normalizedPlanCode = options.Plans.Keys.First(k => string.Equals(k, planCode.Trim(), StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;

        var existing = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        if (existing.Any(sub => HasEntitlement(sub, now)))
            return new(SubscriptionFailure.AlreadySubscribed, ErrorDetail: "You already have an active subscription.");

        var ttl = TimeSpan.FromMinutes(Math.Max(5, options.PendingCheckoutTtlMinutes));
        var reusablePending = existing
            .Where(sub => sub.Status == SubscriptionStatus.ApprovalPending
                          && string.Equals(sub.PlanCode, normalizedPlanCode, StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(sub.ApprovalUrl)
                          && now - sub.CreatedAt < ttl)
            .OrderByDescending(sub => sub.CreatedAt)
            .FirstOrDefault();

        if (reusablePending is not null)
        {
            return new(SubscriptionFailure.None, new CreateCheckoutResponse(
                reusablePending.PayPalSubscriptionId,
                reusablePending.ApprovalUrl!,
                reusablePending.PlanCode,
                ReusedPending: true));
        }

        foreach (var stale in existing.Where(sub => sub.Status == SubscriptionStatus.ApprovalPending))
        {
            stale.Status = SubscriptionStatus.Expired;
            stale.UpdatedAt = now;
        }

        PayMongoCreateCheckoutResult created;
        try
        {
            var (successUrl, cancelUrl) = ResolveCallbackUrls(options);
            var planName = string.IsNullOrWhiteSpace(plan.Name) ? normalizedPlanCode : plan.Name;
            var description = string.IsNullOrWhiteSpace(plan.Description)
                ? $"Safe Passage {planName} subscription"
                : plan.Description;

            created = await payMongoClient.CreateCheckoutSessionAsync(
                plan.AmountCentavos,
                plan.Currency,
                planName,
                description,
                normalizedPlanCode,
                userId.ToString(),
                successUrl,
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
            PayPalSubscriptionId = created.SessionId,
            PayPalPlanId = $"paymongo:{normalizedPlanCode}",
            PlanCode = normalizedPlanCode,
            Status = SubscriptionStatus.ApprovalPending,
            ApprovalUrl = created.CheckoutUrl,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.UserSubscriptions.Add(record);
        AddActivity(userId, "Subscription Checkout", $"Created PayMongo checkout session {created.SessionId} ({normalizedPlanCode})", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(SubscriptionFailure.None, new CreateCheckoutResponse(
            created.SessionId,
            created.CheckoutUrl,
            normalizedPlanCode,
            ReusedPending: false));
    }

    public async Task<SubscriptionResult<UserSubscriptionResponse>> ConfirmSubscriptionAsync(
        Guid userId,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return new(SubscriptionFailure.NotFound, ErrorDetail: "Checkout session ID is required.");

        var normalizedId = subscriptionId.Trim();
        var record = await dbContext.UserSubscriptions
            .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == normalizedId, cancellationToken);

        if (record is null)
            return new(SubscriptionFailure.NotFound, ErrorDetail: "No matching subscription record was found.");

        if (record.UserId != userId)
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "This subscription belongs to a different account.");

        PayMongoCheckoutSessionDetails details;
        try
        {
            details = await payMongoClient.GetCheckoutSessionAsync(normalizedId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new(SubscriptionFailure.ProviderError, ErrorDetail: ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(details.CustomUserId)
            && (!Guid.TryParse(details.CustomUserId, out var metaUserId) || metaUserId != userId))
        {
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "Checkout session user mismatch.");
        }

        var now = DateTimeOffset.UtcNow;
        if (details.IsPaid)
        {
            var periodStart = details.PaidAt ?? now;
            var plan = optionsAccessor.Value.Plans.TryGetValue(record.PlanCode, out var opt) ? opt : null;
            var isAnnual = string.Equals(plan?.Interval, "Year", StringComparison.OrdinalIgnoreCase);
            record.Status = SubscriptionStatus.Active;
            record.CurrentPeriodStart ??= periodStart;
            record.CurrentPeriodEnd ??= isAnnual ? periodStart.AddYears(1) : periodStart.AddMonths(1);
            record.ApprovalUrl = null;
            record.UpdatedAt = now;
            AddActivity(userId, "Subscription Activated", $"Confirmed PayMongo payment {normalizedId}", ActivityLogStatus.Success);
        }
        else if (string.Equals(details.Status, "expired", StringComparison.OrdinalIgnoreCase))
        {
            record.Status = SubscriptionStatus.Expired;
            record.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(SubscriptionFailure.None, ToResponse(record, now));
    }

    public async Task<SubscriptionResult<bool>> AbandonPendingAsync(
        Guid userId,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return new(SubscriptionFailure.NotFound, ErrorDetail: "Checkout session ID is required.");

        var normalizedId = subscriptionId.Trim();
        var record = await dbContext.UserSubscriptions
            .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == normalizedId, cancellationToken);

        if (record is null)
            return new(SubscriptionFailure.NotFound, ErrorDetail: "No matching subscription record was found.");

        if (record.UserId != userId)
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "This subscription belongs to a different account.");

        if (record.Status != SubscriptionStatus.ApprovalPending)
            return new(SubscriptionFailure.InvalidState, ErrorDetail: "Only pending checkouts can be discarded.");

        record.Status = SubscriptionStatus.Expired;
        record.ApprovalUrl = null;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        AddActivity(userId, "Subscription Checkout Discarded", $"Discarded pending checkout {normalizedId}", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(SubscriptionFailure.None, true);
    }

    public async Task<SubscriptionResult<UserSubscriptionResponse>> CancelSubscriptionAsync(
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var subscriptions = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        var active = subscriptions
            .Where(sub => sub.Status == SubscriptionStatus.Active)
            .OrderByDescending(sub => sub.UpdatedAt)
            .FirstOrDefault();

        if (active is null)
            return new(SubscriptionFailure.NotFound, ErrorDetail: "No active subscription was found to cancel.");

        active.Status = SubscriptionStatus.Cancelled;
        active.CancelledAt = now;
        active.UpdatedAt = now;
        AddActivity(userId, "Subscription Cancelled", $"Cancelled subscription {active.PayPalSubscriptionId}", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(SubscriptionFailure.None, ToResponse(active, now));
    }

    public async Task<SubscriptionResult<bool>> ProcessWebhookAsync(
        string signatureHeader,
        string rawBody,
        CancellationToken cancellationToken = default)
    {
        var options = optionsAccessor.Value;
        if (!string.IsNullOrWhiteSpace(options.WebhookSecret)
            && !payMongoClient.VerifyWebhookSignature(signatureHeader, rawBody, options.WebhookSecret))
        {
            return new(SubscriptionFailure.InvalidWebhookSignature, ErrorDetail: "Invalid PayMongo webhook signature.");
        }

        string? eventType = null;
        string? sessionId = null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
            eventType = attrs.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (attrs.TryGetProperty("data", out var eventData)
                && eventData.TryGetProperty("id", out var idEl))
            {
                sessionId = idEl.GetString();
            }
        }
        catch (Exception)
        {
            return new(SubscriptionFailure.InvalidState, ErrorDetail: "Malformed webhook payload.");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            return new(SubscriptionFailure.None, true);

        var record = await dbContext.UserSubscriptions
            .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == sessionId, cancellationToken);

        if (record is null)
            return new(SubscriptionFailure.None, true);

        var now = DateTimeOffset.UtcNow;
        if (string.Equals(eventType, "checkout_session.payment.paid", StringComparison.OrdinalIgnoreCase))
        {
            var plan = options.Plans.TryGetValue(record.PlanCode, out var opt) ? opt : null;
            var isAnnual = string.Equals(plan?.Interval, "Year", StringComparison.OrdinalIgnoreCase);
            record.Status = SubscriptionStatus.Active;
            record.CurrentPeriodStart ??= now;
            record.CurrentPeriodEnd ??= isAnnual ? now.AddYears(1) : now.AddMonths(1);
            record.ApprovalUrl = null;
            record.UpdatedAt = now;
            AddActivity(record.UserId, "Subscription Webhook", $"Processed {eventType} for {sessionId}", ActivityLogStatus.Success);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new(SubscriptionFailure.None, true);
    }

    private (string SuccessUrl, string CancelUrl) ResolveCallbackUrls(PayMongoOptions options)
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
            var successUrl = IsUnsetOrLoopback(options.SuccessUrl)
                ? new Uri(frontendUri, "/subscription/paymongo-callback?status=success").ToString()
                : options.SuccessUrl;
            var cancelUrl = IsUnsetOrLoopback(options.CancelUrl)
                ? new Uri(frontendUri, "/subscription/paymongo-callback?status=cancel").ToString()
                : options.CancelUrl;
            return (successUrl, cancelUrl);
        }

        return (options.SuccessUrl, options.CancelUrl);
    }

    private static bool IsUnsetOrLoopback(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback);

    private static UserSubscription? SelectCurrentSubscription(IReadOnlyCollection<UserSubscription> subscriptions, DateTimeOffset now)
    {
        var active = subscriptions
            .Where(sub => HasEntitlement(sub, now))
            .OrderByDescending(sub => sub.CurrentPeriodEnd ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

        if (active is not null)
            return active;

        var pending = subscriptions
            .Where(sub => sub.Status == SubscriptionStatus.ApprovalPending)
            .OrderByDescending(sub => sub.CreatedAt)
            .FirstOrDefault();

        if (pending is not null)
            return pending;

        return subscriptions
            .OrderByDescending(sub => sub.UpdatedAt)
            .FirstOrDefault();
    }

    private static bool HasEntitlement(UserSubscription subscription, DateTimeOffset now)
    {
        if (subscription.Status == SubscriptionStatus.Active)
            return true;

        if (subscription.Status == SubscriptionStatus.Cancelled
            && subscription.CurrentPeriodEnd.HasValue
            && subscription.CurrentPeriodEnd.Value > now)
        {
            return true;
        }

        return false;
    }

    private void AddActivity(Guid userId, string action, string resource, ActivityLogStatus status) =>
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Resource = resource,
            Status = status
        });

    private UserSubscriptionResponse ToResponse(UserSubscription subscription, DateTimeOffset now)
    {
        optionsAccessor.Value.Plans.TryGetValue(subscription.PlanCode, out var planOption);
        var hasEntitlement = HasEntitlement(subscription, now);
        var cancelAtPeriodEnd = subscription.Status == SubscriptionStatus.Cancelled && hasEntitlement;

        return new UserSubscriptionResponse(
            hasEntitlement,
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
