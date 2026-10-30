using System.ComponentModel.DataAnnotations;

namespace SafePassage.Api.Contracts.Devices;

public sealed record DeviceBootstrapRequest
{
    [Required, StringLength(100, MinimumLength = 1)] public required string Username { get; init; }
    [Required, StringLength(1024, MinimumLength = 1)] public required string Password { get; init; }
    [Required, StringLength(256, MinimumLength = 1)] public required string DeviceId { get; init; }
    [Required, StringLength(200, MinimumLength = 1)] public required string DeviceName { get; init; }
    [Required, StringLength(100, MinimumLength = 1)] public required string DeviceType { get; init; }
    [Required, StringLength(100, MinimumLength = 1)] public required string OS { get; init; }
    [Required, StringLength(128, MinimumLength = 88)] public required string SigningPublicKey { get; init; }
}

public sealed record DeviceBootstrapResponse(string AccessToken, string DeviceId);
