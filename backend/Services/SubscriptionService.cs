using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
    Task<SubscriptionEntitlementsResponse> GetUserEntitlementsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetEligibleDeviceIdsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, UserSubscriptionResponse>> GetUserSubscriptionsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
    Task<bool> HasActiveEntitlementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<CreateCheckoutResponse>> CreateCheckoutAsync(Guid userId, string planCode, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<UserSubscriptionResponse>> ConfirmSubscriptionAsync(Guid userId, string subscriptionId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<bool>> AbandonPendingAsync(Guid userId, string subscriptionId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<UserSubscriptionResponse>> CancelSubscriptionAsync(Guid userId, string? reason, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<bool>> ProcessWebhookAsync(string signatureHeader, string rawBody, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<bool>> CompleteDemoCheckoutAsync(Guid userId, string sessionId, string paymentMethod, string outcome, CancellationToken cancellationToken = default);
    Guid? ValidateDemoToken(string sessionId, string? demoToken);
    Task<IReadOnlyList<SubscriptionHistoryItemResponse>> GetBillingHistoryAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<SubscriptionResult<ResendReceiptResponse>> ResendReceiptAsync(Guid userId, string? subscriptionId = null, CancellationToken cancellationToken = default);
}

public sealed class SubscriptionService(
    ApplicationDbContext dbContext,
    IPayMongoClient payMongoClient,
    IOptions<PayMongoOptions> optionsAccessor,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null,
    IActivationEmailSender? emailSender = null,
    DemoCheckoutStateStore? demoStore = null) : ISubscriptionService
{
    private bool IsDemoModeActive(PayMongoOptions options) =>
        !options.HasCredentials && (options.DemoMode || environment?.IsDevelopment() == true);

    public IReadOnlyList<SubscriptionPlanResponse> GetPlans()
    {
        var options = optionsAccessor.Value;
        var isDevelopment = environment?.IsDevelopment() == true;
        var mode = options.HasCredentials
            ? "paymongo"
            : IsDemoModeActive(options)
                ? "demo"
                : "unconfigured";

        return options.Plans
            .Select(kvp => new SubscriptionPlanResponse(
                kvp.Key,
                string.IsNullOrWhiteSpace(kvp.Value.Name) ? kvp.Key : kvp.Value.Name,
                kvp.Value.Description,
                kvp.Value.PriceDisplay,
                kvp.Value.Interval,
                options.IsConfigured(isDevelopment) && kvp.Value.AmountCentavos >= 2000,
                mode))
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

    public async Task<IReadOnlyDictionary<Guid, UserSubscriptionResponse>> GetUserSubscriptionsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<Guid, UserSubscriptionResponse>();

        var subscriptions = await dbContext.UserSubscriptions
            .Where(subscription => ids.Contains(subscription.UserId))
            .ToListAsync(cancellationToken);
        var subscriptionsByUser = subscriptions.ToLookup(subscription => subscription.UserId);
        var now = DateTimeOffset.UtcNow;

        return ids.ToDictionary(
            userId => userId,
            userId =>
            {
                var current = SelectCurrentSubscription(subscriptionsByUser[userId].ToArray(), now);
                return current is null ? ToFreeResponse() : ToResponse(current, now);
            });
    }

    public async Task<bool> HasActiveEntitlementAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetUserSubscriptionAsync(userId, cancellationToken);
        return current.HasActiveEntitlement;
    }

    public async Task<SubscriptionEntitlementsResponse> GetUserEntitlementsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var role = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Super Admin", StringComparison.OrdinalIgnoreCase))
            return new SubscriptionEntitlementsResponse("admin", false, null, null, 90, true);

        var subscription = await GetUserSubscriptionAsync(userId, cancellationToken);
        var isPro = subscription.HasActiveEntitlement
            && subscription.PlanCode is "pro-monthly" or "pro-annual";

        return new SubscriptionEntitlementsResponse(
            isPro ? subscription.PlanCode! : "free",
            isPro,
            isPro ? null : 2,
            isPro ? null : 5,
            isPro ? 90 : 7,
            isPro);
    }

    public async Task<IReadOnlySet<string>> GetEligibleDeviceIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var entitlements = await GetUserEntitlementsAsync(userId, cancellationToken);
        var devices = await dbContext.Devices.AsNoTracking()
            .Where(device => device.UserId == userId && device.Status != DeviceStatus.Disabled)
            .ToListAsync(cancellationToken);
        IEnumerable<string> ordered = devices
            .OrderByDescending(device => device.Status == DeviceStatus.Active && device.TrustStatus == DeviceTrustStatus.Trusted)
            .ThenByDescending(device => device.LastActiveDate ?? device.RegistrationDate)
            .ThenBy(device => device.RegistrationDate)
            .Select(device => device.DeviceId);
        if (entitlements.MaxTrustedDevices is { } limit)
            ordered = ordered.Take(limit);
        return ordered.ToHashSet(StringComparer.Ordinal);
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

        var isDevelopment = environment?.IsDevelopment() == true;
        if (!options.IsConfigured(isDevelopment) || plan.AmountCentavos < 2000)
            return new(SubscriptionFailure.PlanNotConfigured, ErrorDetail: "Payment checkout is not configured on the server.");

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

        if (IsDemoModeActive(options))
        {
            var demoSessionId = $"cs_demo_{Guid.NewGuid():N}";
            var planName = string.IsNullOrWhiteSpace(plan.Name) ? normalizedPlanCode : plan.Name;
            var frontendBase = ResolveFrontendBaseUrl(options);
            var demoToken = CreateDemoToken(userId, demoSessionId, now.AddHours(2));
            var demoUrl = $"{frontendBase}/subscription/demo-checkout"
                + $"?sessionId={Uri.EscapeDataString(demoSessionId)}"
                + $"&plan={Uri.EscapeDataString(normalizedPlanCode)}"
                + $"&amount={Uri.EscapeDataString(plan.PriceDisplay)}"
                + $"&name={Uri.EscapeDataString(planName)}"
                + $"&token={Uri.EscapeDataString(demoToken)}";

            var demoRecord = new UserSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PayPalSubscriptionId = demoSessionId,
                PayPalPlanId = $"demo:{normalizedPlanCode}",
                PlanCode = normalizedPlanCode,
                Status = SubscriptionStatus.ApprovalPending,
                ApprovalUrl = demoUrl,
                CreatedAt = now,
                UpdatedAt = now
            };

            dbContext.UserSubscriptions.Add(demoRecord);
            AddActivity(userId, "Subscription Checkout", $"Created PayMongo checkout session {demoSessionId} ({normalizedPlanCode})", ActivityLogStatus.Success);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new(SubscriptionFailure.None, new CreateCheckoutResponse(
                demoSessionId,
                demoUrl,
                normalizedPlanCode,
                ReusedPending: false));
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

        var now = DateTimeOffset.UtcNow;
        var wasPending = record.Status == SubscriptionStatus.ApprovalPending;
        if (normalizedId.StartsWith("cs_demo_", StringComparison.OrdinalIgnoreCase)
            || record.PayPalPlanId.StartsWith("demo:", StringComparison.OrdinalIgnoreCase))
        {
            DemoCheckoutOutcome? demoOutcome = null;
            var hasOutcome = demoStore?.TryGetOutcome(normalizedId, out demoOutcome) == true && demoOutcome is not null;

            if (hasOutcome && string.Equals(demoOutcome!.Outcome, "paid", StringComparison.OrdinalIgnoreCase))
            {
                var periodStart = demoOutcome.RecordedAt;
                var plan = optionsAccessor.Value.Plans.TryGetValue(record.PlanCode, out var opt) ? opt : null;
                var isAnnual = string.Equals(plan?.Interval, "Year", StringComparison.OrdinalIgnoreCase);
                record.Status = SubscriptionStatus.Active;
                record.CurrentPeriodStart ??= periodStart;
                record.CurrentPeriodEnd ??= isAnnual ? periodStart.AddYears(1) : periodStart.AddMonths(1);
                record.ApprovalUrl = null;
                record.UpdatedAt = now;
                if (wasPending)
                {
                    AddActivity(userId, "Subscription Activated", $"Confirmed PayMongo payment {normalizedId} via {demoOutcome.PaymentMethod}", ActivityLogStatus.Success);
                }
                await dbContext.SaveChangesAsync(cancellationToken);

                if (wasPending)
                    await TrySendReceiptBestEffortAsync(record, demoOutcome.PaymentMethod, cancellationToken);
            }
            else if (hasOutcome && (string.Equals(demoOutcome!.Outcome, "cancelled", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(demoOutcome.Outcome, "failed", StringComparison.OrdinalIgnoreCase)))
            {
                record.Status = SubscriptionStatus.Expired;
                record.ApprovalUrl = null;
                record.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return new(SubscriptionFailure.None, ToResponse(record, now));
        }

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
        if (wasPending && record.Status == SubscriptionStatus.Active)
            await TrySendReceiptBestEffortAsync(record, "PayMongo", cancellationToken);

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
        if (options.HasCredentials && string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            return new(
                SubscriptionFailure.InvalidWebhookSignature,
                ErrorDetail: "PayMongo webhook signing secret is not configured.");
        }

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

    public async Task<SubscriptionResult<bool>> CompleteDemoCheckoutAsync(
        Guid userId,
        string sessionId,
        string paymentMethod,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new(SubscriptionFailure.NotFound, ErrorDetail: "Checkout session ID is required.");

        var normalizedId = sessionId.Trim();
        var record = await dbContext.UserSubscriptions
            .FirstOrDefaultAsync(sub => sub.PayPalSubscriptionId == normalizedId, cancellationToken);

        if (record is null)
            return new(SubscriptionFailure.NotFound, ErrorDetail: "No matching checkout session was found.");

        if (record.UserId != userId)
            return new(SubscriptionFailure.OwnershipMismatch, ErrorDetail: "This checkout session belongs to a different account.");

        if (!record.PayPalPlanId.StartsWith("demo:", StringComparison.OrdinalIgnoreCase))
            return new(SubscriptionFailure.InvalidState, ErrorDetail: "Invalid checkout session.");

        if (record.Status != SubscriptionStatus.ApprovalPending)
            return new(SubscriptionFailure.InvalidState, ErrorDetail: "This checkout session is no longer pending.");

        var normalizedOutcome = outcome?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedOutcome is not ("paid" or "failed" or "cancelled"))
            return new(SubscriptionFailure.InvalidState, ErrorDetail: "Invalid checkout outcome.");

        var method = string.IsNullOrWhiteSpace(paymentMethod) ? "Card •••• 4242" : paymentMethod.Trim();
        var now = DateTimeOffset.UtcNow;

        demoStore?.SetOutcome(userId, normalizedId, normalizedOutcome, method, now);

        if (normalizedOutcome is "cancelled" or "failed")
        {
            record.Status = SubscriptionStatus.Expired;
            record.ApprovalUrl = null;
            record.UpdatedAt = now;
            AddActivity(userId, "Subscription Checkout", $"PayMongo checkout {normalizedId} {normalizedOutcome} ({method})", ActivityLogStatus.Denied);
        }
        else
        {
            AddActivity(userId, "Subscription Checkout", $"PayMongo payment approved for {normalizedId} via {method}", ActivityLogStatus.Success);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new(SubscriptionFailure.None, true);
    }

    private static readonly byte[] FallbackDemoKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private byte[] GetDemoSigningKey()
    {
        var configured = configuration?["Jwt:SigningKey"];
        return !string.IsNullOrWhiteSpace(configured)
            ? System.Text.Encoding.UTF8.GetBytes(configured)
            : FallbackDemoKey;
    }

    private string CreateDemoToken(Guid userId, string sessionId, DateTimeOffset expiresAt)
    {
        var payload = $"{sessionId}|{userId:N}|{expiresAt.ToUnixTimeSeconds()}";
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        using var hmac = new System.Security.Cryptography.HMACSHA256(GetDemoSigningKey());
        var sig = hmac.ComputeHash(payloadBytes);
        return $"{Convert.ToBase64String(payloadBytes)}.{Convert.ToBase64String(sig)}";
    }

    public Guid? ValidateDemoToken(string sessionId, string? demoToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(demoToken))
            return null;

        var parts = demoToken.Split('.', 2);
        if (parts.Length != 2)
            return null;

        try
        {
            var payloadBytes = Convert.FromBase64String(parts[0]);
            var providedSig = Convert.FromBase64String(parts[1]);
            using var hmac = new System.Security.Cryptography.HMACSHA256(GetDemoSigningKey());
            var expectedSig = hmac.ComputeHash(payloadBytes);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
                return null;

            var payload = System.Text.Encoding.UTF8.GetString(payloadBytes);
            var segments = payload.Split('|', 3);
            if (segments.Length != 3
                || !string.Equals(segments[0], sessionId.Trim(), StringComparison.Ordinal)
                || !Guid.TryParseExact(segments[1], "N", out var userId)
                || !long.TryParse(segments[2], out var expUnix))
                return null;

            return DateTimeOffset.FromUnixTimeSeconds(expUnix) >= DateTimeOffset.UtcNow ? userId : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SubscriptionHistoryItemResponse>> GetBillingHistoryAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        var plans = optionsAccessor.Value.Plans;
        var now = DateTimeOffset.UtcNow;
        return subscriptions
            .OrderByDescending(sub => sub.CreatedAt)
            .Take(25)
            .Select(sub =>
            {
                plans.TryGetValue(sub.PlanCode, out var planOption);
                var paymentMode = "PayMongo";
                var status = (sub.Status == SubscriptionStatus.Active || sub.Status == SubscriptionStatus.Cancelled)
                    && sub.CurrentPeriodEnd.HasValue
                    && sub.CurrentPeriodEnd.Value <= now
                        ? SubscriptionStatus.Expired
                        : sub.Status;
                return new SubscriptionHistoryItemResponse(
                    sub.PayPalSubscriptionId,
                    sub.PlanCode,
                    planOption?.Name ?? sub.PlanCode,
                    status.ToString(),
                    planOption?.PriceDisplay ?? "-",
                    paymentMode,
                    sub.CreatedAt,
                    sub.CurrentPeriodStart,
                    sub.CurrentPeriodEnd,
                    sub.CancelledAt);
            })
            .ToList();
    }

    public async Task<SubscriptionResult<ResendReceiptResponse>> ResendReceiptAsync(
        Guid userId,
        string? subscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        if (emailSender is null)
            return new(SubscriptionFailure.ProviderError, ErrorDetail: "Email delivery is not configured on the server.");

        var subscriptions = await dbContext.UserSubscriptions
            .Where(sub => sub.UserId == userId)
            .ToListAsync(cancellationToken);

        UserSubscription? target = null;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            var normalizedId = subscriptionId.Trim();
            target = subscriptions.FirstOrDefault(sub => sub.PayPalSubscriptionId == normalizedId);
            if (target is null)
                return new(SubscriptionFailure.NotFound, ErrorDetail: "No matching subscription record was found.");
        }
        else
        {
            target = subscriptions
                .Where(sub => (sub.Status == SubscriptionStatus.Active || sub.Status == SubscriptionStatus.Cancelled)
                              && sub.CurrentPeriodStart.HasValue)
                .OrderByDescending(sub => sub.UpdatedAt)
                .FirstOrDefault();
            if (target is null)
                return new(SubscriptionFailure.NotFound, ErrorDetail: "No paid subscription was found to send a receipt for.");
        }

        var paymentMode = "PayMongo";

        try
        {
            var sent = await SendReceiptAsync(target, paymentMode, cancellationToken);
            if (!sent)
                return new(SubscriptionFailure.UserUnavailable, ErrorDetail: "No verified email address was found for your account.");

            return new(SubscriptionFailure.None, new ResendReceiptResponse(true, $"Receipt email sent for {target.PayPalSubscriptionId}."));
        }
        catch (EmailDeliveryException ex)
        {
            return new(SubscriptionFailure.ProviderError, ErrorDetail: ex.Message);
        }
    }

    private async Task TrySendReceiptBestEffortAsync(
        UserSubscription record,
        string paymentMethod,
        CancellationToken cancellationToken)
    {
        if (emailSender is null)
            return;

        try
        {
            await SendReceiptAsync(record, paymentMethod, cancellationToken);
        }
        catch (Exception)
        {
            // Receipt delivery is best-effort and must never block subscription activation.
        }
    }

    private async Task<bool> SendReceiptAsync(
        UserSubscription record,
        string paymentMethod,
        CancellationToken cancellationToken)
    {
        if (emailSender is null)
            return false;

        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == record.UserId, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
            return false;

        var options = optionsAccessor.Value;
        options.Plans.TryGetValue(record.PlanCode, out var planOption);
        var planName = planOption?.Name ?? record.PlanCode;
        var amountDisplay = planOption?.PriceDisplay ?? "-";
        var periodStart = record.CurrentPeriodStart ?? DateTimeOffset.UtcNow;
        var periodEnd = record.CurrentPeriodEnd ?? periodStart.AddMonths(1);
        var portalUrl = $"{ResolveFrontendBaseUrl(options)}/subscription";

        await emailSender.SendSubscriptionReceiptAsync(
            new SubscriptionReceiptEmail(
                user.Email,
                user.Username,
                record.PayPalSubscriptionId,
                planName,
                amountDisplay,
                paymentMethod,
                periodStart,
                periodEnd,
                record.UpdatedAt,
                portalUrl),
            cancellationToken);

        return true;
    }

    private string ResolveFrontendBaseUrl(PayMongoOptions options)
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
            return frontendUri.ToString().TrimEnd('/');
        }

        if (Uri.TryCreate(options.SuccessUrl, UriKind.Absolute, out var successUri))
            return successUri.GetLeftPart(UriPartial.Authority);

        return "http://localhost:5092";
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

        return null;
    }

    private static bool HasEntitlement(UserSubscription subscription, DateTimeOffset now)
    {
        if (subscription.Status == SubscriptionStatus.Active)
            return !subscription.CurrentPeriodEnd.HasValue || subscription.CurrentPeriodEnd.Value > now;

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
