using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class AdminEmailLoginServiceTests
{
    [Fact]
    public async Task RequestOtpAsync_SendsSevenDayOtp()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        await fixture.Service.RequestOtpAsync(admin.Email, CancellationToken.None);

        Assert.NotNull(fixture.EmailSender.LastOtpCode);
        Assert.Equal(6, fixture.EmailSender.LastOtpCode!.Length);
        Assert.Equal(7 * 24 * 60, fixture.EmailSender.LastLifetimeMinutes);
        Assert.True(fixture.EmailSender.LastIsAdminLogin);

        var token = await fixture.DbContext.AdminLoginTokens.SingleAsync(item => item.UserId == admin.Id);
        Assert.Null(token.UsedAt);
        Assert.InRange(token.ExpiresAt - token.CreatedAt, TimeSpan.FromDays(6.99), TimeSpan.FromDays(7.01));
    }

    [Fact]
    public async Task CompleteOtpAsync_AllowsMultipleReusesWithinSevenDays()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        await fixture.Service.RequestOtpAsync(admin.Email, CancellationToken.None);
        var otpCode = fixture.EmailSender.LastOtpCode!;

        var firstResult = await fixture.Service.CompleteOtpAsync(admin.Email, otpCode, CancellationToken.None);
        var secondResult = await fixture.Service.CompleteOtpAsync(admin.Email, otpCode, CancellationToken.None);
        var thirdResult = await fixture.Service.CompleteOtpAsync(admin.Email, otpCode, CancellationToken.None);

        Assert.Equal(AuthFlowFailure.None, firstResult.Failure);
        Assert.Equal(AuthFlowFailure.None, secondResult.Failure);
        Assert.Equal(AuthFlowFailure.None, thirdResult.Failure);
        Assert.False(string.IsNullOrWhiteSpace(firstResult.Response?.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(secondResult.Response?.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(thirdResult.Response?.AccessToken));

        var token = await fixture.DbContext.AdminLoginTokens.SingleAsync(item => item.UserId == admin.Id);
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public async Task CompleteOtpAsync_DoesNotLockOutAfterFailedAttempts()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        await fixture.Service.RequestOtpAsync(admin.Email, CancellationToken.None);
        var otpCode = fixture.EmailSender.LastOtpCode!;
        var wrongCode = otpCode == "000000" ? "999999" : "000000";

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var failed = await fixture.Service.CompleteOtpAsync(admin.Email, wrongCode, CancellationToken.None);
            Assert.Equal(AuthFlowFailure.InvalidCredentials, failed.Failure);
        }

        var success = await fixture.Service.CompleteOtpAsync(admin.Email, otpCode, CancellationToken.None);
        Assert.Equal(AuthFlowFailure.None, success.Failure);
        Assert.False(string.IsNullOrWhiteSpace(success.Response?.AccessToken));
    }

    [Fact]
    public async Task CompleteOtpAsync_RejectsExpiredOtp()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        await fixture.Service.RequestOtpAsync(admin.Email, CancellationToken.None);
        var otpCode = fixture.EmailSender.LastOtpCode!;

        var token = await fixture.DbContext.AdminLoginTokens.SingleAsync(item => item.UserId == admin.Id);
        token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.DbContext.SaveChangesAsync();

        var result = await fixture.Service.CompleteOtpAsync(admin.Email, otpCode, CancellationToken.None);
        Assert.Equal(AuthFlowFailure.InvalidCredentials, result.Failure);
    }

    [Fact]
    public async Task RequestOtpAsync_RevokesPreviousOtpWhenNewOtpRequested()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        await fixture.Service.RequestOtpAsync(admin.Email, CancellationToken.None);
        var firstCode = fixture.EmailSender.LastOtpCode!;

        await fixture.Service.RequestOtpAsync(admin.Email, CancellationToken.None);
        var secondCode = fixture.EmailSender.LastOtpCode!;

        if (firstCode != secondCode)
        {
            var oldResult = await fixture.Service.CompleteOtpAsync(admin.Email, firstCode, CancellationToken.None);
            Assert.Equal(AuthFlowFailure.InvalidCredentials, oldResult.Failure);
        }

        var newResult = await fixture.Service.CompleteOtpAsync(admin.Email, secondCode, CancellationToken.None);
        Assert.Equal(AuthFlowFailure.None, newResult.Failure);
    }

    [Fact]
    public async Task HasActiveOtpAsync_ReflectsActiveAndExpiredState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        Assert.False(await fixture.Service.HasActiveOtpAsync(admin.Email, CancellationToken.None));

        await fixture.Service.RequestOtpAsync(admin.Email, forceResend: false, CancellationToken.None);
        Assert.True(await fixture.Service.HasActiveOtpAsync(admin.Email, CancellationToken.None));

        var token = await fixture.DbContext.AdminLoginTokens.SingleAsync(item => item.UserId == admin.Id);
        token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.DbContext.SaveChangesAsync();

        Assert.False(await fixture.Service.HasActiveOtpAsync(admin.Email, CancellationToken.None));
    }

    [Fact]
    public async Task RequestOtpAsync_WithoutForceResend_KeepsExistingActiveOtp()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();

        await fixture.Service.RequestOtpAsync(admin.Email, forceResend: false, CancellationToken.None);
        var firstCode = fixture.EmailSender.LastOtpCode!;
        var sendCountAfterFirst = fixture.EmailSender.SendCount;

        await fixture.Service.RequestOtpAsync(admin.Email, forceResend: false, CancellationToken.None);

        Assert.Equal(sendCountAfterFirst, fixture.EmailSender.SendCount);
        Assert.Equal(firstCode, fixture.EmailSender.LastOtpCode);
        Assert.Equal(1, await fixture.DbContext.AdminLoginTokens.CountAsync(item => item.UserId == admin.Id));
    }

    [Fact]
    public void FormatOtpBody_UsesReusableCopyForAdminAndSingleUseForUser()
    {
        var adminBody = SmtpEmailSender.FormatOtpBody("Admin User", "123456", 7 * 24 * 60, isAdminLogin: true);
        var userBody = SmtpEmailSender.FormatOtpBody("Regular User", "654321", 10, isAdminLogin: false);

        Assert.Contains("expires in 7 days and can be reused for administrator sign-ins", adminBody);
        Assert.DoesNotContain("can only be used once", adminBody);

        Assert.Contains("expires in 10 minutes and can only be used once", userBody);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public ApplicationDbContext DbContext { get; }
        public CapturingEmailSender EmailSender { get; } = new();
        public AdminEmailLoginService Service { get; }

        private TestFixture(SqliteConnection connection, ApplicationDbContext dbContext)
        {
            this.connection = connection;
            DbContext = dbContext;
            var jwtSettings = new JwtSettings
            {
                Issuer = "safe-passage",
                Audience = "safe-passage-clients",
                SigningKey = new string('x', 64),
                AccessTokenMinutes = 60
            };
            Service = new AdminEmailLoginService(
                dbContext,
                EmailSender,
                new JwtTokenService(jwtSettings),
                jwtSettings,
                NullLogger<AdminEmailLoginService>.Instance);
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .UseOpenIddict()
                .Options;
            var dbContext = new ApplicationDbContext(options);
            await dbContext.Database.MigrateAsync();
            return new TestFixture(connection, dbContext);
        }

        public async Task<User> SeedAdminAsync()
        {
            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "admin-user",
                FullName = "Admin User",
                Email = "admin@example.com",
                PasswordHash = "unused",
                Role = "Admin",
                IsActive = true,
                IsEmailVerified = true
            };
            DbContext.Users.Add(admin);
            await DbContext.SaveChangesAsync();
            return admin;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class CapturingEmailSender : IActivationEmailSender
    {
        public int SendCount { get; private set; }
        public string? LastOtpCode { get; private set; }
        public int LastLifetimeMinutes { get; private set; }
        public bool LastIsAdminLogin { get; private set; }

        public Task SendActivationEmailAsync(string email, string username, string activationUrl, bool requiresPasswordSetup, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAdminLoginLinkAsync(string email, string username, string loginUrl, int tokenLifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAdminEmailChangeVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendBackupEmailVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendSubscriptionReceiptAsync(SubscriptionReceiptEmail receipt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendSignInOtpAsync(string email, string username, string otpCode, int lifetimeMinutes, bool isAdminLogin, CancellationToken cancellationToken)
        {
            SendCount++;
            LastOtpCode = otpCode;
            LastLifetimeMinutes = lifetimeMinutes;
            LastIsAdminLogin = isAdminLogin;
            return Task.CompletedTask;
        }
    }
}
