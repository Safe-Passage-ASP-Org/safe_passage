namespace SafePassage.Api.Models;

public sealed class AdminEmailChangeToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string NewEmail { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string OtpHash { get; set; } = string.Empty;
    public int FailedAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
