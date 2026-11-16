using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class PendingDeviceRegistrationService(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IDeviceManagementService deviceManagement)
{
    private readonly User _dummyUser = new();
    private readonly string _dummyPasswordHash = passwordHasher.HashPassword(
        new User(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public async Task<DeviceManagementResult<DeviceResponse>?> RegisterAsync(
        PendingDeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var identifier = request.Username.Trim();
        var normalizedIdentifier = identifier.ToUpperInvariant();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.IsActive
                && (candidate.Username.ToUpper() == normalizedIdentifier
                    || candidate.Email.ToUpper() == normalizedIdentifier),
            cancellationToken);

        var verification = passwordHasher.VerifyHashedPassword(
            user ?? _dummyUser,
            user?.PasswordHash ?? _dummyPasswordHash,
            request.Password);
        if (user is null || verification == PasswordVerificationResult.Failed)
            return null;

        return await deviceManagement.RegisterAsync(user.Id, new DeviceRegistrationRequest
        {
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName,
            DeviceType = request.DeviceType,
            OS = request.OS,
            SigningPublicKey = request.SigningPublicKey,
            Posture = request.Posture
        }, cancellationToken);
    }

    public async Task<DeviceManagementResult<DeviceResponse>?> RecoverSigningKeyAsync(
        PendingDeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var identifier = request.Username.Trim();
        var normalizedIdentifier = identifier.ToUpperInvariant();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.IsActive
                && (candidate.Username.ToUpper() == normalizedIdentifier
                    || candidate.Email.ToUpper() == normalizedIdentifier),
            cancellationToken);

        var verification = passwordHasher.VerifyHashedPassword(
            user ?? _dummyUser,
            user?.PasswordHash ?? _dummyPasswordHash,
            request.Password);
        if (user is null || verification == PasswordVerificationResult.Failed)
            return null;

        return await deviceManagement.RequestSigningKeyRecoveryAsync(user.Id, new DeviceSigningKeyRequest
        {
            DeviceId = request.DeviceId,
            SigningPublicKey = request.SigningPublicKey
        }, cancellationToken);
    }
}
