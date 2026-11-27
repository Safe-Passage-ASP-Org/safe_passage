using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class AdminActivityControllerTests
{
    [Fact]
    public async Task GetRecentFiltersByDateActionUserAndGroupInTheDatabase()
    {
        await using var database = await CreateDatabaseAsync();
        var member = NewUser("member");
        var outsider = NewUser("outsider");
        var group = new Group { Id = Guid.NewGuid(), Name = "Support" };
        database.Users.AddRange(member, outsider);
        database.Groups.Add(group);
        database.UserGroups.Add(new UserGroup { UserId = member.Id, GroupId = group.Id });
        database.ActivityLogs.AddRange(
            NewActivity(member.Id, "Successful Login", ActivityLogStatus.Success, new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero)),
            NewActivity(member.Id, "Device Trusted", ActivityLogStatus.Success, new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero)),
            NewActivity(outsider.Id, "Successful Login", ActivityLogStatus.Success, new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero)),
            NewActivity(member.Id, "Successful Login", ActivityLogStatus.Success, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero)));
        await database.SaveChangesAsync();
        var controller = new AdminActivityController(database);

        var dateResult = await controller.GetRecent(
            fromDate: new DateOnly(2026, 11, 1),
            toDate: new DateOnly(2026, 11, 30),
            cancellationToken: CancellationToken.None);
        var dateRows = Assert.IsAssignableFrom<IReadOnlyList<AdminActivityController.AdminActivityRecord>>(
            Assert.IsType<OkObjectResult>(dateResult.Result).Value);
        Assert.Equal(3, dateRows.Count);

        var result = await controller.GetRecent(
            action: "Successful Login",
            userId: member.Id,
            groupId: group.Id,
            fromDate: new DateOnly(2026, 11, 1),
            toDate: new DateOnly(2026, 11, 30),
            cancellationToken: CancellationToken.None);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<AdminActivityController.AdminActivityRecord>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var row = Assert.Single(rows);
        Assert.Equal(member.Id, row.UserId);
        Assert.Equal("member", row.Username);
        Assert.Equal("Successful Login", row.Action);
    }

    [Fact]
    public async Task GetSummaryCountsActivityAndLoginOutcomesForSelectedUser()
    {
        await using var database = await CreateDatabaseAsync();
        var member = NewUser("member");
        var outsider = NewUser("outsider");
        database.Users.AddRange(member, outsider);
        database.ActivityLogs.AddRange(
            NewActivity(member.Id, "Login", ActivityLogStatus.Success, DateTimeOffset.UtcNow),
            NewActivity(member.Id, "Login", ActivityLogStatus.Denied, DateTimeOffset.UtcNow),
            NewActivity(member.Id, "MFA", ActivityLogStatus.Success, DateTimeOffset.UtcNow),
            NewActivity(outsider.Id, "Login", ActivityLogStatus.Success, DateTimeOffset.UtcNow));
        await database.SaveChangesAsync();
        var controller = new AdminActivityController(database);

        var result = await controller.GetSummary(userId: member.Id, cancellationToken: CancellationToken.None);

        var summary = Assert.IsType<AdminActivityController.AdminActivitySummary>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(1, summary.SuccessfulLogins);
        Assert.Equal(1, summary.DeniedLogins);
        Assert.Equal(1, summary.MfaEvents);
        Assert.Equal(2, summary.ActionCounts!.Single(action => action.Action == "Login").Count);
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

    private static User NewUser(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = $"{username}@example.test",
        PasswordHash = "hash"
    };

    private static ActivityLog NewActivity(
        Guid userId,
        string action,
        ActivityLogStatus status,
        DateTimeOffset timestamp) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Timestamp = timestamp,
        Action = action,
        Resource = action,
        Status = status
    };
}
