using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class UsersControllerInvitationTests
{
    [Fact]
    public async Task Create_WithoutPassword_CreatesInactiveAccountAwaitingActivation()
    {
        await using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext, new TestActivationService());

        var result = await controller.Create(
            new UsersController.UserRequest(
                "new.user",
                "new.user@example.com",
                "User",
                []),
            CancellationToken.None);

        var user = Assert.Single(dbContext.Users);
        Assert.False(user.IsActive);
        Assert.False(user.IsEmailVerified);
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Create_WhenInvitationDeliveryFails_RemovesPendingAccount()
    {
        await using var dbContext = CreateDbContext();
        var controller = CreateController(
            dbContext,
            new TestActivationService { Failure = new EmailDeliveryException("SMTP delivery failed.") });

        var result = await controller.Create(
            new UsersController.UserRequest("new.user", "new.user@example.com", "User", []),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(dbContext.Users);
        Assert.Empty(dbContext.ActivityLogs);
        Assert.Empty(dbContext.AccountActivationTokens);
    }

    private static UsersController CreateController(
        ApplicationDbContext dbContext,
        IAccountActivationService activationService)
    {
        var controller = new UsersController(dbContext, new PasswordHasher<User>(), activationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    private sealed class TestActivationService : IAccountActivationService
    {
        public EmailDeliveryException? Failure { get; init; }

        public Task CreateAndSendInvitationAsync(User user, CancellationToken cancellationToken)
        {
            if (Failure is not null)
                throw Failure;
            return Task.CompletedTask;
        }

        public Task<AccountActivationResult> ActivateAsync(
            string token,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountActivationResult.InvalidOrExpired);
    }
}
