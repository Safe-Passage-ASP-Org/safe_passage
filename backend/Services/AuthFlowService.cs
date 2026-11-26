using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
    ILogger<AuthFlowService> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    IMemoryCache? cache = null,
    IActivationEmailSender? emailSender = null) : IAuthFlowService
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
        var identifier = request.Username.Trim();
        var normalizedIdentifier = identifier.ToUpperInvariant();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Username.ToUpper() == normalizedIdentifier
                || candidate.Email.ToUpper() == normalizedIdentifier,
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

        if (IsAdministrator(user.Role))
        {
            AddActivity(user.Id, "Login", "Administrator directed to email OTP sign-in", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.AdminEmailLoginRequired);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        var policy = await securityPolicyEvaluator.EvaluateAsync(cancellationToken);
        var loginTimestampNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request.DeviceSignature is not null
            && request.DeviceSignatureTimestamp is long signatureTimestamp
            && policy.IsValid
            && signatureTimestamp >= loginTimestampNow - 60_000
            && signatureTimestamp <= loginTimestampNow + 60_000
            && !string.IsNullOrWhiteSpace(request.DeviceId))
        {
            var signingDevice = await dbContext.Devices.FirstOrDefaultAsync(candidate =>
                candidate.UserId == user.Id
                && candidate.DeviceId == request.DeviceId
                && candidate.Status == DeviceStatus.Active
                && candidate.TrustStatus == DeviceTrustStatus.Trusted,
                cancellationToken);

            if (signingDevice is not null
                && MfaApprovalSignature.VerifyDeviceLogin(
                    signingDevice.SigningPublicKey,
                    signingDevice.DeviceId,
                    signatureTimestamp,
                    request.DeviceSignature))
            {
                var posture = DevicePostureEvaluator.Evaluate(signingDevice, policy);
                if (!posture.IsCompliant)
                {
                    AddActivity(user.Id, "Login", "Device posture non-compliant for key-based login", ActivityLogStatus.Denied, signingDevice.DeviceId, posture.FailureReason);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    signingDevice.LastActiveDate = DateTimeOffset.UtcNow;
                    AddActivity(user.Id, "Login", "Trusted mobile device proved possession of its signing key", ActivityLogStatus.Success, signingDevice.DeviceId);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return AuthFlowResult<LoginResponse>.Success(new(
                        false,
                        null,
                        signingDevice.DeviceId,
                        null,
                        null,
                        jwtTokenService.CreateAccessToken(user, signingDevice.DeviceId),
                        "Bearer",
                        "Credentials accepted; this trusted device authenticated with its registered signing key."));
                }
            }
        }

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

            if (trustedDevice is not null && DevicePostureEvaluator.Evaluate(trustedDevice, policy).IsCompliant)
            {
                trustedDevice.LastActiveDate = DateTimeOffset.UtcNow;
                AddActivity(user.Id, "Login", "Trusted-device MFA bypass allowed by policy", ActivityLogStatus.Success, trustedDevice.DeviceId);
                await dbContext.SaveChangesAsync(cancellationToken);
                return AuthFlowResult<LoginResponse>.Success(new(
                    false,
                    null,
                    trustedDevice.DeviceId,
                    null,
                    null,
                    jwtTokenService.CreateAccessToken(user, trustedDevice.DeviceId),
                    "Bearer",
                    "Credentials accepted; MFA was skipped by trusted-device policy."));
            }
        }

        var mfaDevices = await dbContext.Devices
            .Where(candidate => candidate.UserId == user.Id
                && candidate.Status == DeviceStatus.Active
                && candidate.TrustStatus == DeviceTrustStatus.Trusted
                && (candidate.DeviceType.ToLower() == "mobile"
                    || candidate.DeviceType.ToLower() == "phone"
                    || candidate.OS.ToLower().StartsWith("android")
                    || candidate.OS.ToLower().StartsWith("ios")))
            .ToListAsync(cancellationToken);
        var device = mfaDevices
            .OrderByDescending(candidate => candidate.LastActiveDate ?? candidate.RegistrationDate)
            .FirstOrDefault(candidate =>
                !string.Equals(candidate.DeviceId, request.DeviceId, StringComparison.Ordinal));

        if (device is null)
        {
            if (mfaDevices.Any(candidate =>
                string.Equals(candidate.DeviceId, request.DeviceId, StringComparison.Ordinal)))
                return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.SelfApprovalNotAllowed);

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

        var mfaContext = BuildMfaRequestContext();
        CachePendingMfaChallenge(session.Id, sessionToken, mfaContext, expiresAt);
        try
        {
            await notificationService.SendMfaApprovalRequestAsync(user, device, sessionToken, mfaContext, cancellationToken);
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

    public async Task<AuthFlowResult<LoginResponse>> LoginWithExternalProviderAsync(
        User user,
        string provider,
        CancellationToken cancellationToken)
    {
        if (!user.IsActive || !user.IsEmailVerified)
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.UserUnavailable);

        if (IsAdministrator(user.Role))
        {
            AddActivity(user.Id, "Login", $"Administrator {provider} sign-in directed to email verification", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.AdminEmailLoginRequired);
        }

        var policy = await securityPolicyEvaluator.EvaluateAsync(cancellationToken);
        if (!policy.IsValid)
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        if (!policy.RequiresStepUp)
        {
            AddActivity(user.Id, "Login", $"{provider} sign-in", ActivityLogStatus.Success);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Success(new(
                false, null, null, null, null, jwtTokenService.CreateAccessToken(user), "Bearer",
                $"Signed in with {provider}."));
        }

        var devices = await dbContext.Devices
            .Where(device => device.UserId == user.Id
                && device.Status == DeviceStatus.Active
                && device.TrustStatus == DeviceTrustStatus.Trusted
                && (device.DeviceType.ToLower() == "mobile" || device.DeviceType.ToLower() == "phone"
                    || device.OS.ToLower().StartsWith("android") || device.OS.ToLower().StartsWith("ios")))
            .ToListAsync(cancellationToken);
        var device = devices.FirstOrDefault();
        if (device is null)
        {
            AddActivity(user.Id, "Login", $"{provider} sign-in denied: no MFA device", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NoActiveMobileDevice);
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(MfaLifetime);
        var sessionToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var session = new MfaSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, DeviceId = device.Id,
            SessionToken = HashSessionToken(sessionToken), Status = MfaSessionStatus.Pending,
            Purpose = "AdminEmailLogin", CreatedAt = now, ExpiresAt = expiresAt
        };
        dbContext.MfaSessions.Add(session);
        AddActivity(user.Id, "Login", $"{provider} accepted; MFA pending", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        var mfaContext = BuildMfaRequestContext();
        CachePendingMfaChallenge(session.Id, sessionToken, mfaContext, expiresAt);
        try
        {
            await notificationService.SendMfaApprovalRequestAsync(user, device, sessionToken, mfaContext, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "MFA notification failed after {Provider} sign-in for user {UserId}.", provider, user.Id);
            session.Status = MfaSessionStatus.Denied;
            AddActivity(user.Id, "MFA Push", "MFA notification delivery failed", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NotificationFailed);
        }

        return AuthFlowResult<LoginResponse>.Success(new(
            true, sessionToken, device.DeviceId, MfaSessionStatus.Pending, expiresAt, null, null,
            $"Approve the {provider} sign-in on your registered device."));
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

        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == session.UserId, cancellationToken);
        if (user is null || !user.IsActive || IsAdministrator(user.Role))
        {
            session.Status = MfaSessionStatus.Denied;
            AddActivity(session.UserId, "MFA", "Administrator mobile approval is disabled", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.UserUnavailable);
        }

        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate => candidate.Id == session.DeviceId, cancellationToken);
        if (device is null || device.Status != DeviceStatus.Active || device.TrustStatus != DeviceTrustStatus.Trusted)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceUnavailable);

        if (string.IsNullOrWhiteSpace(device.SigningPublicKey))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceSigningKeyNotRegistered);

        if (!string.Equals(device.DeviceId, request.DeviceId, StringComparison.Ordinal)
            || !MfaApprovalSignature.Verify(device.SigningPublicKey, request.SessionToken, request.DeviceId, request.Signature))
        {
            AddActivity(session.UserId, "MFA Approval Rejected", "Invalid device-bound approval signature", ActivityLogStatus.Denied, device.DeviceId);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidApprovalSignature);
        }

        var policy = await securityPolicyEvaluator.EvaluateAsync(cancellationToken);
        var posture = DevicePostureEvaluator.Evaluate(device, policy);
        if (!posture.IsCompliant)
        {
            AddActivity(session.UserId, "MFA Approval Rejected", "Approving device failed security posture check", ActivityLogStatus.Denied, device.DeviceId, posture.FailureReason);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceUnavailable);
        }

        session.Status = MfaSessionStatus.Approved;
        if (string.Equals(session.Purpose, "BrowserAppAccess", StringComparison.Ordinal))
            session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
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

        var sessionUser = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == session.UserId, cancellationToken);
        if (sessionUser is null || !sessionUser.IsActive || IsAdministrator(sessionUser.Role))
        {
            session.Status = MfaSessionStatus.Denied;
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.UserUnavailable);
        }

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

    public async Task<AuthFlowResult<MfaActionResponse>> ResendMfaApprovalAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(sessionToken, cancellationToken);
        if (session is null)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionNotFound);

        if (await ExpireIfNeededAsync(session, cancellationToken))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionExpired);

        if (session.Status != MfaSessionStatus.Pending)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidSessionState);

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == session.UserId && candidate.IsActive,
            cancellationToken);
        if (user is null || IsAdministrator(user.Role))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.UserUnavailable);

        var device = await dbContext.Devices.SingleOrDefaultAsync(
            candidate => candidate.Id == session.DeviceId
                && candidate.Status == DeviceStatus.Active
                && candidate.TrustStatus == DeviceTrustStatus.Trusted,
            cancellationToken);
        if (device is null)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceUnavailable);
        if (string.IsNullOrWhiteSpace(device.SigningPublicKey))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.DeviceSigningKeyNotRegistered);

        try
        {
            await notificationService.SendMfaApprovalRequestAsync(
                user,
                device,
                sessionToken,
                BuildMfaRequestContext(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "MFA approval request resend failed for user {UserId}.", user.Id);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.NotificationFailed);
        }

        AddActivity(user.Id, "MFA Approval Request Resent", device.DeviceName, ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AuthFlowResult<MfaActionResponse>.Success(new(session.Status, session.ExpiresAt));
    }

    public async Task<AuthFlowResult<LoginResponse>> RequestBrowserAccessApprovalAsync(
        Guid userId,
        string? approvalDeviceId,
        string origin,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == userId && candidate.IsActive,
            cancellationToken);
        if (user is null || IsAdministrator(user.Role))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.UserUnavailable);

        if (string.IsNullOrWhiteSpace(approvalDeviceId))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NoActiveMobileDevice);

        var device = await dbContext.Devices.SingleOrDefaultAsync(candidate =>
            candidate.UserId == userId
            && candidate.DeviceId == approvalDeviceId
            && candidate.Status == DeviceStatus.Active
            && candidate.TrustStatus == DeviceTrustStatus.Trusted,
            cancellationToken);
        if (device is null)
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.DeviceUnavailable);

        if (string.IsNullOrWhiteSpace(device.SigningPublicKey))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.DeviceSigningKeyNotRegistered);

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
            Purpose = "BrowserAppAccess",
            CreatedAt = now,
            ExpiresAt = expiresAt
        };
        dbContext.MfaSessions.Add(session);
        AddActivity(user.Id, "Browser App Access Request", origin, ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await notificationService.SendMfaApprovalRequestAsync(
                user,
                device,
                sessionToken,
                BuildMfaRequestContext(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Browser app MFA notification failed for user {UserId}.", user.Id);
            session.Status = MfaSessionStatus.Denied;
            AddActivity(user.Id, "Browser App Access Notification", origin, ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NotificationFailed);
        }

        return AuthFlowResult<LoginResponse>.Success(new(
            true,
            sessionToken,
            device.DeviceId,
            session.Status,
            expiresAt,
            null,
            null,
            $"Approve access to {origin} on your registered Safe Passage device."));
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

            var sessionUser = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == session.UserId,
                cancellationToken);
            if (sessionUser is null || !sessionUser.IsActive)
                return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.UserUnavailable);
            if (IsAdministrator(sessionUser.Role))
            {
                if (session.Status == MfaSessionStatus.Pending)
                {
                    var trackedSession = await FindSessionByIdAsync(session.Id, cancellationToken);
                    if (trackedSession is not null)
                    {
                        trackedSession.Status = MfaSessionStatus.Denied;
                        AddActivity(session.UserId, "MFA", "Administrator mobile approval is disabled", ActivityLogStatus.Denied);
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                }
                return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.UserUnavailable);
            }

            if (session.Status == MfaSessionStatus.Pending && session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                var trackedSession = await FindSessionByIdAsync(session.Id, cancellationToken);
                if (trackedSession is not null)
                    await ExpireIfNeededAsync(trackedSession, cancellationToken);
                continue;
            }

            if (session.Status == MfaSessionStatus.Approved
                && string.Equals(session.Purpose, "BrowserAppAccess", StringComparison.Ordinal)
                && session.ExpiresAt <= DateTimeOffset.UtcNow)
                return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.SessionExpired);

            if (session.Status == MfaSessionStatus.Approved)
            {
                var deviceId = await dbContext.Devices.AsNoTracking()
                    .Where(candidate => candidate.Id == session.DeviceId && candidate.Status == DeviceStatus.Active)
                    .Select(candidate => candidate.DeviceId)
                    .SingleOrDefaultAsync(cancellationToken);
                if (deviceId is null)
                    return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.DeviceUnavailable);

                return AuthFlowResult<MfaStatusResponse>.Success(new(
                    MfaSessionStatus.Approved,
                    session.ExpiresAt,
                    session.Purpose == "BrowserAppAccess"
                        ? jwtTokenService.CreateAccessToken(sessionUser, deviceId, "BrowserAppAccess", session.ExpiresAt)
                        : jwtTokenService.CreateAccessToken(sessionUser, deviceId),
                    "Bearer",
                    deviceId));
            }

            if (session.Status != MfaSessionStatus.Pending || DateTimeOffset.UtcNow >= deadline)
                return AuthFlowResult<MfaStatusResponse>.Success(new(session.Status, session.ExpiresAt, null, null, null));

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PendingMfaChallengeResponse>> GetPendingMfaAsync(
        Guid userId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        if (cache is null)
            return [];

        var now = DateTimeOffset.UtcNow;
        var sessions = await dbContext.MfaSessions
            .AsNoTracking()
            .Include(session => session.Device)
            .Where(session => session.UserId == userId
                && session.Status == MfaSessionStatus.Pending
                && session.ExpiresAt > now
                && session.User.Role.ToUpper() != "ADMIN"
                && session.User.Role.ToUpper() != "ADMINISTRATOR"
                && session.User.Role.ToUpper() != "SUPER ADMIN"
                && session.Device.Status == DeviceStatus.Active
                && session.Device.TrustStatus == DeviceTrustStatus.Trusted)
            .ToListAsync(cancellationToken);

        var ordered = sessions
            .OrderByDescending(s => !string.IsNullOrWhiteSpace(deviceId) && string.Equals(s.Device.DeviceId, deviceId, StringComparison.Ordinal))
            .ThenByDescending(s => s.CreatedAt)
            .ToList();

        var results = new List<PendingMfaChallengeResponse>();
        foreach (var session in ordered)
        {
            if (cache.TryGetValue(PendingMfaCacheKey(session.Id), out CachedPendingMfa? cached) && cached is not null)
            {
                results.Add(new PendingMfaChallengeResponse(
                    cached.SessionToken,
                    session.Device.DeviceId,
                    session.Purpose,
                    session.CreatedAt,
                    session.ExpiresAt,
                    cached.Context.IpAddress,
                    cached.Context.OperatingSystem,
                    cached.Context.Location));
            }
        }

        return results;
    }

    private sealed record CachedPendingMfa(string SessionToken, MfaRequestContext Context);

    private static string PendingMfaCacheKey(Guid sessionId) => $"pending-mfa-raw:{sessionId:N}";

    private void CachePendingMfaChallenge(Guid sessionId, string sessionToken, MfaRequestContext context, DateTimeOffset expiresAt)
    {
        if (cache is null)
            return;
        cache.Set(PendingMfaCacheKey(sessionId), new CachedPendingMfa(sessionToken, context), expiresAt);
    }

    private sealed record CachedEmailOtp(byte[] CodeHash, int Attempts, DateTimeOffset ExpiresAt);

    public async Task<AuthFlowResult<MfaActionResponse>> SendMfaEmailOtpAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        if (cache is null || emailSender is null)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.NotificationFailed);

        var session = await FindSessionAsync(sessionToken, cancellationToken);
        if (session is null)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionNotFound);

        if (await ExpireIfNeededAsync(session, cancellationToken))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.SessionExpired);

        if (session.Status != MfaSessionStatus.Pending)
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.InvalidSessionState);

        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == session.UserId && candidate.IsActive,
            cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Email) || IsAdministrator(user.Role))
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.UserUnavailable);

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        cache.Set(
            $"mfa-email-otp:{session.Id:N}",
            new CachedEmailOtp(hash, 0, session.ExpiresAt),
            session.ExpiresAt);

        try
        {
            await emailSender.SendSignInOtpAsync(
                user.Email,
                user.FullName ?? user.Username,
                code,
                5,
                isAdminLogin: false,
                cancellationToken);
        }
        catch (Exception ex) when (ex is EmailDeliveryException or InvalidOperationException)
        {
            logger.LogError(ex, "Failed to send MFA Email OTP for user {UserId}.", user.Id);
            return AuthFlowResult<MfaActionResponse>.Failed(AuthFlowFailure.NotificationFailed);
        }

        AddActivity(user.Id, "MFA Email OTP", "Sent 6-digit sign-in OTP to verified email", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AuthFlowResult<MfaActionResponse>.Success(new(session.Status, session.ExpiresAt));
    }

    public async Task<AuthFlowResult<MfaStatusResponse>> VerifyMfaEmailOtpAsync(
        string sessionToken,
        string code,
        CancellationToken cancellationToken)
    {
        if (cache is null)
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.InvalidApprovalSignature);

        var session = await FindSessionAsync(sessionToken, cancellationToken);
        if (session is null)
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.SessionNotFound);

        if (await ExpireIfNeededAsync(session, cancellationToken))
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.SessionExpired);

        if (session.Status != MfaSessionStatus.Pending)
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.InvalidSessionState);

        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == session.UserId && candidate.IsActive,
            cancellationToken);
        if (user is null || IsAdministrator(user.Role))
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.UserUnavailable);

        var otpKey = $"mfa-email-otp:{session.Id:N}";
        if (!cache.TryGetValue(otpKey, out CachedEmailOtp? cached) || cached is null || cached.ExpiresAt <= DateTimeOffset.UtcNow)
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.InvalidApprovalSignature);

        var normalizedCode = (code ?? string.Empty).Trim();
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, cached.CodeHash))
        {
            var nextAttempts = cached.Attempts + 1;
            if (nextAttempts >= 5)
                cache.Remove(otpKey);
            else
                cache.Set(otpKey, cached with { Attempts = nextAttempts }, cached.ExpiresAt);

            AddActivity(session.UserId, "MFA Email OTP Rejected", "Invalid 6-digit OTP code", ActivityLogStatus.Denied);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AuthFlowResult<MfaStatusResponse>.Failed(AuthFlowFailure.InvalidApprovalSignature);
        }

        cache.Remove(otpKey);
        session.Status = MfaSessionStatus.Approved;
        AddActivity(session.UserId, "MFA", "Email OTP verification", ActivityLogStatus.Success);
        await dbContext.SaveChangesAsync(cancellationToken);

        var deviceId = await dbContext.Devices.AsNoTracking()
            .Where(candidate => candidate.Id == session.DeviceId && candidate.Status == DeviceStatus.Active)
            .Select(candidate => candidate.DeviceId)
            .SingleOrDefaultAsync(cancellationToken);

        return AuthFlowResult<MfaStatusResponse>.Success(new(
            MfaSessionStatus.Approved,
            session.ExpiresAt,
            jwtTokenService.CreateAccessToken(user, deviceId),
            "Bearer",
            deviceId));
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

    private void AddActivity(
        Guid? userId,
        string action,
        string resource,
        ActivityLogStatus status,
        string? deviceId = null,
        string? reason = null) =>
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ActorUserId = userId,
            TargetUserId = userId,
            DeviceId = deviceId,
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Resource = resource,
            Reason = reason,
            IpAddress = BuildMfaRequestContext().IpAddress,
            Status = status
        });

    private static bool IsAdministrator(string role) =>
        string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "Super Admin", StringComparison.OrdinalIgnoreCase);

    private MfaRequestContext BuildMfaRequestContext()
    {
        var http = httpContextAccessor?.HttpContext;
        if (http is null)
            return new MfaRequestContext();

        var forwardedFor = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ipAddress = !string.IsNullOrWhiteSpace(forwardedFor)
            ? forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            : http.Connection.RemoteIpAddress?.ToString();

        var userAgent = http.Request.Headers.UserAgent.ToString();
        var operatingSystem = ParseOperatingSystem(userAgent);
        var location = http.Request.Headers["CF-IPCountry"].FirstOrDefault()
            ?? http.Request.Headers["X-Vercel-IP-Country"].FirstOrDefault();

        return new MfaRequestContext(ipAddress, operatingSystem, location);
    }

    private static string? ParseOperatingSystem(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;
        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "macOS";
        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        return "Web Browser";
    }

    private static string HashSessionToken(string sessionToken) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken)));
}
