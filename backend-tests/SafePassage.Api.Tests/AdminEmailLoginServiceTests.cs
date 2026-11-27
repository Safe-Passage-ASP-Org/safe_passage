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
    public async Task AdminOtpLogin_DoesNotRequirePriorEmailVerification()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Email = "admin@example.com",
            PasswordHash = "not-used",
            Role = "Admin",
            IsActive = true,
            IsEmailVerified = false
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var emailSender = new CapturingEmailSender();
        var service = new AdminEmailLoginService(
            db,
            emailSender,
            new StubJwtTokenService(),
            new JwtSettings { SigningKey = "test-signing-key-that-is-at-least-32-bytes" },
            NullLogger<AdminEmailLoginService>.Instance);

        await service.RequestOtpAsync(user.Email, CancellationToken.None);
        var result = await service.CompleteOtpAsync(user.Email, emailSender.Code!, CancellationToken.None);

        Assert.Equal(AuthFlowFailure.None, result.Failure);
        Assert.NotNull(result.Response);
        Assert.False(user.IsEmailVerified);
        var token = await db.AdminLoginTokens.SingleAsync();
        Assert.Equal(TimeSpan.FromDays(7), token.ExpiresAt - token.CreatedAt);
    }

    private sealed class StubJwtTokenService : IJwtTokenService
    {
        public string CreateAccessToken(User user, string? deviceId = null, string? authContext = null, DateTimeOffset? authContextExpiresAt = null) =>
            "test-access-token";
    }

    private sealed class CapturingEmailSender : IActivationEmailSender
    {
        public string? Code { get; private set; }

        public Task SendActivationEmailAsync(string email, string username, string activationUrl, bool requiresPasswordSetup, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendAdminEmailChangeVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendBackupEmailVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendSubscriptionReceiptAsync(SubscriptionReceiptEmail receipt, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendSignInOtpAsync(string email, string username, string otpCode, int lifetimeMinutes, bool isAdminLogin, CancellationToken cancellationToken)
        {
            Code = otpCode;
            return Task.CompletedTask;
        }
    }
}
