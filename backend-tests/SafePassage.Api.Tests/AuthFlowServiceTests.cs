using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Tests;

public sealed class AuthFlowServiceTests
{
    [Fact]
    public async Task Login_Then_Approve_Then_GetStatus_IssuesToken()
    {
        await using var dbContext = CreateDbContext();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            PasswordHash = new PasswordHasher<User>().HashPassword(null!, "Password123!"),
            Role = "User",
            IsActive = true
        };

        var device = new Device
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = "device-123",
            DeviceName = "Pixel 9",
            DeviceType = "mobile",
            OS = "Android 15",
            Status = DeviceStatus.Active,
            TrustStatus = DeviceTrustStatus.Trusted,
            User = user
        };

        dbContext.Users.Add(user);
        dbContext.Devices.Add(device);
        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = "Baseline authentication",
            PolicyType = "Authentication",
            IsEnforced = true,
            RulesJson = "{\"requiresTrustedDevice\":true,\"allowTrustedDeviceMfaBypass\":false,\"requiresStepUp\":false,\"requireMfa\":true,\"requiresScreenLock\":true,\"blockRootedDevice\":true,\"blockJailbrokenDevice\":true}"
        });
        await dbContext.SaveChangesAsync();

        var settings = new JwtSettings
        {
            Issuer = "safe-passage",
            Audience = "safe-passage-clients",
            SigningKey = new string('x', 64),
            AccessTokenMinutes = 60
        };

        var service = new AuthFlowService(
            dbContext,
            new PasswordHasher<User>(),
            new TestNotificationService(),
            new JwtTokenService(settings),
            new SecurityPolicyEvaluator(dbContext),
            NullLogger<AuthFlowService>.Instance);

        var loginResult = await service.LoginAsync(
            new LoginRequest { Username = "alice", Password = "Password123!", DeviceId = "device-123" },
            CancellationToken.None);

        Assert.NotNull(loginResult.Response);
        Assert.True(loginResult.Response!.MfaRequired);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Response.SessionToken));

        var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = new byte[65];
        var parameters = signingKey.ExportParameters(false);
        publicKey[0] = 0x04;
        Array.Copy(parameters.Q.X!, 0, publicKey, 1, 32);
        Array.Copy(parameters.Q.Y!, 0, publicKey, 33, 32);
        var publicKeyEncoded = Convert.ToBase64String(publicKey);

        device.SigningPublicKey = publicKeyEncoded;
        await dbContext.SaveChangesAsync();

        var message = MfaApprovalSignature.CreateMessage(loginResult.Response.SessionToken!, device.DeviceId);
        var signature = signingKey.SignData(
            Encoding.UTF8.GetBytes(message),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signatureEncoded = Convert.ToBase64String(signature);

        var approval = await service.ApproveMfaAsync(
            new MfaApprovalRequest
            {
                SessionToken = loginResult.Response.SessionToken!,
                DeviceId = device.DeviceId,
                Signature = signatureEncoded
            },
            CancellationToken.None);

        Assert.Equal(MfaSessionStatus.Approved, approval.Response!.Status);

        var status = await service.GetMfaStatusAsync(loginResult.Response.SessionToken!, CancellationToken.None);

        Assert.Equal(MfaSessionStatus.Approved, status.Response!.Status);
        Assert.False(string.IsNullOrWhiteSpace(status.Response.AccessToken));
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    private sealed class TestNotificationService : INotificationService
    {
        public Task SendMfaApprovalRequestAsync(User user, Device device, string sessionToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
