using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class MockNotificationService(ILogger<MockNotificationService> logger) : INotificationService
{
    public Task SendMfaApprovalRequestAsync(
        User user,
        Device device,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(
            "MOCK PUSH: MFA approval requested for user {UserId} on device {DeviceRecordId}. Session token is not logged.",
            user.Id,
            device.Id);
        return Task.CompletedTask;
    }
}