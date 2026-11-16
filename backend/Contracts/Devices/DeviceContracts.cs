using System.ComponentModel.DataAnnotations;

namespace SafePassage.Api.Contracts.Devices;

/// <summary>Device telemetry submitted by an authenticated mobile application.</summary>
public sealed record DeviceRegistrationRequest
{
    /// <summary>Stable hardware fingerprint for this installation.</summary>
    [Required, StringLength(256, MinimumLength = 1)]
    public required string DeviceId { get; init; }

    /// <summary>Friendly device name reported by the operating system.</summary>
    [Required, StringLength(200, MinimumLength = 1)]
    public required string DeviceName { get; init; }

    /// <summary>Device category such as Mobile, Phone, Tablet, or Desktop.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string DeviceType { get; init; }

    /// <summary>Operating system and version reported by the device.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string OS { get; init; }

    /// <summary>Base64-encoded uncompressed P-256 signing public key for this installation.</summary>
    [Required, StringLength(128, MinimumLength = 88)]
    public required string SigningPublicKey { get; init; }

    /// <summary>Optional device security posture signals reported by supported mobile platforms.</summary>
    public DevicePostureRequest? Posture { get; init; }
}

/// <summary>Credentials plus device data used to submit a device while the user has not completed MFA.</summary>
public sealed record PendingDeviceRegistrationRequest
{
    [Required, StringLength(320, MinimumLength = 1)] public required string Username { get; init; }
    [Required, StringLength(1024, MinimumLength = 1)] public required string Password { get; init; }
    [Required, StringLength(256, MinimumLength = 1)] public required string DeviceId { get; init; }
    [Required, StringLength(200, MinimumLength = 1)] public required string DeviceName { get; init; }
    [Required, StringLength(100, MinimumLength = 1)] public required string DeviceType { get; init; }
    [Required, StringLength(100, MinimumLength = 1)] public required string OS { get; init; }
    [Required, StringLength(128, MinimumLength = 88)] public required string SigningPublicKey { get; init; }
    public DevicePostureRequest? Posture { get; init; }
}

public sealed record DevicePostureRequest(
    bool? ScreenLockEnabled = null,
    bool? IsRootedOrJailbroken = null,
    string? SignalSource = null);

public sealed record DevicePostureUpdateRequest
{
    [Required, StringLength(256, MinimumLength = 1)]
    public required string DeviceId { get; init; }

    public DevicePostureRequest? Posture { get; init; }
}

public sealed record DevicePostureResponse(
    bool? ScreenLockEnabled,
    bool? IsRootedOrJailbroken,
    DateTimeOffset? ReportedAt);

public sealed record DeviceSigningKeyRequest
{
    [Required, StringLength(256, MinimumLength = 1)]
    public required string DeviceId { get; init; }

    [Required, StringLength(128, MinimumLength = 88)]
    public required string SigningPublicKey { get; init; }
}

/// <summary>Device data returned to an authorized API client.</summary>
public sealed record DeviceResponse(
    Guid Id,
    string DeviceId,
    string DeviceName,
    string DeviceType,
    string OS,
    DateTimeOffset RegistrationDate,
    DateTimeOffset? LastActiveDate,
    string Status,
    string TrustStatus,
    Guid UserId,
    bool HasSigningKey = false,
    bool HasPendingSigningKey = false,
    DevicePostureResponse? Posture = null);
