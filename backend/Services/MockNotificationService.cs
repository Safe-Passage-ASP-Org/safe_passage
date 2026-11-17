using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using SafePassage.Api.Hubs;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class MockNotificationService(
    ILogger<MockNotificationService> logger,
    IHubContext<MfaHub> hubContext,
    IHttpClientFactory? httpClientFactory = null) : INotificationService
{
    private const string SimulatorToken = "ExponentPushToken[expo-go-dev-simulator]";
    private static readonly Uri ExpoPushUri = new("https://exp.host/--/api/v2/push/send");

    public Task SendMfaApprovalRequestAsync(
        User user,
        Device device,
        string sessionToken,
        CancellationToken cancellationToken) =>
        SendMfaApprovalRequestAsync(user, device, sessionToken, null, cancellationToken);

    public async Task SendMfaApprovalRequestAsync(
        User user,
        Device device,
        string sessionToken,
        MfaRequestContext? context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestedAt = DateTimeOffset.UtcNow;
        await hubContext.Clients.Group(MfaHub.DeviceGroupName(user.Id, device.DeviceId)).SendAsync(
            "MfaRequested",
            new
            {
                sessionToken,
                userId = user.Id,
                deviceId = device.DeviceId,
                deviceRecordId = device.Id,
                requestedAt,
                ipAddress = context?.IpAddress,
                operatingSystem = context?.OperatingSystem,
                location = context?.Location
            },
            cancellationToken);

        if (IsDeliverableExpoToken(device.PushToken) && httpClientFactory is not null)
        {
            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var payload = new
                {
                    to = device.PushToken,
                    title = "Safe Passage Sign-In Request",
                    body = $"Approve sign-in{(string.IsNullOrWhiteSpace(context?.OperatingSystem) ? "" : $" from {context.OperatingSystem}")}?",
                    data = new
                    {
                        requestId = sessionToken,
                        deviceId = device.DeviceId,
                        ip = context?.IpAddress,
                        os = context?.OperatingSystem,
                        location = context?.Location,
                        time = requestedAt.ToString("O")
                    },
                    sound = "default",
                    priority = "high"
                };

                using var response = await client.PostAsJsonAsync(ExpoPushUri, payload, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Expo push delivery returned HTTP {StatusCode} for device {DeviceRecordId}.",
                        (int)response.StatusCode,
                        device.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Expo push delivery failed for device {DeviceRecordId}; SignalR delivery still sent.", device.Id);
            }
        }
        else
        {
            logger.LogInformation(
                "SignalR MFA approval requested for user {UserId} on device {DeviceRecordId}. Session token is not logged.",
                user.Id,
                device.Id);
        }
    }

    private static bool IsDeliverableExpoToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && !string.Equals(token.Trim(), SimulatorToken, StringComparison.Ordinal)
        && (token.StartsWith("ExponentPushToken[", StringComparison.Ordinal)
            || token.StartsWith("ExpoPushToken[", StringComparison.Ordinal));
}
