namespace SafePassage.Api.Models;

public enum MfaSessionStatus
{
    Pending,
    Approved,
    Denied,
    Expired
}

public sealed class MfaSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public MfaSessionStatus Status { get; set; } = MfaSessionStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    public User User { get; set; } = null!;
    public Device Device { get; set; } = null!;
}