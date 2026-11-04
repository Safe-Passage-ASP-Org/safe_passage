using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class AccountActivationServiceTests
{
    [Fact]
    public async Task CreateAndSendInvitation_StoresOnlyTokenHashAndKeepsAccountInactive()
    {
        await using var dbContext = CreateDbContext();
        var user = NewPendingUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var emailSender = new TestEmailSender();
        var service = CreateService(dbContext, emailSender);

        await service.CreateAndSendInvitationAsync(user, CancellationToken.None);

        var token = dbContext.AccountActivationTokens.Single();
        Assert.False(user.IsActive);
        Assert.False(user.IsEmailVerified);
        Assert.NotEqual(token.TokenHash, ReadToken(emailSender.ActivationUrl!));
        Assert.Equal(user.Email, emailSender.Email);
    }

    [Fact]
    public async Task ActivateAsync_ValidTokenActivatesAccountAndRejectsReuse()
    {
        await using var dbContext = CreateDbContext();
        var user = NewPendingUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var emailSender = new TestEmailSender();
        var service = CreateService(dbContext, emailSender);
        await service.CreateAndSendInvitationAsync(user, CancellationToken.None);
        var token = ReadToken(emailSender.ActivationUrl!);

        var result = await service.ActivateAsync(token, "NewPassword123!", CancellationToken.None);
        var replay = await service.ActivateAsync(token, "AnotherPassword123!", CancellationToken.None);

        Assert.Equal(AccountActivationResult.Activated, result);
        Assert.Equal(AccountActivationResult.InvalidOrExpired, replay);
        Assert.True(user.IsActive);
        Assert.True(user.IsEmailVerified);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, "NewPassword123!"));
        Assert.NotNull(dbContext.AccountActivationTokens.Single().UsedAt);
    }

    [Fact]
    public async Task ActivateAsync_PublicRegistrationVerifiesEmailWithoutChangingPassword()
    {
        await using var dbContext = CreateDbContext();
        var user = NewPendingUser();
        var passwordHasher = new PasswordHasher<User>();
        user.PasswordHash = passwordHasher.HashPassword(user, "Chosen-at-signup-123");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var emailSender = new TestEmailSender();
        var service = CreateService(dbContext, emailSender);
        await service.CreateAndSendInvitationAsync(user, CancellationToken.None, requiresPasswordSetup: false);
        var token = ReadToken(emailSender.ActivationUrl!);

        var result = await service.ActivateAsync(token, null, CancellationToken.None);

        Assert.Equal(AccountActivationResult.Activated, result);
        Assert.True(user.IsActive);
        Assert.True(user.IsEmailVerified);
        Assert.Equal(PasswordVerificationResult.Success,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, "Chosen-at-signup-123"));
        Assert.False(passwordHasher.VerifyHashedPassword(user, user.PasswordHash, "AnotherPassword123!")
            is PasswordVerificationResult.Success);
    }

    [Fact]
    public async Task ActivateAsync_ExpiredTokenDoesNotActivateAccount()
    {
        await using var dbContext = CreateDbContext();
        var user = NewPendingUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var emailSender = new TestEmailSender();
        var service = CreateService(dbContext, emailSender);
        await service.CreateAndSendInvitationAsync(user, CancellationToken.None);
        var token = ReadToken(emailSender.ActivationUrl!);
        var activationToken = dbContext.AccountActivationTokens.Single();
        activationToken.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();

        var result = await service.ActivateAsync(token, "NewPassword123!", CancellationToken.None);

        Assert.Equal(AccountActivationResult.InvalidOrExpired, result);
        Assert.False(user.IsActive);
        Assert.False(user.IsEmailVerified);
    }

    [Fact]
    public async Task ActivateAsync_RelationalStoreConsumesTokenOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.MigrateAsync();
        var user = NewPendingUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var emailSender = new TestEmailSender();
        var service = CreateService(dbContext, emailSender);
        await service.CreateAndSendInvitationAsync(user, CancellationToken.None);
        var token = ReadToken(emailSender.ActivationUrl!);

        var result = await service.ActivateAsync(token, "NewPassword123!", CancellationToken.None);
        var replay = await service.ActivateAsync(token, "AnotherPassword123!", CancellationToken.None);

        Assert.Equal(AccountActivationResult.Activated, result);
        Assert.Equal(AccountActivationResult.InvalidOrExpired, replay);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, "NewPassword123!"));
    }

    [Fact]
    public async Task CreateAndSendInvitation_EmailFailureRemovesActivationToken()
    {
        await using var dbContext = CreateDbContext();
        var user = NewPendingUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, new TestEmailSender
        {
            Failure = new EmailDeliveryException("SMTP delivery failed.")
        });

        await Assert.ThrowsAsync<EmailDeliveryException>(
            () => service.CreateAndSendInvitationAsync(user, CancellationToken.None));

        Assert.Empty(dbContext.AccountActivationTokens);
        Assert.False(user.IsActive);
    }

    [Fact]
    public async Task SmtpEmailSender_UnconfiguredSmtpReportsConfigurationError()
    {
        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions()));

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            sender.SendActivationEmailAsync(
                "person@example.com",
                "person",
                "https://example.com/activate?token=token",
                requiresPasswordSetup: true,
                cancellationToken: CancellationToken.None));

        Assert.Contains("SMTP is not configured", exception.Message);
    }

    private static AccountActivationService CreateService(
        ApplicationDbContext dbContext,
        IActivationEmailSender emailSender) =>
        new(
            dbContext,
            new PasswordHasher<User>(),
            emailSender,
            Options.Create(new AccountActivationOptions
            {
                PublicUrl = "http://localhost:5092/activate",
                TokenLifetimeMinutes = 60
            }));

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    private static User NewPendingUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "new.user",
        Email = "new.user@example.com",
        PasswordHash = "placeholder",
        Role = "User",
        IsActive = false,
        IsEmailVerified = false
    };

    private static string ReadToken(string activationUrl)
    {
        var query = new Uri(activationUrl).Query;
        return QueryHelpers.ParseQuery(query)["token"].ToString();
    }

    private sealed class TestEmailSender : IActivationEmailSender
    {
        public string? Email { get; private set; }
        public string? ActivationUrl { get; private set; }
        public EmailDeliveryException? Failure { get; init; }

        public Task SendActivationEmailAsync(
            string email,
            string username,
            string activationUrl,
            bool requiresPasswordSetup,
            CancellationToken cancellationToken)
        {
            Email = email;
            ActivationUrl = activationUrl;
            if (Failure is not null)
                throw Failure;
            return Task.CompletedTask;
        }
    }
}
