namespace SafePassage.Api.Models;

public sealed class UserNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Category { get; set; } = "Announcement";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Href { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}
