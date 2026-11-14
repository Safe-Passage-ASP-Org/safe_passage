using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class SharedDeviceEnrollmentTests
{
    [Fact]
    public async Task Bootstrap_AllowsSamePhysicalDeviceForAnotherAccountWithSeparateTrustRecord()
    {
        await using var dbContext = CreateDbContext();
        var existingUser = CreateUser("existing", "existing@example.com", "existing-password");
        var newUser = CreateUser("admin.jayshan", "yanyangodornes@gmail.com", "new-password");
        var signingPublicKey = CreateSigningPublicKey();
        dbContext.Users.AddRange(existingUser, newUser);
        dbContext.Devices.Add(new Device
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            DeviceId = "shared-phone",
            DeviceName = "Phone",
            DeviceType = "Phone",
            OS = "Android",
            Status = DeviceStatus.Active,
            TrustStatus = DeviceTrustStatus.Trusted,
            SigningPublicKey = signingPublicKey
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).EnrollAsync(
            new DeviceBootstrapRequest
            {
                Username = newUser.Username,
                Password = "new-password",
                DeviceId = "shared-phone",
                DeviceName = "Phone",
                DeviceType = "Phone",
                OS = "Android",
                SigningPublicKey = signingPublicKey
            },
            CancellationToken.None);

        Assert.NotNull(result);
        var devices = await dbContext.Devices.OrderBy(device => device.UserId).ToListAsync();
        Assert.Equal(2, devices.Count);
        Assert.All(devices, device => Assert.Equal("shared-phone", device.DeviceId));
        Assert.Equal(newUser.Id, devices.Single(device => device.UserId == newUser.Id).UserId);
        Assert.Equal(DeviceTrustStatus.Trusted, devices.Single(device => device.UserId == newUser.Id).TrustStatus);
    }

    private static User CreateUser(string username, string email, string password)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            IsActive = true
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        return user;
    }

    private static DeviceBootstrapService CreateService(ApplicationDbContext dbContext) =>
        new(dbContext, new PasswordHasher<User>(), new TestJwtTokenService());

    private static string CreateSigningPublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        return Convert.ToBase64String([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);
    }

    private static ApplicationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class TestJwtTokenService : IJwtTokenService
    {
        public string CreateAccessToken(User user, string? deviceId = null) => "test-token";
    }
}
