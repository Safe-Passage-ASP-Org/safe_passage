using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SafePassage.Api.Services;

public sealed class PayPalHttpClient(
    HttpClient httpClient,
    IOptions<PayPalOptions> optionsAccessor) : IPayPalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public async Task<PayPalCreateSubscriptionResult> CreateSubscriptionAsync(
        string payPalPlanId,
        string customId,
        string returnUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("v1/billing/subscriptions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new
        {
            plan_id = payPalPlanId,
            custom_id = customId,
            application_context = new
            {
                brand_name = "Safe Passage",
                locale = "en-US",
                shipping_preference = "NO_SHIPPING",
                user_action = "SUBSCRIBE_NOW",
                return_url = returnUrl,
                cancel_url = cancelUrl
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal subscription creation failed ({(int)response.StatusCode}): {body}");

        var payload = JsonSerializer.Deserialize<CreateSubscriptionApiResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("PayPal returned an empty subscription response.");

        var approvalUrl = payload.Links?
            .FirstOrDefault(link => string.Equals(link.Rel, "approve", StringComparison.OrdinalIgnoreCase))
            ?.Href;

        if (string.IsNullOrWhiteSpace(payload.Id) || string.IsNullOrWhiteSpace(approvalUrl))
            throw new InvalidOperationException("PayPal did not return a subscription ID and approval link.");

        return new PayPalCreateSubscriptionResult(
            payload.Id,
            payload.Status ?? "APPROVAL_PENDING",
            approvalUrl);
    }

    public async Task<PayPalSubscriptionDetails> GetSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri($"v1/billing/subscriptions/{Uri.EscapeDataString(subscriptionId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"PayPal subscription '{subscriptionId}' was not found.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal subscription lookup failed ({(int)response.StatusCode}): {body}");

        var payload = JsonSerializer.Deserialize<GetSubscriptionApiResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("PayPal returned an empty subscription details payload.");

        return new PayPalSubscriptionDetails(
            payload.Id ?? subscriptionId,
            payload.Status ?? "UNKNOWN",
            payload.PlanId ?? string.Empty,
            payload.CustomId,
            payload.StartTime,
            payload.BillingInfo?.NextBillingTime,
            payload.StatusUpdateTime);
    }

    public async Task CancelSubscriptionAsync(
        string subscriptionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri($"v1/billing/subscriptions/{Uri.EscapeDataString(subscriptionId)}/cancel"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new { reason });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"PayPal subscription cancellation failed ({(int)response.StatusCode}): {body}");
    }

    public async Task<bool> VerifyWebhookSignatureAsync(
        PayPalWebhookHeaders headers,
        JsonElement rawWebhookEvent,
        CancellationToken cancellationToken = default)
    {
        var options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(options.WebhookId))
            throw new InvalidOperationException("PayPal WebhookId is not configured.");

        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri("v1/notifications/verify-webhook-signature"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new
        {
            auth_algo = headers.AuthAlgo,
            cert_url = headers.CertUrl,
            transmission_id = headers.TransmissionId,
            transmission_sig = headers.TransmissionSig,
            transmission_time = headers.TransmissionTime,
            webhook_id = options.WebhookId,
            webhook_event = rawWebhookEvent
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return false;

        var payload = await response.Content.ReadFromJsonAsync<VerifyWebhookResponse>(JsonOptions, cancellationToken);
        return string.Equals(payload?.VerificationStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _cachedAccessToken;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _cachedAccessToken;

            var options = optionsAccessor.Value;
            if (!options.HasCredentials)
                throw new InvalidOperationException("PayPal Sandbox credentials (PayPal:ClientId / PayPal:ClientSecret) are not configured.");

            var basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}"));
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("v1/oauth2/token"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"PayPal OAuth token request failed ({(int)response.StatusCode}): {body}");

            var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(body, JsonOptions)
                ?? throw new InvalidOperationException("PayPal returned an empty OAuth token response.");

            if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                throw new InvalidOperationException("PayPal OAuth response did not contain an access_token.");

            var bufferSeconds = Math.Max(60, tokenResponse.ExpiresIn / 10);
            _cachedAccessToken = tokenResponse.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, tokenResponse.ExpiresIn - bufferSeconds));
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = optionsAccessor.Value.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl, UriKind.Absolute), relativePath.TrimStart('/'));
    }

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record CreateSubscriptionApiResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("links")] List<PayPalLink>? Links);

    private sealed record PayPalLink(
        [property: JsonPropertyName("href")] string? Href,
        [property: JsonPropertyName("rel")] string? Rel);

    private sealed record GetSubscriptionApiResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("plan_id")] string? PlanId,
        [property: JsonPropertyName("custom_id")] string? CustomId,
        [property: JsonPropertyName("start_time")] DateTimeOffset? StartTime,
        [property: JsonPropertyName("status_update_time")] DateTimeOffset? StatusUpdateTime,
        [property: JsonPropertyName("billing_info")] PayPalBillingInfo? BillingInfo);

    private sealed record PayPalBillingInfo(
        [property: JsonPropertyName("next_billing_time")] DateTimeOffset? NextBillingTime);

    private sealed record VerifyWebhookResponse(
        [property: JsonPropertyName("verification_status")] string? VerificationStatus);
}
