using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class AccountRegistrationServiceTests
{
    [Fact]
    public async Task RegisterAsync_ValidDetailsCreatesInactiveUserAndSendsActivation()
    {
        await using var dbContext = CreateDbContext();
        var activationService = new TestAccountActivationService();
        var service = new AccountRegistrationService(
            dbContext,
            new PasswordHasher<User>(),
            activationService);

        var result = await service.RegisterAsync(
            new RegisterRequest
            {
                FullName = "  Alex Example ",
                Username = "  Alex.Example_1 ",
                Email = "Alex.Example@Example.com ",
                PhoneNumber = " +1 555 123 4567 ",
                Password = "A-secure-password-123"
            },
            CancellationToken.None);

        var user = Assert.Single(dbContext.Users);
        Assert.Equal(AccountRegistrationResult.Registered, result);
        Assert.Equal("Alex Example", user.FullName);
        Assert.Equal("alex.example_1", user.Username);
        Assert.Equal("alex.example@example.com", user.Email);
        Assert.True(user.Username.Length <= 100);
        Assert.Equal("+1 555 123 4567", user.PhoneNumber);
        Assert.Equal("User", user.Role);
        Assert.False(user.IsActive);
        Assert.False(user.IsEmailVerified);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, "A-secure-password-123"));
        Assert.Same(user, activationService.ActivatedUser);
        Assert.False(activationService.RequiresPasswordSetup);
        Assert.Equal("User Registered", Assert.Single(dbContext.ActivityLogs).Action);
    }

    [Fact]
    public async Task RegisterAsync_ExistingEmailIgnoringCaseDoesNotCreateAnotherUser()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "existing",
            Email = "person@example.com",
            PasswordHash = "placeholder",
            Role = "User"
        });
        await dbContext.SaveChangesAsync();
        var activationService = new TestAccountActivationService();
        var service = new AccountRegistrationService(
            dbContext,
            new PasswordHasher<User>(),
            activationService);

        var result = await service.RegisterAsync(
            new RegisterRequest
            {
                FullName = "Another Person",
                Username = "another-person",
                Email = "PERSON@example.com",
                PhoneNumber = "+1 555 111 2222",
                Password = "A-secure-password-123"
            },
            CancellationToken.None);

        Assert.Equal(AccountRegistrationResult.AlreadyExists, result);
        Assert.Single(dbContext.Users);
        Assert.Null(activationService.ActivatedUser);
    }

    [Fact]
    public async Task RegisterAsync_UsernameAlreadyUsedIgnoringCaseReturnsUsernameUnavailable()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "chosen.name",
            Email = "existing@example.com",
            PasswordHash = "placeholder",
            Role = "User"
        });
        await dbContext.SaveChangesAsync();
        var activationService = new TestAccountActivationService();
        var service = new AccountRegistrationService(
            dbContext,
            new PasswordHasher<User>(),
            activationService);

        var result = await service.RegisterAsync(
            new RegisterRequest
            {
                FullName = "Another Person",
                Username = " CHOSEN.NAME ",
                Email = "new@example.com",
                PhoneNumber = "+1 555 111 2222",
                Password = "A-secure-password-123"
            },
            CancellationToken.None);

        Assert.Equal(AccountRegistrationResult.UsernameUnavailable, result);
        Assert.Single(dbContext.Users);
        Assert.Null(activationService.ActivatedUser);
    }

    [Fact]
    public async Task RegisterAsync_EmailFailureRemovesPendingUserAndAuditEvent()
    {
        await using var dbContext = CreateDbContext();
        var activationService = new TestAccountActivationService
        {
            Failure = new EmailDeliveryException("SMTP delivery failed.")
        };
        var service = new AccountRegistrationService(
            dbContext,
            new PasswordHasher<User>(),
            activationService);

        var result = await service.RegisterAsync(
            new RegisterRequest
            {
                FullName = "Alex Example",
                Username = "alex-example",
                Email = "alex@example.com",
                PhoneNumber = "+1 555 123 4567",
                Password = "A-secure-password-123"
            },
            CancellationToken.None);

        Assert.Equal(AccountRegistrationResult.EmailDeliveryFailed, result);
        Assert.Empty(dbContext.Users);
        Assert.Empty(dbContext.ActivityLogs);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class TestAccountActivationService : IAccountActivationService
    {
        public User? ActivatedUser { get; private set; }
        public bool RequiresPasswordSetup { get; private set; }
        public EmailDeliveryException? Failure { get; init; }

        public Task CreateAndSendInvitationAsync(
            User user,
            CancellationToken cancellationToken,
            bool requiresPasswordSetup = true)
        {
            ActivatedUser = user;
            RequiresPasswordSetup = requiresPasswordSetup;
            if (Failure is not null)
                throw Failure;
            return Task.CompletedTask;
        }

        public Task<AccountActivationResult> ActivateAsync(
            string token,
            string? password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
