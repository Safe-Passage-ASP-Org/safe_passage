using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public enum DeviceManagementFailure
{
    None,
    UserUnavailable,
    DuplicateDevice,
    NotFound,
    InvalidSigningKey,
    SigningKeyAlreadyRegistered
}

public sealed record DeviceManagementResult<T>(T? Response, DeviceManagementFailure Failure)
{
    public static DeviceManagementResult<T> Success(T response) => new(response, DeviceManagementFailure.None);
    public static DeviceManagementResult<T> Failed(DeviceManagementFailure failure) => new(default, failure);
}

public interface IDeviceManagementService
{
    Task<IReadOnlyList<DeviceResponse>> GetAllAsync(Guid requesterId, bool includeAll, CancellationToken cancellationToken);
    Task<DeviceManagementResult<DeviceResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<DeviceManagementResult<DeviceResponse>> RegisterAsync(Guid userId, DeviceRegistrationRequest request, CancellationToken cancellationToken);
    Task<DeviceManagementResult<DeviceResponse>> EnrollSigningKeyAsync(Guid userId, DeviceSigningKeyRequest request, CancellationToken cancellationToken);
    Task<DeviceManagementResult<DeviceResponse>> TrustAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
    Task<DeviceManagementResult<DeviceResponse>> RevokeAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
}

public sealed class DeviceManagementService(ApplicationDbContext dbContext) : IDeviceManagementService
{
    public async Task<IReadOnlyList<DeviceResponse>> GetAllAsync(
        Guid requesterId,
        bool includeAll,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Devices.AsNoTracking();
        if (!includeAll)
            query = query.Where(device => device.UserId == requesterId);

        var devices = await query.ToListAsync(cancellationToken);
        return devices
            .OrderByDescending(device => device.RegistrationDate)
            .Select(ToResponse)
            .ToList();
    }

    public async Task<DeviceManagementResult<DeviceResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return device is null
            ? DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.NotFound)
            : DeviceManagementResult<DeviceResponse>.Success(ToResponse(device));
    }

    public async Task<DeviceManagementResult<DeviceResponse>> RegisterAsync(
        Guid userId,
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (!MfaApprovalSignature.IsValidPublicKey(request.SigningPublicKey))
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.InvalidSigningKey);

        var userIsActive = await dbContext.Users.AnyAsync(
            user => user.Id == userId && user.IsActive,
            cancellationToken);
        if (!userIsActive)
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.UserUnavailable);

        var deviceExists = await dbContext.Devices.AnyAsync(
            device => device.DeviceId == request.DeviceId,
            cancellationToken);
        if (deviceExists)
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.DuplicateDevice);

        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceId = request.DeviceId.Trim(),
            DeviceName = request.DeviceName.Trim(),
            DeviceType = request.DeviceType.Trim(),
            OS = request.OS.Trim(),
            RegistrationDate = DateTimeOffset.UtcNow,
            Status = DeviceStatus.PendingApproval,
            TrustStatus = DeviceTrustStatus.Untrusted,
            SigningPublicKey = request.SigningPublicKey,
            UserId = userId
        };

        dbContext.Devices.Add(device);
        AddActivity(userId, "Device Registration", device.DeviceName, ActivityLogStatus.Success);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.DuplicateDevice);
        }

        return DeviceManagementResult<DeviceResponse>.Success(ToResponse(device));
    }

    public async Task<DeviceManagementResult<DeviceResponse>> EnrollSigningKeyAsync(
        Guid userId,
        DeviceSigningKeyRequest request,
        CancellationToken cancellationToken)
    {
        if (!MfaApprovalSignature.IsValidPublicKey(request.SigningPublicKey))
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.InvalidSigningKey);

        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate =>
            candidate.UserId == userId
            && candidate.DeviceId == request.DeviceId
            && candidate.Status == DeviceStatus.Active
            && candidate.TrustStatus == DeviceTrustStatus.Trusted,
            cancellationToken);
        if (device is null)
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.NotFound);

        if (!string.IsNullOrWhiteSpace(device.SigningPublicKey))
        {
            return string.Equals(device.SigningPublicKey, request.SigningPublicKey, StringComparison.Ordinal)
                ? DeviceManagementResult<DeviceResponse>.Success(ToResponse(device))
                : DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.SigningKeyAlreadyRegistered);
        }

        device.SigningPublicKey = request.SigningPublicKey;
        AddActivity(userId, "Device Signing Key Enrolled", device.DeviceId, ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        return DeviceManagementResult<DeviceResponse>.Success(ToResponse(device));
    }

    public async Task<DeviceManagementResult<DeviceResponse>> TrustAsync(
        Guid id,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (device is null)
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.NotFound);

        device.Status = DeviceStatus.Active;
        device.TrustStatus = DeviceTrustStatus.Trusted;
        AddActivity(actorId, "Device Trusted", device.DeviceId, ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        return DeviceManagementResult<DeviceResponse>.Success(ToResponse(device));
    }

    public async Task<DeviceManagementResult<DeviceResponse>> RevokeAsync(
        Guid id,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (device is null)
            return DeviceManagementResult<DeviceResponse>.Failed(DeviceManagementFailure.NotFound);

        device.Status = DeviceStatus.Disabled;
        device.TrustStatus = DeviceTrustStatus.Untrusted;
        AddActivity(actorId, "Device Revoked", device.DeviceId, ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        return DeviceManagementResult<DeviceResponse>.Success(ToResponse(device));
    }

    private void AddActivity(Guid userId, string action, string resource, ActivityLogStatus status) =>
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Resource = resource,
            Status = status
        });

    private static DeviceResponse ToResponse(Device device) => new(
        device.Id,
        device.DeviceId,
        device.DeviceName,
        device.DeviceType,
        device.OS,
        device.RegistrationDate,
        device.LastActiveDate,
        device.Status switch
        {
            DeviceStatus.PendingApproval => "PENDING_APPROVAL",
            DeviceStatus.Active => "ACTIVE",
            DeviceStatus.Disabled => "DISABLED",
            _ => "UNKNOWN"
        },
        device.TrustStatus == DeviceTrustStatus.Trusted ? "TRUSTED" : "UNTRUSTED",
        device.UserId);
}