namespace SafePassage.Api.Models;

public enum DeviceStatus
{
    PendingApproval,
    Active,
    Disabled
}

public enum DeviceTrustStatus
{
    Trusted,
    Untrusted
}

public sealed class Device
{
    public Guid Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string OS { get; set; } = string.Empty;
    public DateTimeOffset RegistrationDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastActiveDate { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.PendingApproval;
    public DeviceTrustStatus TrustStatus { get; set; } = DeviceTrustStatus.Untrusted;
    public string? SigningPublicKey { get; set; }
    public Guid UserId { get; set; }

    public User User { get; set; } = null!;
    public ICollection<MfaSession> MfaSessions { get; set; } = new List<MfaSession>();
}