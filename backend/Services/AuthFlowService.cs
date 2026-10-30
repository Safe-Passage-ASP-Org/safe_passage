using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class AuthFlowService(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    INotificationService notificationService,
    IJwtTokenService jwtTokenService,
    ISecurityPolicyEvaluator securityPolicyEvaluator,
    ILogger<AuthFlowService> logger) : IAuthFlowService
{
    private static readonly TimeSpan MfaLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongPollDuration = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly User _dummyUser = new();
    private readonly string _dummyPasswordHash = passwordHasher.HashPassword(new User(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public async Task<AuthFlowResult<LoginResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Username == request.Username,
            cancellationToken);
        var verification = passwordHasher.VerifyHashedPassword(
            user ?? _dummyUser,
            user?.PasswordHash ?? _dummyPasswordHash,
            request.Password);

        if (user is null || !user.IsActive || verification == PasswordVerificationResult.Failed)
        {
            AddActivity(user?.Id, "Login", "Authentication", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        var policy = await securityPolicyEvaluator.EvaluateAsync(cancellationToken);
        if (policy.IsValid && !policy.RequiresStepUp)
        {
            AddActivity(user.Id, "Login", "MFA not required by enforced policy", ActivityLogStatus.Success);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Success(new(
                false,
                null,
                request.DeviceId,
                null,
                null,
                jwtTokenService.CreateAccessToken(user),
                "Bearer",
                "Credentials accepted; no MFA challenge is required by the enforced policy."));
        }

        if (policy.IsValid && policy.AllowsTrustedDeviceMfaBypass && !string.IsNullOrWhiteSpace(request.DeviceId))
        {
            var trustedDevice = await dbContext.Devices.FirstOrDefaultAsync(candidate =>
                candidate.UserId == user.Id
                && candidate.DeviceId == request.DeviceId
                && candidate.Status == DeviceStatus.Active
                && candidate.TrustStatus == DeviceTrustStatus.Trusted,
                cancellationToken);

            if (trustedDevice is not null)
            {
                trustedDevice.LastActiveDate = DateTimeOffset.UtcNow;
                AddActivity(user.Id, "Login", "Trusted-device MFA bypass allowed by policy", ActivityLogStatus.Success);
                await dbContext.SaveChangesAsync(cancellationToken);
                return AuthFlowResult<LoginResponse>.Success(new(
                    false,
                    null,
                    trustedDevice.DeviceId,
                    null,
                    null,
                    jwtTokenService.CreateAccessToken(user),
                    "Bearer",
                    "Credentials accepted; MFA was skipped by trusted-device policy."));
            }
        }

        var device = await dbContext.Devices
            .Where(candidate => candidate.UserId == user.Id
                && candidate.Status == DeviceStatus.Active
                && (candidate.DeviceType.ToLower() == "mobile"
                    || candidate.DeviceType.ToLower() == "phone"
                    || candidate.OS.ToLower().StartsWith("android")
                    || candidate.OS.ToLower().StartsWith("ios")))
            .OrderByDescending(candidate => candidate.TrustStatus == DeviceTrustStatus.Trusted)
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            AddActivity(user.Id, "Login", "No active mobile device available for MFA", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NoActiveMobileDevice);
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(MfaLifetime);
        var sessionToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var session = new MfaSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceId = device.Id,
            SessionToken = HashSessionToken(sessionToken),
            Status = MfaSessionStatus.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        dbContext.MfaSessions.Add(session);
        AddActivity(user.Id, "Login", "Primary credentials accepted; MFA pending", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await notificationService.SendMfaApprovalRequestAsync(user, device, sessionToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mock MFA notification failed for user {UserId}.", user.Id);
            session.Status = MfaSessionStatus.Denied;
            AddActivity(user.Id, "MFA Push", "MFA notification delivery failed", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NotificationFailed);
        }

        return AuthFlowResult<LoginResponse>.Success(new(
            true,
            sessionToken,
            device.DeviceId,
            MfaSessionStatus.Pending,
            expiresAt,
            null,
            null,
            "Credentials accepted. Approve the sign-in on your registered device."));
    }

    public async Task<AuthFlowResult<MfaActionResponse>> ApproveMfaAsync(
        MfaApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(request.SessionToken, cancellationToken);
        if (session is null)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionNotFound);

        if (await ExpireIfNeededAsync(session, cancellationToken))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionExpired);

        if (session.Status != MfaSessionStatus.Pending)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidSessionState);

        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate => candidate.Id == session.DeviceId, cancellationToken);
        if (device is null || device.Status != DeviceStatus.Active || device.TrustStatus != DeviceTrustStatus.Trusted)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceUnavailable);

        if (string.IsNullOrWhiteSpace(device.SigningPublicKey))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceSigningKeyNotRegistered);

        if (!string.Equals(device.DeviceId, request.DeviceId, StringComparison.Ordinal)
            || !MfaApprovalSignature.Verify(device.SigningPublicKey, request.SessionToken, request.DeviceId, request.Signature))
        {
            AddActivity(session.UserId, "MFA Approval Rejected", "Invalid device-bound approval signature", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidApprovalSignature);
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == session.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            session.Status = MfaSessionStatus.Denied;
            AddActivity(session.UserId, "Security Alert: MFA Denied", "Account unavailable during MFA", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.UserUnavailable);
        }

        session.Status = MfaSessionStatus.Approved;
        device.LastActiveDate = DateTimeOffset.UtcNow;
        AddActivity(session.UserId, "MFA", "MFA approval", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AuthFlowResult<MfaActionResponse>.Success(new(session.Status, session.ExpiresAt));
    }

    public async Task<AuthFlowResult<MfaActionResponse>> DenyMfaAsync(
        MfaDenialRequest request,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(request.SessionToken, cancellationToken);
        if (session is null)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionNotFound);

        if (await ExpireIfNeededAsync(session, cancellationToken))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionExpired);

        if (session.Status != MfaSessionStatus.Pending)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidSessionState);

        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate => candidate.Id == session.DeviceId, cancellationToken);
        if (device is null || device.Status != DeviceStatus.Active || device.TrustStatus != DeviceTrustStatus.Trusted)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceUnavailable);

        if (string.IsNullOrWhiteSpace(device.SigningPublicKey))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceSigningKeyNotRegistered);

        if (!string.Equals(device.DeviceId, request.DeviceId, StringComparison.Ordinal)
            || !MfaApprovalSignature.Verify(device.SigningPublicKey, request.SessionToken, request.DeviceId, request.Signature))
        {
            AddActivity(session.UserId, "MFA Denial Rejected", "Invalid device-bound denial signature", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidApprovalSignature);
        }

        session.Status = MfaSessionStatus.Denied;
        AddActivity(session.UserId, "Security Alert: MFA Denied", "User denied a sign-in request", ActivityLogStatus.Denied);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AuthFlowResult<MfaActionResponse>.Success(new(session.Status, session.ExpiresAt));
    }

    public async Task<AuthFlowResult<MfaStatusResponse>> GetMfaStatusAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = HashSessionToken(sessionToken);
        var deadline = DateTimeOffset.UtcNow.Add(LongPollDuration);

        while (true)
        {
            var session = await dbContext.MfaSessions.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.SessionToken == tokenHash,
                cancellationToken);
            if (session is null)
                return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.SessionNotFound);

            if (session.Status == MfaSessionStatus.Pending && session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                var trackedSession = await FindSessionByIdAsync(session.Id, cancellationToken);
                if (trackedSession is not null)
                    await ExpireIfNeededAsync(trackedSession, cancellationToken);
                continue;
            }

            if (session.Status == MfaSessionStatus.Approved)
            {
                var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
                    candidate => candidate.Id == session.UserId,
                    cancellationToken);
                if (user is null || !user.IsActive)
                    return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.UserUnavailable);

                var deviceId = await dbContext.Devices.AsNoTracking()
                    .Where(candidate => candidate.Id == session.DeviceId && candidate.Status == DeviceStatus.Active)
                    .Select(candidate => candidate.DeviceId)
                    .SingleOrDefaultAsync(cancellationToken);
                if (deviceId is null)
                    return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.DeviceUnavailable);

                return AuthFlowResult<MfaStatusResponse>.Success(new(
                    MfaSessionStatus.Approved,
                    session.ExpiresAt,
                    jwtTokenService.CreateAccessToken(user),
                    "Bearer",
                    deviceId));
            }

            if (session.Status != MfaSessionStatus.Pending || DateTimeOffset.UtcNow >= deadline)
                return AuthFlowResult<MfaStatusResponse>.Success(new(session.Status, session.ExpiresAt, null, null, null));

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private Task<MfaSession?> FindSessionAsync(string sessionToken, CancellationToken cancellationToken) =>
        dbContext.MfaSessions.SingleOrDefaultAsync(
            candidate => candidate.SessionToken == HashSessionToken(sessionToken),
            cancellationToken);

    private Task<MfaSession?> FindSessionByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.MfaSessions.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

    private async Task<bool> ExpireIfNeededAsync(MfaSession session, CancellationToken cancellationToken)
    {
        if (session.Status != MfaSessionStatus.Pending || session.ExpiresAt > DateTimeOffset.UtcNow)
            return false;

        session.Status = MfaSessionStatus.Expired;
        AddActivity(session.UserId, "MFA", "MFA session expired", ActivityLogStatus.Denied);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void AddActivity(Guid? userId, string action, string resource, ActivityLogStatus status) =>
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Resource = resource,
            Status = status
        });

    private static string HashSessionToken(string sessionToken) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken)));
}
