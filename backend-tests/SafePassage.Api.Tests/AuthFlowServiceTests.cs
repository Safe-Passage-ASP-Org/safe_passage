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
    public async Task Login_WithFreshSignatureFromTrustedMobileDevice_IssuesTokenWithoutMfaChallenge()
    {
        await using var dbContext = CreateDbContext();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
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
            SigningPublicKey = ExportPublicKey(signingKey),
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

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var message = MfaApprovalSignature.CreateDeviceLoginMessage(device.DeviceId, timestamp);
        var signature = Convert.ToBase64String(signingKey.SignData(
            Encoding.UTF8.GetBytes(message),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        var result = await service.LoginAsync(new LoginRequest
        {
            Username = user.Username,
            Password = "Password123!",
            DeviceId = device.DeviceId,
            DeviceSignature = signature,
            DeviceSignatureTimestamp = timestamp
        }, CancellationToken.None);

        Assert.NotNull(result.Response);
        Assert.False(result.Response!.MfaRequired);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.AccessToken));
        Assert.Equal(device.DeviceId, result.Response.ApprovalDeviceId);

        var staleTimestamp = timestamp - 120_000;
        var staleMessage = MfaApprovalSignature.CreateDeviceLoginMessage(device.DeviceId, staleTimestamp);
        var staleSignature = Convert.ToBase64String(signingKey.SignData(
            Encoding.UTF8.GetBytes(staleMessage),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var staleResult = await service.LoginAsync(new LoginRequest
        {
            Username = user.Username,
            Password = "Password123!",
            DeviceId = device.DeviceId,
            DeviceSignature = staleSignature,
            DeviceSignatureTimestamp = staleTimestamp
        }, CancellationToken.None);

        Assert.True(staleResult.Response!.MfaRequired);
    }

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
            new LoginRequest { Username = "ALICE@example.com", Password = "Password123!", DeviceId = "device-123" },
            CancellationToken.None);

        Assert.NotNull(loginResult.Response);
        Assert.True(loginResult.Response!.MfaRequired);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Response.SessionToken));

        var usernameLogin = await service.LoginAsync(
            new LoginRequest { Username = "ALICE", Password = "Password123!", DeviceId = "device-123" },
            CancellationToken.None);
        Assert.True(usernameLogin.Response!.MfaRequired);

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
        var accessToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(status.Response.AccessToken);
        Assert.Contains(accessToken.Claims, claim => claim.Type == "device_id" && claim.Value == device.DeviceId);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }

    private static string ExportPublicKey(ECDsa signingKey)
    {
        var parameters = signingKey.ExportParameters(false);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        Array.Copy(parameters.Q.X!, 0, publicKey, 1, 32);
        Array.Copy(parameters.Q.Y!, 0, publicKey, 33, 32);
        return Convert.ToBase64String(publicKey);
    }

    private sealed class TestNotificationService : INotificationService
    {
        public Task SendMfaApprovalRequestAsync(User user, Device device, string sessionToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
