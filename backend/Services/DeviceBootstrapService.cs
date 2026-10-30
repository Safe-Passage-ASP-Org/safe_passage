using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class DeviceBootstrapService(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IJwtTokenService jwtTokenService)
{
    public async Task<DeviceBootstrapResponse?> EnrollAsync(
        DeviceBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password)
            || string.IsNullOrWhiteSpace(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.DeviceName)
            || string.IsNullOrWhiteSpace(request.DeviceType)
            || string.IsNullOrWhiteSpace(request.OS)
            || string.IsNullOrWhiteSpace(request.SigningPublicKey))
            throw new ArgumentException("All device enrollment fields are required.");

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Username == request.Username.Trim() && candidate.IsActive,
            cancellationToken);
        if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password)
            == PasswordVerificationResult.Failed)
            return null;

        if (!MfaApprovalSignature.IsValidPublicKey(request.SigningPublicKey))
            throw new ArgumentException("The device signing key is invalid.");

        var deviceId = request.DeviceId.Trim();
        var duplicate = await dbContext.Devices.AnyAsync(
            device => device.DeviceId == deviceId && device.UserId != user.Id,
            cancellationToken);
        if (duplicate)
            throw new InvalidOperationException("This device is already registered to another account.");

        // Development bootstrap is permitted only while the account has no enrolled MFA key.
        var alreadyEnrolled = await dbContext.Devices.AnyAsync(device =>
            device.UserId == user.Id
            && device.Status == DeviceStatus.Active
            && device.TrustStatus == DeviceTrustStatus.Trusted
            && device.SigningPublicKey != null,
            cancellationToken);
        if (alreadyEnrolled)
            throw new InvalidOperationException("This account already has an enrolled trusted device. Use normal sign-in and device approval.");

        var device = await dbContext.Devices.SingleOrDefaultAsync(
            candidate => candidate.UserId == user.Id && candidate.DeviceId == deviceId,
            cancellationToken);
        device ??= await dbContext.Devices.SingleOrDefaultAsync(
            candidate => candidate.UserId == user.Id
                && candidate.DeviceId == "PRIMARY-ANDROID-001"
                && candidate.SigningPublicKey == null,
            cancellationToken);

        if (device is null)
        {
            device = new Device { Id = Guid.NewGuid(), UserId = user.Id };
            dbContext.Devices.Add(device);
        }

        device.DeviceId = deviceId;
        device.DeviceName = request.DeviceName.Trim();
        device.DeviceType = request.DeviceType.Trim();
        device.OS = request.OS.Trim();
        device.SigningPublicKey = request.SigningPublicKey;
        device.Status = DeviceStatus.Active;
        device.TrustStatus = DeviceTrustStatus.Trusted;
        device.LastActiveDate = DateTimeOffset.UtcNow;

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = user.Id, Timestamp = DateTimeOffset.UtcNow,
            Action = "First Device Enrollment", Resource = device.DeviceName,
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(jwtTokenService.CreateAccessToken(user), device.DeviceId);
    }
}
