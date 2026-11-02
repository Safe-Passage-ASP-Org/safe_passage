using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed record ApplicationAccessVerificationResult(
    bool Allowed,
    string Reason,
    Guid ApplicationId,
    bool RequiresTrustedDevice,
    DateTimeOffset EvaluatedAt);

public interface IApplicationAccessVerificationService
{
    Task<ApplicationAccessVerificationResult> VerifyAsync(
        Guid userId,
        string? deviceId,
        Guid applicationId,
        CancellationToken cancellationToken);
}

public sealed class ApplicationAccessVerificationService(
    ApplicationDbContext dbContext,
    ISecurityPolicyEvaluator policyEvaluator) : IApplicationAccessVerificationService
{
    public async Task<ApplicationAccessVerificationResult> VerifyAsync(
        Guid userId,
        string? deviceId,
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.Id, candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null || !user.IsActive)
            return await DenyAsync(applicationId, user is null ? null : user.Id, "account_inactive", cancellationToken);

        var applicationExists = await dbContext.Applications
            .AnyAsync(application => application.Id == applicationId, cancellationToken);
        if (!applicationExists)
            return await DenyAsync(applicationId, userId, "application_not_found", cancellationToken);

        var hasDirectAssignment = await dbContext.ApplicationUserAccesses
            .AnyAsync(access => access.ApplicationId == applicationId && access.UserId == userId, cancellationToken);
        var hasGroupAssignment = await dbContext.ApplicationGroupAccesses
            .AnyAsync(access => access.ApplicationId == applicationId
                && dbContext.UserGroups.Any(membership =>
                    membership.UserId == userId && membership.GroupId == access.GroupId),
                cancellationToken);

        if (!hasDirectAssignment && !hasGroupAssignment)
            return await DenyAsync(applicationId, userId, "access_not_assigned", cancellationToken);

        var policy = await policyEvaluator.EvaluateAsync(cancellationToken);
        if (!policy.IsValid)
            return await DenyAsync(applicationId, userId, "security_policy_unavailable", cancellationToken);

        if (policy.RequiresTrustedDevice)
        {
            var isTrustedDevice = !string.IsNullOrWhiteSpace(deviceId)
                && await dbContext.Devices.AnyAsync(device =>
                    device.DeviceId == deviceId
                    && device.UserId == userId
                    && device.Status == DeviceStatus.Active
                    && device.TrustStatus == DeviceTrustStatus.Trusted,
                    cancellationToken);

            if (!isTrustedDevice)
                return await DenyAsync(applicationId, userId, "trusted_device_required", cancellationToken, true);
        }

        dbContext.ActivityLogs.Add(CreateActivity(userId, applicationId, ActivityLogStatus.Success));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, "access_granted", applicationId, policy.RequiresTrustedDevice, DateTimeOffset.UtcNow);
    }

    private async Task<ApplicationAccessVerificationResult> DenyAsync(
        Guid applicationId,
        Guid? userId,
        string reason,
        CancellationToken cancellationToken,
        bool requiresTrustedDevice = false)
    {
        dbContext.ActivityLogs.Add(CreateActivity(userId, applicationId, ActivityLogStatus.Denied));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(false, reason, applicationId, requiresTrustedDevice, DateTimeOffset.UtcNow);
    }

    private static ActivityLog CreateActivity(Guid? userId, Guid applicationId, ActivityLogStatus status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Timestamp = DateTimeOffset.UtcNow,
        Action = "Application Access Verification",
        Resource = applicationId.ToString(),
        Status = status
    };
}
