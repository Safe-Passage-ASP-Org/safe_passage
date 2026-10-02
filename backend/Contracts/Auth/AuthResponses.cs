using SafePassage.Api.Models;

namespace SafePassage.Api.Contracts.Auth;

/// <summary>Represents either an MFA challenge or a trusted-device sign-in.</summary>
public sealed record LoginResponse(
    bool MfaRequired,
    string? SessionToken,
    string? ApprovalDeviceId,
    MfaSessionStatus? MfaStatus,
    DateTimeOffset? ExpiresAt,
    string? AccessToken,
    string? TokenType,
    string Message);

/// <summary>Represents the result of an MFA approval or denial action.</summary>
public sealed record MfaActionResponse(
    MfaSessionStatus Status,
    DateTimeOffset ExpiresAt);

/// <summary>Current MFA state and, after approval, the issued access token.</summary>
public sealed record MfaStatusResponse(
    MfaSessionStatus Status,
    DateTimeOffset ExpiresAt,
    string? AccessToken,
    string? TokenType,
    string? DeviceId);