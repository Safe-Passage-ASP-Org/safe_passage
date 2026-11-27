using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class UserNotificationsTests
{
    [Fact]
    public async Task DeniedLoginActivityCreatesNotificationForTheAccountOwner()
    {
        await using var database = await CreateDatabaseAsync();
        var user = new User { Id = Guid.NewGuid(), Username = "member", Email = "member@example.test" };
        database.Users.Add(user);
        database.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Action = "Login",
            Resource = "Authentication",
            Reason = "Account is suspended.",
            Status = ActivityLogStatus.Denied
        });

        await database.SaveChangesAsync();

        var notification = await database.UserNotifications.SingleAsync();
        Assert.Equal(user.Id, notification.UserId);
        Assert.Equal("Sign-in blocked", notification.Title);
        Assert.Equal("Account is suspended.", notification.Message);
        Assert.Equal("/notifications", notification.Href);
    }

    [Fact]
    public async Task RoutineSuccessfulApplicationActivityDoesNotCreateNotification()
    {
        await using var database = await CreateDatabaseAsync();
        var user = new User { Id = Guid.NewGuid(), Username = "member", Email = "member@example.test" };
        database.Users.Add(user);
        database.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Action = "Application Access Verification",
            Resource = Guid.NewGuid().ToString(),
            Status = ActivityLogStatus.Success
        });

        await database.SaveChangesAsync();

        Assert.Empty(await database.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task UsersCanOnlyMarkTheirOwnNotificationRead()
    {
        await using var database = await CreateDatabaseAsync();
        var owner = new User { Id = Guid.NewGuid(), Username = "owner", Email = "owner@example.test" };
        var other = new User { Id = Guid.NewGuid(), Username = "other", Email = "other@example.test" };
        var notification = new UserNotification
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            Title = "Announcement",
            Message = "A message."
        };
        database.Users.AddRange(owner, other);
        database.UserNotifications.Add(notification);
        await database.SaveChangesAsync();

        var controller = new UserNotificationsController(database, new NullPushSender());
        controller.ControllerContext = CreateControllerContext(other.Id);

        var result = await controller.MarkRead(notification.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Null((await database.UserNotifications.SingleAsync()).ReadAt);
    }

    [Fact]
    public async Task PromotionsCreateNotificationsForActiveNonAdminUsers()
    {
        await using var database = await CreateDatabaseAsync();
        database.Users.AddRange(
            new User { Id = Guid.NewGuid(), Username = "member", Email = "member@example.test" },
            new User { Id = Guid.NewGuid(), Username = "admin", Email = "admin@example.test", Role = "Admin" },
            new User { Id = Guid.NewGuid(), Username = "inactive", Email = "inactive@example.test", IsActive = false });
        await database.SaveChangesAsync();
        var sender = new NullPushSender();
        var controller = new UserNotificationsController(database, sender);
        controller.ControllerContext = CreateControllerContext(Guid.NewGuid(), "Admin");

        var result = await controller.SendPromotion(
            new UserNotificationsController.PromotionRequest("Promo", "A message", "/subscription"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UserNotificationsController.PromotionResponse>(ok.Value);
        Assert.Equal(1, response.Recipients);
        var notification = await database.UserNotifications.SingleAsync();
        Assert.Equal("Promo", notification.Title);
        Assert.Equal("/subscription", notification.Href);
    }

    [Fact]
    public async Task PromotionsCanTargetOnlyMembersOfSelectedGroup()
    {
        await using var database = await CreateDatabaseAsync();
        var recipient = new User { Id = Guid.NewGuid(), Username = "group-member", Email = "member@example.test" };
        var outsider = new User { Id = Guid.NewGuid(), Username = "outsider", Email = "outsider@example.test" };
        var administrator = new User { Id = Guid.NewGuid(), Username = "group-admin", Email = "admin@example.test", Role = "Admin" };
        var inactive = new User { Id = Guid.NewGuid(), Username = "group-inactive", Email = "inactive@example.test", IsActive = false };
        var group = new Group { Id = Guid.NewGuid(), Name = "Beta testers" };
        database.Users.AddRange(recipient, outsider, administrator, inactive);
        database.Groups.Add(group);
        database.UserGroups.AddRange(
            new UserGroup { UserId = recipient.Id, GroupId = group.Id },
            new UserGroup { UserId = administrator.Id, GroupId = group.Id },
            new UserGroup { UserId = inactive.Id, GroupId = group.Id });
        await database.SaveChangesAsync();
        var controller = new UserNotificationsController(database, new NullPushSender());
        controller.ControllerContext = CreateControllerContext(Guid.NewGuid(), "Admin");

        var result = await controller.SendPromotion(
            new UserNotificationsController.PromotionRequest(
                "Beta update",
                "Try the new feature.",
                null,
                "Group",
                group.Id),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UserNotificationsController.PromotionResponse>(ok.Value);
        Assert.Equal(1, response.Recipients);
        var notification = await database.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.UserId);
        Assert.Equal("Beta update", notification.Title);
    }

    [Fact]
    public async Task PromotionsCanTargetOneSelectedMember()
    {
        await using var database = await CreateDatabaseAsync();
        var recipient = new User { Id = Guid.NewGuid(), Username = "chosen-member", Email = "chosen@example.test" };
        database.Users.AddRange(
            recipient,
            new User { Id = Guid.NewGuid(), Username = "other-member", Email = "other@example.test" });
        await database.SaveChangesAsync();
        var controller = new UserNotificationsController(database, new NullPushSender());
        controller.ControllerContext = CreateControllerContext(Guid.NewGuid(), "Admin");

        var result = await controller.SendPromotion(
            new UserNotificationsController.PromotionRequest(
                "Account update",
                "A message for one member.",
                null,
                "User",
                UserId: recipient.Id),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UserNotificationsController.PromotionResponse>(ok.Value);
        Assert.Equal(1, response.Recipients);
        Assert.Equal(recipient.Id, (await database.UserNotifications.SingleAsync()).UserId);
    }

    [Fact]
    public async Task RecipientSearchReturnsOnlyMatchingActiveNonAdminMembers()
    {
        await using var database = await CreateDatabaseAsync();
        var recipient = new User
        {
            Id = Guid.NewGuid(),
            Username = "alex-member",
            Email = "alex@example.test",
            FullName = "Alex Member"
        };
        database.Users.AddRange(
            recipient,
            new User { Id = Guid.NewGuid(), Username = "other", Email = "other@example.test" },
            new User { Id = Guid.NewGuid(), Username = "alex-admin", Email = "admin@example.test", Role = "Admin" },
            new User { Id = Guid.NewGuid(), Username = "alex-inactive", Email = "inactive@example.test", IsActive = false });
        await database.SaveChangesAsync();
        var controller = new UserNotificationsController(database, new NullPushSender());
        controller.ControllerContext = CreateControllerContext(Guid.NewGuid(), "Admin");

        var result = await controller.SearchRecipients("alex", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var results = Assert.IsType<List<UserNotificationsController.NotificationRecipientResponse>>(ok.Value);
        var found = Assert.Single(results);
        Assert.Equal(recipient.Id, found.Id);
        Assert.Equal("Alex Member", found.FullName);
    }

    [Fact]
    public async Task PromotionsRejectAnEmptyAudienceInsteadOfBroadcasting()
    {
        await using var database = await CreateDatabaseAsync();
        database.Users.Add(new User { Id = Guid.NewGuid(), Username = "member", Email = "member@example.test" });
        await database.SaveChangesAsync();
        var controller = new UserNotificationsController(database, new NullPushSender());
        controller.ControllerContext = CreateControllerContext(Guid.NewGuid(), "Admin");

        var result = await controller.SendPromotion(
            new UserNotificationsController.PromotionRequest("Promo", "A message.", null, ""),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await database.UserNotifications.ToListAsync());
    }

    private static async Task<ApplicationDbContext> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        var database = new ApplicationDbContext(options);
        await database.Database.EnsureCreatedAsync();
        return database;
    }

    private static ControllerContext CreateControllerContext(Guid userId, string role = "User") => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ], "test"))
        }
    };

    private sealed class NullPushSender : IUserNotificationPushSender
    {
        public Task SendAsync(
            IEnumerable<string> tokens,
            string title,
            string message,
            Guid notificationId,
            string? href,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
