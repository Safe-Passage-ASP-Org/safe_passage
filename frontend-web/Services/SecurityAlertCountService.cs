namespace SafePassage.Web.Services;

public sealed class SecurityAlertCountService(AdminApiClient api)
{
    public int? Count { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var devices = await api.GetDevicesAsync(cancellationToken);
            SetCount(devices.Count(device => device.Status == "PENDING_APPROVAL"));
        }
        catch
        {
            SetCount(null);
        }
    }

    public void SetCount(int? count)
    {
        Count = count;
        Changed?.Invoke();
    }
}
