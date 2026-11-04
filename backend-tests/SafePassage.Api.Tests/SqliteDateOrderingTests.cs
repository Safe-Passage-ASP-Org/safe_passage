using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class SqliteDateOrderingTests
{
    [Fact]
    public async Task DashboardQueries_OrderDevicesAndActivityOnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.MigrateAsync();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "dashboard-user",
            Email = "dashboard@example.com"
        };
        var older = DateTimeOffset.UtcNow.AddDays(-1);
        var newer = DateTimeOffset.UtcNow;
        dbContext.Users.Add(user);
        dbContext.Devices.AddRange(
            CreateDevice(user.Id, "older", older),
            CreateDevice(user.Id, "newer", newer));
        dbContext.ActivityLogs.AddRange(
            CreateActivity(user.Id, "older", older),
            CreateActivity(user.Id, "newer", newer));
        await dbContext.SaveChangesAsync();

        var devices = await new DeviceManagementService(dbContext)
            .GetAllAsync(user.Id, includeAll: true, CancellationToken.None);
        var activity = await new AdminActivityController(dbContext)
            .GetRecent(CancellationToken.None);

        Assert.Equal(new[] { "newer", "older" }, devices.Select(device => device.DeviceId));
        var response = Assert.IsType<OkObjectResult>(activity.Result);
        var logs = Assert.IsAssignableFrom<IReadOnlyList<AdminActivityController.AdminActivityRecord>>(response.Value);
        Assert.Equal(new[] { "newer", "older" }, logs.Select(log => log.Resource));
    }

    private static Device CreateDevice(Guid userId, string deviceId, DateTimeOffset registrationDate) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        DeviceId = deviceId,
        DeviceName = deviceId,
        DeviceType = "Phone",
        OS = "Android",
        RegistrationDate = registrationDate,
        Status = DeviceStatus.Active,
        TrustStatus = DeviceTrustStatus.Trusted
    };

    private static ActivityLog CreateActivity(Guid userId, string resource, DateTimeOffset timestamp) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Action = "Test",
        Resource = resource,
        Timestamp = timestamp,
        Status = ActivityLogStatus.Success
    };
}
