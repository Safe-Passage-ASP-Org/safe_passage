using Microsoft.AspNetCore.SignalR;

namespace SafePassage.Api.Hubs;

public sealed class MfaHub : Hub
{
    public Task JoinDeviceGroup(string deviceId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");

    public Task LeaveDeviceGroup(string deviceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}");

    public Task JoinSession(string sessionToken) =>
        Groups.AddToGroupAsync(Context.ConnectionId, sessionToken);

    public Task LeaveSession(string sessionToken) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionToken);
}
