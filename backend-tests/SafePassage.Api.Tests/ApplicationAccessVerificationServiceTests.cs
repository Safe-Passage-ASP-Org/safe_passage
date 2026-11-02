using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class ApplicationAccessVerificationServiceTests
{
    [Fact]
    public async Task Verify_AllowsDirectAssignment_AndRecordsSuccess()
    {
        await using var dbContext = CreateDbContext();
        var (user, application) = await SeedUserAndApplicationAsync(dbContext);
        dbContext.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            UserId = user.Id,
            ApplicationId = application.Id
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).VerifyAsync(
            user.Id, null, application.Id, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("access_granted", result.Reason);
        Assert.Equal(ActivityLogStatus.Success, (await dbContext.ActivityLogs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Verify_AllowsGroupAssignment()
    {
        await using var dbContext = CreateDbContext();
        var (user, application) = await SeedUserAndApplicationAsync(dbContext);
        var group = new Group { Id = Guid.NewGuid(), Name = "Engineering" };
        dbContext.Groups.Add(group);
        dbContext.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
        dbContext.ApplicationGroupAccesses.Add(new ApplicationGroupAccess
        {
            ApplicationId = application.Id,
            GroupId = group.Id
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).VerifyAsync(
            user.Id, null, application.Id, CancellationToken.None);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Verify_DeniesUnassignedUser_AndRecordsDenial()
    {
        await using var dbContext = CreateDbContext();
        var (user, application) = await SeedUserAndApplicationAsync(dbContext);

        var result = await CreateService(dbContext).VerifyAsync(
            user.Id, null, application.Id, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("access_not_assigned", result.Reason);
        Assert.Equal(ActivityLogStatus.Denied, (await dbContext.ActivityLogs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Verify_RequiresAnActiveTrustedDeviceWhenPolicyRequiresIt()
    {
        await using var dbContext = CreateDbContext();
        var (user, application) = await SeedUserAndApplicationAsync(dbContext);
        dbContext.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            UserId = user.Id,
            ApplicationId = application.Id
        });
        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = "Trusted devices",
            PolicyType = "Device",
            IsEnforced = true,
            RulesJson = "{\"requiresTrustedDevice\":true}"
        });
        dbContext.Devices.Add(new Device
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = "device-1",
            DeviceName = "Phone",
            DeviceType = "mobile",
            OS = "Android",
            Status = DeviceStatus.Active,
            TrustStatus = DeviceTrustStatus.Trusted
        });
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var missingDevice = await service.VerifyAsync(user.Id, null, application.Id, CancellationToken.None);
        var trustedDevice = await service.VerifyAsync(user.Id, "device-1", application.Id, CancellationToken.None);

        Assert.False(missingDevice.Allowed);
        Assert.Equal("trusted_device_required", missingDevice.Reason);
        Assert.True(trustedDevice.Allowed);
        Assert.True(trustedDevice.RequiresTrustedDevice);
    }

    private static ApplicationAccessVerificationService CreateService(ApplicationDbContext dbContext) =>
        new(dbContext, new SecurityPolicyEvaluator(dbContext));

    private static async Task<(User User, Application Application)> SeedUserAndApplicationAsync(
        ApplicationDbContext dbContext)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            IsActive = true
        };
        var application = new Application
        {
            Id = Guid.NewGuid(),
            Name = "HR Portal"
        };
        dbContext.Users.Add(user);
        dbContext.Applications.Add(application);
        await dbContext.SaveChangesAsync();
        return (user, application);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
