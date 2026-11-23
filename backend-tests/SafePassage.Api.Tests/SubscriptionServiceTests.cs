using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
        var payMongo = new FakePayMongoClient();
        var service = CreateService(db, payMongo);

        var first = await service.CreateCheckoutAsync(user.Id, "pro-monthly");
        var second = await service.CreateCheckoutAsync(user.Id, "pro-monthly");

        Assert.Equal(SubscriptionFailure.None, first.Failure);
        Assert.False(first.Data!.ReusedPending);
        Assert.Equal(SubscriptionFailure.None, second.Failure);
        Assert.True(second.Data!.ReusedPending);
        Assert.Equal(first.Data.PayPalSubscriptionId, second.Data.PayPalSubscriptionId);
        Assert.Equal(1, payMongo.CreateCalls);

        payMongo.NextDetails = new PayMongoCheckoutSessionDetails(
            first.Data.PayPalSubscriptionId,
            "active",
            true,
            "pro-monthly",
            user.Id.ToString(),
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
        var payMongo = new FakePayMongoClient
        {
            NextDetails = new PayMongoCheckoutSessionDetails(
                "cs_owned_by_a",
                "active",
                true,
                "pro-monthly",
                userA.Id.ToString(),
                DateTimeOffset.UtcNow)
        };
        var service = CreateService(db, payMongo);

        db.UserSubscriptions.Add(new UserSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userA.Id,
            PayPalSubscriptionId = "cs_owned_by_a",
            PayPalPlanId = "paymongo:pro-monthly",
            PlanCode = "pro-monthly",
            Status = SubscriptionStatus.ApprovalPending
        });
        await db.SaveChangesAsync();

        var mismatch = await service.ConfirmSubscriptionAsync(userB.Id, "cs_owned_by_a");
        Assert.Equal(SubscriptionFailure.OwnershipMismatch, mismatch.Failure);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_RetainsEntitlementUntilPeriodEnd()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db);
        var payMongo = new FakePayMongoClient();
        var service = CreateService(db, payMongo);

        db.UserSubscriptions.Add(new UserSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PayPalSubscriptionId = "cs_active_1",
            PayPalPlanId = "paymongo:pro-monthly",
            PlanCode = "pro-monthly",
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-5),
            CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(25)
        });
        await db.SaveChangesAsync();

        var cancelled = await service.CancelSubscriptionAsync(user.Id, "Testing");
        Assert.Equal(SubscriptionFailure.None, cancelled.Failure);
        Assert.Equal("Cancelled", cancelled.Data!.Status);
        Assert.True(cancelled.Data.HasActiveEntitlement);
        Assert.True(cancelled.Data.CancelAtPeriodEnd);
    }

    [Fact]
    public async Task ProcessWebhookAsync_ActivatesSubscriptionOnPaidEvent()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db);
        var payMongo = new FakePayMongoClient();
        var service = CreateService(db, payMongo);

        db.UserSubscriptions.Add(new UserSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PayPalSubscriptionId = "cs_webhook_1",
            PayPalPlanId = "paymongo:pro-monthly",
            PlanCode = "pro-monthly",
            Status = SubscriptionStatus.ApprovalPending
        });
        await db.SaveChangesAsync();

        var payload = JsonSerializer.Serialize(new
        {
            data = new
            {
                attributes = new
                {
                    type = "checkout_session.payment.paid",
                    data = new
                    {
                        id = "cs_webhook_1"
                    }
                }
            }
        });

        var result = await service.ProcessWebhookAsync("t=123,te=valid", payload);
        Assert.Equal(SubscriptionFailure.None, result.Failure);

        var updated = await service.GetUserSubscriptionAsync(user.Id);
        Assert.True(updated.HasActiveEntitlement);
        Assert.Equal("Active", updated.Status);
    }

    [Fact]
    public async Task CreateCheckoutAsync_UsesDemoCheckout_WhenDemoModeEnabled()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db, "demouser");
        var payMongo = new FakePayMongoClient();
        var service = CreateDemoService(db, payMongo);

        var plans = service.GetPlans();
        Assert.All(plans, p =>
        {
            Assert.True(p.IsConfigured);
            Assert.Equal("demo", p.Mode);
        });

        var checkout = await service.CreateCheckoutAsync(user.Id, "pro-monthly");
        Assert.Equal(SubscriptionFailure.None, checkout.Failure);
        Assert.StartsWith("cs_demo_", checkout.Data!.PayPalSubscriptionId);
        Assert.Contains("/subscription/demo-checkout", checkout.Data.ApprovalUrl);
        Assert.Equal(0, payMongo.CreateCalls);
    }

    [Fact]
    public async Task CompleteDemoCheckoutAsync_AndConfirm_ActivatesAndSendsReceiptEmail()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db, "demopayer");
        var payMongo = new FakePayMongoClient();
        var emailSender = new FakeReceiptEmailSender();
        var demoStore = new DemoCheckoutStateStore(new MemoryCache(new MemoryCacheOptions()));
        var service = CreateDemoService(db, payMongo, emailSender, demoStore);

        var checkout = await service.CreateCheckoutAsync(user.Id, "pro-monthly");
        Assert.Equal(SubscriptionFailure.None, checkout.Failure);

        var completed = await service.CompleteDemoCheckoutAsync(
            user.Id,
            checkout.Data!.PayPalSubscriptionId,
            "GCash (Sandbox)",
            "paid");
        Assert.Equal(SubscriptionFailure.None, completed.Failure);

        var confirmed = await service.ConfirmSubscriptionAsync(user.Id, checkout.Data.PayPalSubscriptionId);
        Assert.Equal(SubscriptionFailure.None, confirmed.Failure);
        Assert.True(confirmed.Data!.HasActiveEntitlement);
        Assert.Equal("Active", confirmed.Data.Status);

        Assert.Single(emailSender.SentReceipts);
        Assert.Equal(user.Email, emailSender.SentReceipts[0].RecipientEmail);
        Assert.Equal("GCash (Sandbox)", emailSender.SentReceipts[0].PaymentMethod);
        Assert.Equal(checkout.Data.PayPalSubscriptionId, emailSender.SentReceipts[0].ReferenceId);
    }

    [Fact]
    public async Task CompleteDemoCheckoutAsync_RejectsCrossUserOwnershipMismatch()
    {
        await using var db = CreateDbContext();
        var userA = SeedUser(db, "demoownera");
        var userB = SeedUser(db, "demoownerb");
        var service = CreateDemoService(db, new FakePayMongoClient());

        var checkout = await service.CreateCheckoutAsync(userA.Id, "pro-monthly");
        Assert.Equal(SubscriptionFailure.None, checkout.Failure);

        var mismatch = await service.CompleteDemoCheckoutAsync(
            userB.Id,
            checkout.Data!.PayPalSubscriptionId,
            "Demo Card •••• 4242",
            "paid");
        Assert.Equal(SubscriptionFailure.OwnershipMismatch, mismatch.Failure);
    }

    [Fact]
    public async Task GetBillingHistoryAsync_ReturnsUserSubscriptionsOrderedNewestFirst()
    {
        await using var db = CreateDbContext();
        var user = SeedUser(db, "historyuser");
        var service = CreateDemoService(db, new FakePayMongoClient());

        var now = DateTimeOffset.UtcNow;
        db.UserSubscriptions.AddRange(
            new UserSubscription
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PayPalSubscriptionId = "cs_demo_older",
                PayPalPlanId = "demo:pro-monthly",
                PlanCode = "pro-monthly",
                Status = SubscriptionStatus.Expired,
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2)
            },
            new UserSubscription
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PayPalSubscriptionId = "cs_demo_newer",
                PayPalPlanId = "demo:pro-annual",
                PlanCode = "pro-annual",
                Status = SubscriptionStatus.Active,
                CreatedAt = now.AddHours(-1),
                UpdatedAt = now.AddHours(-1),
                CurrentPeriodStart = now.AddHours(-1),
                CurrentPeriodEnd = now.AddYears(1)
            });
        await db.SaveChangesAsync();

        var history = await service.GetBillingHistoryAsync(user.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal("cs_demo_newer", history[0].SubscriptionId);
        Assert.Equal("Sandbox Simulator", history[0].PaymentMode);
        Assert.Equal("cs_demo_older", history[1].SubscriptionId);
    }

    private static User SeedUser(ApplicationDbContext db, string username = "subuser")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            IsActive = true
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SubscriptionService CreateService(ApplicationDbContext db, FakePayMongoClient payMongo)
    {
        var options = Options.Create(new PayMongoOptions
        {
            SecretKey = "sk_test_fake",
            WebhookSecret = "whsec_fake",
            Plans = new Dictionary<string, PayMongoPlanOption>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro-monthly"] = new() { AmountCentavos = 29900, Currency = "PHP", Name = "Pro Monthly", PriceDisplay = "PHP 299 / month", Interval = "Month" },
                ["pro-annual"] = new() { AmountCentavos = 299000, Currency = "PHP", Name = "Pro Annual", PriceDisplay = "PHP 2,990 / year", Interval = "Year" }
            }
        });
        return new SubscriptionService(db, payMongo, options);
    }

    private static SubscriptionService CreateDemoService(
        ApplicationDbContext db,
        FakePayMongoClient payMongo,
        FakeReceiptEmailSender? emailSender = null,
        DemoCheckoutStateStore? demoStore = null)
    {
        var options = Options.Create(new PayMongoOptions
        {
            DemoMode = true,
            Plans = new Dictionary<string, PayMongoPlanOption>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro-monthly"] = new() { AmountCentavos = 29900, Currency = "PHP", Name = "Pro Monthly", PriceDisplay = "PHP 299 / month", Interval = "Month" },
                ["pro-annual"] = new() { AmountCentavos = 299000, Currency = "PHP", Name = "Pro Annual", PriceDisplay = "PHP 2,990 / year", Interval = "Year" }
            }
        });
        return new SubscriptionService(
            db,
            payMongo,
            options,
            emailSender: emailSender,
            demoStore: demoStore ?? new DemoCheckoutStateStore(new MemoryCache(new MemoryCacheOptions())));
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

    private sealed class FakePayMongoClient : IPayMongoClient
    {
        public int CreateCalls { get; private set; }
        public bool WebhookVerified { get; set; } = true;
        public PayMongoCheckoutSessionDetails? NextDetails { get; set; }

        public Task<PayMongoCreateCheckoutResult> CreateCheckoutSessionAsync(
            int amountCentavos,
            string currency,
            string planName,
            string description,
            string planCode,
            string userId,
            string successUrl,
            string cancelUrl,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            var id = $"cs_test_{CreateCalls}";
            return Task.FromResult(new PayMongoCreateCheckoutResult(
                id,
                $"https://checkout.paymongo.com/{id}"));
        }

        public Task<PayMongoCheckoutSessionDetails> GetCheckoutSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(NextDetails ?? new PayMongoCheckoutSessionDetails(
                sessionId,
                "active",
                false,
                "pro-monthly",
                null,
                null));
        }

        public bool VerifyWebhookSignature(string signatureHeader, string rawBody, string webhookSecret) =>
            WebhookVerified;
    }

    private sealed class FakeReceiptEmailSender : IActivationEmailSender
    {
        public List<SubscriptionReceiptEmail> SentReceipts { get; } = [];

        public Task SendActivationEmailAsync(
            string email,
            string username,
            string activationUrl,
            bool requiresPasswordSetup,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendAdminLoginLinkAsync(
            string email,
            string username,
            string loginUrl,
            int tokenLifetimeMinutes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendAdminEmailChangeVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendBackupEmailVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendSubscriptionReceiptAsync(
            SubscriptionReceiptEmail receipt,
            CancellationToken cancellationToken)
        {
            SentReceipts.Add(receipt);
            return Task.CompletedTask;
        }

        public Task SendSignInOtpAsync(
            string email,
            string username,
            string otpCode,
            int lifetimeMinutes,
            bool isAdminLogin,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
