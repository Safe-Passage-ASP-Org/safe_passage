namespace SafePassage.Api.Models;

public enum ActivityLogStatus
{
    Success,
    Denied
}

public sealed class ActivityLog
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UserId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? DeviceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
    public ActivityLogStatus Status { get; set; }

    public User? User { get; set; }
}