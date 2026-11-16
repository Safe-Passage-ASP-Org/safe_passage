using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class SubscriptionServiceTests
{
    [Fact]
    public async Task CreateCheckoutAsync_ReusesFreshPendingAndBlocksDuplicateActive()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db);
        var payPal = new FakePayPalClient();
        var service = CreateService(db, payPal);

        var first = await service.CreateCheckoutAsync(user.Id, "pro-monthly");
        var second = await service.CreateCheckoutAsync(user.Id, "pro-monthly");

        Assert.Equal(SubscriptionFailure.None, first.Failure);
        Assert.False(first.Data!.ReusedPending);
        Assert.Equal(SubscriptionFailure.None, second.Failure);
        Assert.True(second.Data!.ReusedPending);
        Assert.Equal(first.Data.PayPalSubscriptionId, second.Data.PayPalSubscriptionId);
        Assert.Equal(1, payPal.CreateCalls);

        // Activate subscription, then verify duplicate checkout is blocked.
        payPal.NextDetails = new PayPalSubscriptionDetails(
            first.Data.PayPalSubscriptionId,
            "ACTIVE",
            "P-MONTHLY",
            user.Id.ToString(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMonths(1),
            DateTimeOffset.UtcNow);

        var confirmed = await service.ConfirmSubscriptionAsync(user.Id, first.Data.PayPalSubscriptionId);
        Assert.Equal(SubscriptionFailure.None, confirmed.Failure);
        Assert.True(confirmed.Data!.HasActiveEntitlement);

        var duplicate = await service.CreateCheckoutAsync(user.Id, "pro-annual");
        Assert.Equal(SubscriptionFailure.AlreadySubscribed, duplicate.Failure);
    }

    [Fact]
    public async Task ConfirmSubscriptionAsync_RejectsCrossUserOwnershipMismatch()
    {
        await using var db = CreateDbContext();
        var userA = SeedUser(db, "usera");
        var userB = SeedUser(db, "userb");
        var payPal = new FakePayPalClient
        {
            NextDetails = new PayPalSubscriptionDetails(
                "I-SUB-OWNED-BY-A",
                "ACTIVE",
                "P-MONTHLY",
                userA.Id.ToString(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMonths(1),
                DateTimeOffset.UtcNow)
        };
        var service = CreateService(db, payPal);

        var result = await service.ConfirmSubscriptionAsync(userB.Id, "I-SUB-OWNED-BY-A");

        Assert.Equal(SubscriptionFailure.OwnershipMismatch, result.Failure);
        Assert.Empty(db.UserSubscriptions);
        Assert.Contains(db.ActivityLogs, log => log.UserId == userB.Id && log.Action == "SubscriptionOwnershipMismatch");
    }

    [Fact]
    public async Task CancelSubscriptionAsync_RetainsEntitlementUntilCurrentPeriodEnd()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
        db.UserSubscriptions.Add(new UserSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PayPalSubscriptionId = "I-ACTIVE-1",
            PayPalPlanId = "P-MONTHLY",
            PlanCode = "pro-monthly",
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-10),
            CurrentPeriodEnd = periodEnd
        });
        await db.SaveChangesAsync();

        var payPal = new FakePayPalClient();
        var service = CreateService(db, payPal);

        var cancelled = await service.CancelSubscriptionAsync(user.Id, "Testing cancel");

        Assert.Equal(SubscriptionFailure.None, cancelled.Failure);
        Assert.Equal("Cancelled", cancelled.Data!.Status);
        Assert.True(cancelled.Data.HasActiveEntitlement);
        Assert.True(cancelled.Data.CancelAtPeriodEnd);
        Assert.Equal(1, payPal.CancelCalls);
    }

    [Fact]
    public async Task ProcessWebhookAsync_RejectsInvalidSignatureAndIsIdempotent()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db);
        db.UserSubscriptions.Add(new UserSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PayPalSubscriptionId = "I-WEBHOOK-1",
            PayPalPlanId = "P-MONTHLY",
            PlanCode = "pro-monthly",
            Status = SubscriptionStatus.ApprovalPending
        });
        await db.SaveChangesAsync();

        var payPal = new FakePayPalClient { WebhookVerified = false };
        var service = CreateService(db, payPal);
        var headers = new PayPalWebhookHeaders("tx-1", "2026-11-16T00:00:00Z", "sig", "https://api.sandbox.paypal.com/cert", "SHA256withRSA");
        var payload = $$"""
        {
          "id": "WH-EVENT-1",
          "event_type": "BILLING.SUBSCRIPTION.ACTIVATED",
          "resource": {
            "id": "I-WEBHOOK-1",
            "custom_id": "{{user.Id}}",
            "status": "ACTIVE",
            "billing_info": {
              "next_billing_time": "2026-12-16T00:00:00Z"
            }
          }
        }
        """;

        var rejected = await service.ProcessWebhookAsync(headers, payload);
        Assert.Equal(SubscriptionFailure.InvalidWebhookSignature, rejected.Failure);

        payPal.WebhookVerified = true;
        var first = await service.ProcessWebhookAsync(headers, payload);
        var second = await service.ProcessWebhookAsync(headers, payload);

        Assert.Equal(SubscriptionFailure.None, first.Failure);
        Assert.Equal(SubscriptionFailure.None, second.Failure);
        Assert.Single(db.SubscriptionWebhookEvents);

        var sub = await db.UserSubscriptions.SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sub.CurrentPeriodEnd);
    }

    private static User SeedUser(ApplicationDbContext db, string username = "alice")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "hash",
            IsActive = true,
            IsEmailVerified = true,
            Role = "User"
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SubscriptionService CreateService(ApplicationDbContext db, FakePayPalClient payPal)
    {
        var options = Options.Create(new PayPalOptions
        {
            Environment = "Sandbox",
            BaseUrl = "https://api-m.sandbox.paypal.com",
            ClientId = "sb-client",
            ClientSecret = "sb-secret",
            WebhookId = "WH-ID",
            Plans = new Dictionary<string, PayPalPlanOption>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro-monthly"] = new() { PayPalPlanId = "P-MONTHLY", Name = "Pro Monthly", PriceDisplay = "$4.99 / month", Interval = "Month" },
                ["pro-annual"] = new() { PayPalPlanId = "P-ANNUAL", Name = "Pro Annual", PriceDisplay = "$49.99 / year", Interval = "Year" }
            }
        });
        return new SubscriptionService(db, payPal, options);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class FakePayPalClient : IPayPalClient
    {
        public int CreateCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool WebhookVerified { get; set; } = true;
        public PayPalSubscriptionDetails? NextDetails { get; set; }

        public Task<PayPalCreateSubscriptionResult> CreateSubscriptionAsync(
            string payPalPlanId,
            string customId,
            string returnUrl,
            string cancelUrl,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            var id = $"I-SANDBOX-{CreateCalls}";
            return Task.FromResult(new PayPalCreateSubscriptionResult(
                id,
                "APPROVAL_PENDING",
                $"https://www.sandbox.paypal.com/webapps/billing/subscriptions?ba_token={id}"));
        }

        public Task<PayPalSubscriptionDetails> GetSubscriptionAsync(
            string subscriptionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(NextDetails ?? new PayPalSubscriptionDetails(
                subscriptionId,
                "APPROVAL_PENDING",
                "P-MONTHLY",
                null,
                null,
                null,
                null));
        }

        public Task CancelSubscriptionAsync(
            string subscriptionId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> VerifyWebhookSignatureAsync(
            PayPalWebhookHeaders headers,
            JsonElement rawWebhookEvent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WebhookVerified);
    }
}
