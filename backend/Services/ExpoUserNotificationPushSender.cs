using System.Net.Http.Json;

namespace SafePassage.Api.Services;

public sealed class ExpoUserNotificationPushSender(
    IHttpClientFactory httpClientFactory,
    ILogger<ExpoUserNotificationPushSender> logger) : IUserNotificationPushSender
{
    private static readonly Uri ExpoPushUri = new("https://exp.host/--/api/v2/push/send");
    private const string SimulatorToken = "ExponentPushToken[expo-go-dev-simulator]";

    public async Task SendAsync(
        IEnumerable<string> tokens,
        string title,
        string message,
        Guid notificationId,
        string? href,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var token in tokens.Where(IsDeliverableToken).Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var response = await client.PostAsJsonAsync(ExpoPushUri, new
                {
                    to = token,
                    title,
                    body = message,
                    data = new { notificationId, href },
                    sound = "default",
                    priority = "high"
                }, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    logger.LogWarning("Expo notification delivery returned HTTP {StatusCode}.", (int)response.StatusCode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Expo notification delivery failed.");
            }
        }
    }

    private static bool IsDeliverableToken(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && !string.Equals(token.Trim(), SimulatorToken, StringComparison.Ordinal)
        && (token.StartsWith("ExponentPushToken[", StringComparison.Ordinal)
            || token.StartsWith("ExpoPushToken[", StringComparison.Ordinal));
}
