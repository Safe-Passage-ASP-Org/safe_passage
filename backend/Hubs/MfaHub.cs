using Microsoft.AspNetCore.SignalR;

namespace SafePassage.Api.Hubs;

public sealed class MfaHub : Hub
{
    public Task JoinSession(string sessionToken) =>
        Groups.AddToGroupAsync(Context.ConnectionId, sessionToken);

    public Task LeaveSession(string sessionToken) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionToken);
}
