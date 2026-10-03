using Microsoft.AspNetCore.SignalR;
using SafePassage.Api.Hubs;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class MockNotificationService(
    ILogger<MockNotificationService> logger,
    IHubContext<MfaHub> hubContext) : INotificationService
{
    public async Task SendMfaApprovalRequestAsync(
        User user,
        Device device,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await hubContext.Clients.All.SendAsync(
            "MfaRequested",
            new
            {
                sessionToken,
                userId = user.Id,
                deviceId = device.DeviceId,
                deviceRecordId = device.Id,
                requestedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);

        logger.LogInformation(
            "MOCK PUSH: MFA approval requested for user {UserId} on device {DeviceRecordId}. Session token is not logged.",
            user.Id,
            device.Id);
    }
}