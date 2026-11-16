using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class DevicePostureTests
{
    [Fact]
    public async Task TrustAsync_AllowsCompliantDeviceWhenPosturePoliciesAreEnforced()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var dbContext = CreateDbContext(connection);
        var (user, device) = await SeedUserAndDeviceAsync(
            dbContext,
            screenLockEnabled: true,
            isRootedOrJailbroken: false);

        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = "Strict Device Posture",
            PolicyType = "Device",
            IsEnforced = true,
            RulesJson = """{"requiresScreenLock":true,"blockRootedDevice":true,"blockJailbrokenDevice":true}"""
        });
        await dbContext.SaveChangesAsync();

        var service = new DeviceManagementService(dbContext);
        var result = await service.TrustAsync(device.Id, user.Id, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.None, result.Failure);
        Assert.Equal("TRUSTED", result.Response?.TrustStatus);
        Assert.Equal("ACTIVE", result.Response?.Status);
        Assert.True(result.Response?.Posture?.ScreenLockEnabled);
        Assert.False(result.Response?.Posture?.IsRootedOrJailbroken);
    }

    [Fact]
    public async Task TrustAsync_BlocksRootedOrDisabledScreenLockDevices()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var dbContext = CreateDbContext(connection);
        var (user, rootedDevice) = await SeedUserAndDeviceAsync(
            dbContext,
            screenLockEnabled: true,
            isRootedOrJailbroken: true);

        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = "Block Rooted",
            PolicyType = "Device",
            IsEnforced = true,
            RulesJson = """{"blockRootedDevice":true}"""
        });
        await dbContext.SaveChangesAsync();

        var service = new DeviceManagementService(dbContext);
        var rootedResult = await service.TrustAsync(rootedDevice.Id, user.Id, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.PostureNonCompliant, rootedResult.Failure);
        Assert.Contains("rooted or jailbroken", rootedResult.Detail, StringComparison.OrdinalIgnoreCase);

        var noLockResult = DevicePostureEvaluator.Evaluate(
            new Device { ScreenLockEnabled = false, IsRootedOrJailbroken = false },
            new SecurityPolicyEvaluation(true, false, true, RequiresScreenLock: true));
        Assert.False(noLockResult.IsCompliant);
        Assert.Contains("screen lock is disabled", noLockResult.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrustAsync_BlocksUnknownPostureWhenPolicyRequiresVerification()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var dbContext = CreateDbContext(connection);
        var (user, unknownPostureDevice) = await SeedUserAndDeviceAsync(
            dbContext,
            screenLockEnabled: null,
            isRootedOrJailbroken: null);

        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = "Require Screen Lock",
            PolicyType = "Device",
            IsEnforced = true,
            RulesJson = """{"requiresScreenLock":true}"""
        });
        await dbContext.SaveChangesAsync();

        var service = new DeviceManagementService(dbContext);
        var result = await service.TrustAsync(unknownPostureDevice.Id, user.Id, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.PostureNonCompliant, result.Failure);
        Assert.Contains("unknown or unavailable", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        var dbContext = new ApplicationDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static async Task<(User User, Device Device)> SeedUserAndDeviceAsync(
        ApplicationDbContext dbContext,
        bool? screenLockEnabled,
        bool? isRootedOrJailbroken)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "posture-user",
            Email = "posture@example.test",
            PasswordHash = "hash",
            FullName = "Posture User",
            Role = "User",
            IsActive = true,
            IsEmailVerified = true
        };
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceId = "dev-" + Guid.NewGuid().ToString("N")[..8],
            DeviceName = "Test Phone",
            DeviceType = "Phone",
            OS = "Android 15",
            Status = DeviceStatus.PendingApproval,
            TrustStatus = DeviceTrustStatus.Untrusted,
            ScreenLockEnabled = screenLockEnabled,
            IsRootedOrJailbroken = isRootedOrJailbroken,
            PostureUpdatedAt = DateTimeOffset.UtcNow,
            UserId = user.Id
        };

        dbContext.Users.Add(user);
        dbContext.Devices.Add(device);
        await dbContext.SaveChangesAsync();
        return (user, device);
    }
}
