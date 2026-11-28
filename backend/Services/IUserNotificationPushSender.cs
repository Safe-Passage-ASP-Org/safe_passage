namespace SafePassage.Api.Services;

public interface IUserNotificationPushSender
{
    Task SendAsync(
        IEnumerable<string> tokens,
        string title,
        string message,
        Guid notificationId,
        string? href,
        CancellationToken cancellationToken);
}
