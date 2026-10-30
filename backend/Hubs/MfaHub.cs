using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Hubs;

[Authorize]
public sealed class MfaHub(ApplicationDbContext dbContext) : Hub
{
    public async Task JoinDeviceGroup(string deviceId)
    {
        var userClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");
        if (!Guid.TryParse(userClaim, out var userId) || string.IsNullOrWhiteSpace(deviceId))
            throw new HubException("A valid signed-in user and device are required.");

        var canReceiveRequests = await dbContext.Devices.AsNoTracking().AnyAsync(device =>
            device.UserId == userId
            && device.DeviceId == deviceId
            && device.Status == DeviceStatus.Active
            && device.TrustStatus == DeviceTrustStatus.Trusted,
            Context.ConnectionAborted);
        if (!canReceiveRequests)
            throw new HubException("This device is not active and trusted for the signed-in user.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}", Context.ConnectionAborted);
    }

    public Task LeaveDeviceGroup(string deviceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}", Context.ConnectionAborted);
}
