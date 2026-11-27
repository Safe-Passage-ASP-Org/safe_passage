using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class ZeroTrustAuditAndSessionTests
{
    [Fact]
    public async Task RegisterAsync_BlocksNonCompliantPosture_AndRequiresApprovalForSecondDevice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "enroll-user",
            Email = "enroll@example.test",
            PasswordHash = "hash",
            IsActive = true,
            IsEmailVerified = true
        };
        dbContext.Users.Add(user);
        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = "Require Screen Lock",
            PolicyType = "Device",
            IsEnforced = true,
            RulesJson = """{"requiresScreenLock":true,"blockRootedDevice":true}"""
        });
        await dbContext.SaveChangesAsync();

        var service = new DeviceManagementService(dbContext);
        var key = CreateSigningPublicKey();

        var blocked = await service.RegisterAsync(user.Id, new DeviceRegistrationRequest
        {
            DeviceId = "dev-bad",
            DeviceName = "Rooted Phone",
            DeviceType = "Phone",
            OS = "Android 15",
            SigningPublicKey = key,
            Posture = new DevicePostureRequest(ScreenLockEnabled: false, IsRootedOrJailbroken: true)
        }, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.PostureNonCompliant, blocked.Failure);

        var first = await service.RegisterAsync(user.Id, new DeviceRegistrationRequest
        {
            DeviceId = "dev-1",
            DeviceName = "Primary Phone",
            DeviceType = "Phone",
            OS = "Android 15",
            SigningPublicKey = key,
            Posture = new DevicePostureRequest(ScreenLockEnabled: true, IsRootedOrJailbroken: false)
        }, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.None, first.Failure);
        Assert.Equal("ACTIVE", first.Response?.Status);
        Assert.Equal("TRUSTED", first.Response?.TrustStatus);

        var second = await service.RegisterAsync(user.Id, new DeviceRegistrationRequest
        {
            DeviceId = "dev-2",
            DeviceName = "Secondary Phone",
            DeviceType = "Phone",
            OS = "Android 15",
            SigningPublicKey = CreateSigningPublicKey(),
            Posture = new DevicePostureRequest(ScreenLockEnabled: true, IsRootedOrJailbroken: false)
        }, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.None, second.Failure);
        Assert.Equal("PENDING_APPROVAL", second.Response?.Status);
        Assert.Equal("UNTRUSTED", second.Response?.TrustStatus);
    }

    [Fact]
    public async Task RevokeAsync_RevokesSessionsAndLogsTargetUserActivity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Email = "admin@example.test",
            PasswordHash = "hash",
            Role = "Admin",
            IsActive = true
        };
        var target = new User
        {
            Id = Guid.NewGuid(),
            Username = "target",
            Email = "target@example.test",
            PasswordHash = "hash",
            IsActive = true
        };
        var device = new Device
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            DeviceId = "target-phone",
            DeviceName = "Target Phone",
            DeviceType = "Phone",
            OS = "Android 15",
            Status = DeviceStatus.Active,
            TrustStatus = DeviceTrustStatus.Trusted
        };
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            DeviceId = device.DeviceId,
            TokenJti = "jti-123",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        dbContext.Users.AddRange(admin, target);
        dbContext.Devices.Add(device);
        dbContext.UserSessions.Add(session);
        await dbContext.SaveChangesAsync();

        var service = new DeviceManagementService(dbContext);
        var result = await service.RevokeAsync(device.Id, admin.Id, CancellationToken.None);

        Assert.Equal(DeviceManagementFailure.None, result.Failure);
        var updatedSession = await dbContext.UserSessions.SingleAsync(s => s.Id == session.Id);
        Assert.NotNull(updatedSession.RevokedAt);
        Assert.Equal("DeviceRevoked", updatedSession.RevokedReason);

        var log = await dbContext.ActivityLogs.SingleAsync(l => l.Action == "Device Revoked");
        Assert.Equal(admin.Id, log.ActorUserId);
        Assert.Equal(target.Id, log.TargetUserId);
        Assert.Equal(target.Id, log.UserId);
        Assert.Equal("target-phone", log.DeviceId);
    }

    [Fact]
    public async Task SetStatusFalse_RevokesActiveUserSessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);

        var admin = new User { Id = Guid.NewGuid(), Username = "admin2", Email = "admin2@example.test", PasswordHash = "hash", Role = "Admin", IsActive = true };
        var target = new User { Id = Guid.NewGuid(), Username = "target2", Email = "target2@example.test", PasswordHash = "hash", IsActive = true };
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            DeviceId = "dev-x",
            TokenJti = "jti-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        dbContext.Users.AddRange(admin, target);
        dbContext.UserSessions.Add(session);
        await dbContext.SaveChangesAsync();

        var controller = new UsersController(
            dbContext,
            new PasswordHasher<User>(),
            null!,
            null!,
            Options.Create(new SubscriptionExpirationReminderOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString())], "test"))
                }
            }
        };

        var result = await controller.SetStatus(target.Id, new UsersController.UserStatusRequest(false), CancellationToken.None);
        Assert.IsType<NoContentResult>(result);

        var updatedSession = await dbContext.UserSessions.SingleAsync(s => s.Id == session.Id);
        Assert.NotNull(updatedSession.RevokedAt);
        Assert.Equal("AccountSuspended", updatedSession.RevokedReason);
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

    private static string CreateSigningPublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        return Convert.ToBase64String([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);
    }
}
