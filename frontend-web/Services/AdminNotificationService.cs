namespace SafePassage.Web.Services;

public sealed class AdminNotificationService(AdminApiClient api)
{
    public IReadOnlyList<AdminNotification> Items { get; private set; } = [];
    public int? Count { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var devicesTask = api.GetDevicesAsync(cancellationToken);
            var requestsTask = api.GetAdminAccessRequestsAsync("Pending", cancellationToken);
            var websitesTask = api.GetPendingVaultSitesAsync(cancellationToken);
            await Task.WhenAll(devicesTask, requestsTask, websitesTask);

            var notifications = new List<AdminNotification>();
            notifications.AddRange(devicesTask.Result
                .Where(device => device.Status == "PENDING_APPROVAL")
                .Select(device => new AdminNotification(
                    $"device:{device.Id}",
                    "Device approval",
                    "Device awaiting approval",
                    $"{device.DeviceName} is waiting to be trusted.",
                    device.RegistrationDate,
                    "/devices")));
            notifications.AddRange(requestsTask.Result.Select(request => new AdminNotification(
                $"request:{request.Id}",
                "Application access",
                "Application access request",
                $"{request.Username} requested access to {request.ApplicationName}.",
                request.RequestedAt,
                "/access-requests")));
            notifications.AddRange(websitesTask.Result.Select(site => new AdminNotification(
                $"website:{site.Id}",
                "Website verification",
                "Website awaiting verification",
                $"{site.Name} was submitted by {site.Username} and needs review.",
                site.CreatedAt,
                "/applications#pending-websites")));

            Items = notifications.OrderByDescending(item => item.CreatedAt).ToList();
            Count = Items.Count;
            Error = null;
        }
        catch (Exception exception)
        {
            Items = [];
            Count = null;
            Error = exception.Message;
        }

        Changed?.Invoke();
    }
}

public sealed record AdminNotification(
    string Id,
    string Category,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    string Href);
