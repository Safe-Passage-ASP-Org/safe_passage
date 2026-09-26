using System.ComponentModel.DataAnnotations;

namespace SafePassage.Api.Contracts.Auth;

/// <summary>Credentials submitted to begin an MFA-protected sign-in.</summary>
public sealed record LoginRequest
{
    /// <summary>The user's account name.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Username { get; init; }

    /// <summary>The account password, verified against its stored password hash.</summary>
    [Required, StringLength(1024, MinimumLength = 1)]
    public required string Password { get; init; }

    /// <summary>The requesting device's registered hardware fingerprint, when available.</summary>
    [StringLength(256)]
    public string? DeviceId { get; init; }
}

/// <summary>Approval data returned by the mobile application.</summary>
public sealed record MfaApprovalRequest
{
    /// <summary>The one-time MFA session token.</summary>
    [Required, StringLength(512, MinimumLength = 32)]
    public required string SessionToken { get; init; }

    /// <summary>Temporary stand-in for cryptographic signature validation.</summary>
    public bool IsSignatureValid { get; init; }
}

/// <summary>Denial data returned by the mobile application.</summary>
public sealed record MfaDenialRequest
{
    /// <summary>The one-time MFA session token.</summary>
    [Required, StringLength(512, MinimumLength = 32)]
    public required string SessionToken { get; init; }
}