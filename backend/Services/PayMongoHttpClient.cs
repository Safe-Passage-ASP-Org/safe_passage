using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SafePassage.Api.Services;

public sealed record PayMongoCreateCheckoutResult(string SessionId, string CheckoutUrl);

public sealed record PayMongoCheckoutSessionDetails(
    string SessionId,
    string Status,
    bool IsPaid,
    string? PlanCode,
    string? CustomUserId,
    DateTimeOffset? PaidAt);

public interface IPayMongoClient
{
    Task<PayMongoCreateCheckoutResult> CreateCheckoutSessionAsync(
        int amountCentavos,
        string currency,
        string planName,
        string description,
        string planCode,
        string userId,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);

    Task<PayMongoCheckoutSessionDetails> GetCheckoutSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    bool VerifyWebhookSignature(string signatureHeader, string rawBody, string webhookSecret);
}

public sealed class PayMongoHttpClient(
    HttpClient httpClient,
    IOptions<PayMongoOptions> optionsAccessor) : IPayMongoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PayMongoCreateCheckoutResult> CreateCheckoutSessionAsync(
        int amountCentavos,
        string currency,
        string planName,
        string description,
        string planCode,
        string userId,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        var options = optionsAccessor.Value;
        using var request = CreateAuthorizedRequest(HttpMethod.Post, options, "checkout_sessions");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            data = new
            {
                attributes = new
                {
                    billing = (object?)null,
                    send_email_receipt = false,
                    show_description = true,
                    show_line_items = true,
                    description,
                    line_items = new[]
                    {
                        new
                        {
                            currency = string.IsNullOrWhiteSpace(currency) ? "PHP" : currency.ToUpperInvariant(),
                            amount = amountCentavos,
                            description,
                            name = planName,
                            quantity = 1
                        }
                    },
                    payment_method_types = new[] { "card", "gcash", "paymaya", "grab_pay" },
                    success_url = successUrl,
                    cancel_url = cancelUrl,
                    reference_number = $"sp-{userId}-{planCode}",
                    metadata = new
                    {
                        user_id = userId,
                        plan_code = planCode
                    }
                }
            }
        }, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"PayMongo checkout creation failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var id = data.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("PayMongo response omitted checkout session ID.");
        var checkoutUrl = data.GetProperty("attributes").GetProperty("checkout_url").GetString()
            ?? throw new InvalidOperationException("PayMongo response omitted checkout_url.");

        return new PayMongoCreateCheckoutResult(id, checkoutUrl);
    }

    public async Task<PayMongoCheckoutSessionDetails> GetCheckoutSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var options = optionsAccessor.Value;
        using var request = CreateAuthorizedRequest(HttpMethod.Get, options, $"checkout_sessions/{Uri.EscapeDataString(sessionId)}");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"PayMongo checkout lookup failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var attrs = data.GetProperty("attributes");

        var status = attrs.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? "active" : "active";
        var isPaid = false;
        DateTimeOffset? paidAt = null;

        if (attrs.TryGetProperty("payment_intent", out var intentEl)
            && intentEl.ValueKind == JsonValueKind.Object
            && intentEl.TryGetProperty("attributes", out var intentAttrs)
            && intentAttrs.TryGetProperty("status", out var intentStatus)
            && string.Equals(intentStatus.GetString(), "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            isPaid = true;
        }

        if (attrs.TryGetProperty("payments", out var paymentsEl) && paymentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var payment in paymentsEl.EnumerateArray())
            {
                if (payment.TryGetProperty("attributes", out var payAttrs)
                    && payAttrs.TryGetProperty("status", out var payStatus)
                    && string.Equals(payStatus.GetString(), "paid", StringComparison.OrdinalIgnoreCase))
                {
                    isPaid = true;
                    if (payAttrs.TryGetProperty("paid_at", out var paidAtEl) && paidAtEl.TryGetInt64(out var unix))
                        paidAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                    break;
                }
            }
        }

        string? userId = null;
        string? planCode = null;
        if (attrs.TryGetProperty("metadata", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
        {
            if (metaEl.TryGetProperty("user_id", out var uidEl)) userId = uidEl.GetString();
            if (metaEl.TryGetProperty("plan_code", out var pcEl)) planCode = pcEl.GetString();
        }

        return new PayMongoCheckoutSessionDetails(
            data.GetProperty("id").GetString() ?? sessionId,
            status,
            isPaid,
            planCode,
            userId,
            paidAt);
    }

    public bool VerifyWebhookSignature(string signatureHeader, string rawBody, string webhookSecret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(webhookSecret))
            return false;

        string? timestamp = null;
        string? testSig = null;
        string? liveSig = null;

        foreach (var part in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].Trim())
            {
                case "t": timestamp = kv[1].Trim(); break;
                case "te": testSig = kv[1].Trim(); break;
                case "li": liveSig = kv[1].Trim(); break;
            }
        }

        if (string.IsNullOrWhiteSpace(timestamp))
            return false;

        var candidateSig = !string.IsNullOrWhiteSpace(testSig) ? testSig : liveSig;
        if (string.IsNullOrWhiteSpace(candidateSig))
            return false;

        var payload = $"{timestamp}.{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var computed = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(candidateSig.ToLowerInvariant()));
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, PayMongoOptions options, string relativePath)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativePath));
        var basicToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.SecretKey}:"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
