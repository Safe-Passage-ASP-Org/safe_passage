namespace SafePassage.Web.Services;

public sealed class UserNotificationService(AdminApiClient api)
{
    public IReadOnlyList<UserNotification> Items { get; private set; } = [];
    public string? Error { get; private set; }
    public int UnreadCount => Items.Count(item => item.ReadAt is null);
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Items = await api.GetMyNotificationsAsync(cancellationToken);
            Error = null;
        }
        catch (Exception exception)
        {
            Items = [];
            Error = exception.Message;
        }

        Changed?.Invoke();
    }

    public async Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await api.MarkNotificationReadAsync(id, cancellationToken);
        var item = Items.FirstOrDefault(notification => notification.Id == id);
        if (item is not null && item.ReadAt is null)
            Items = Items.Select(notification => notification.Id == id
                ? notification with { ReadAt = DateTimeOffset.UtcNow }
                : notification).ToList();
        Changed?.Invoke();
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        await api.MarkAllNotificationsReadAsync(cancellationToken);
        var readAt = DateTimeOffset.UtcNow;
        Items = Items.Select(notification => notification with
        {
            ReadAt = notification.ReadAt ?? readAt
        }).ToList();
        Changed?.Invoke();
    }
}

public sealed record UserNotification(
    Guid Id,
    string Category,
    string Title,
    string Message,
    string? Href,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
