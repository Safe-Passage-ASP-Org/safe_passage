using System.ComponentModel.DataAnnotations;

namespace SafePassage.Api.Contracts.Auth;

/// <summary>Credentials submitted to begin an MFA-protected sign-in.</summary>
public sealed record LoginRequest
{
    /// <summary>The user's email address or account name.</summary>
    [Required, StringLength(320, MinimumLength = 1)]
    public required string Username { get; init; }

    /// <summary>The account password, verified against its stored password hash.</summary>
    [Required, StringLength(1024, MinimumLength = 1)]
    public required string Password { get; init; }

    /// <summary>The requesting device's registered hardware fingerprint, when available.</summary>
    [StringLength(256)]
    public string? DeviceId { get; init; }

    /// <summary>A fresh device-bound signature proving possession of the registered mobile key.</summary>
    [StringLength(128, MinimumLength = 80)]
    public string? DeviceSignature { get; init; }

    /// <summary>The Unix timestamp in milliseconds covered by the device signature.</summary>
    public long? DeviceSignatureTimestamp { get; init; }
}

/// <summary>Details submitted to create a regular user account.</summary>
public sealed record RegisterRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public required string FullName { get; init; }

    [Required, StringLength(32, MinimumLength = 3)]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._-]*$",
        ErrorMessage = "Username must start with a letter or number and contain only letters, numbers, periods, underscores, or hyphens.")]
    public required string Username { get; init; }

    [Required, EmailAddress, StringLength(320)]
    public required string Email { get; init; }

    [Required, Phone, StringLength(32, MinimumLength = 7)]
    public required string PhoneNumber { get; init; }

    [Required, StringLength(1024, MinimumLength = 12)]
    public required string Password { get; init; }
}

/// <summary>Approval data returned by the mobile application.</summary>
public sealed record MfaApprovalRequest
{
    /// <summary>The one-time MFA session token.</summary>
    [Required, StringLength(512, MinimumLength = 32)]
    public required string SessionToken { get; init; }

    /// <summary>The registered device approving this challenge.</summary>
    [Required, StringLength(256, MinimumLength = 1)]
    public required string DeviceId { get; init; }

    /// <summary>Base64-encoded P-256 ECDSA signature over the session and device ID.</summary>
    [Required, StringLength(128, MinimumLength = 80)]
    public required string Signature { get; init; }
}

/// <summary>Denial data returned by the mobile application.</summary>
public sealed record MfaDenialRequest
{
    /// <summary>The one-time MFA session token.</summary>
    [Required, StringLength(512, MinimumLength = 32)]
    public required string SessionToken { get; init; }

    /// <summary>The registered device making the decision.</summary>
    [Required, StringLength(256, MinimumLength = 1)]
    public required string DeviceId { get; init; }

    /// <summary>Device-bound P-256 signature over the session and device ID.</summary>
    [Required, StringLength(128, MinimumLength = 80)]
    public required string Signature { get; init; }
}
