namespace SafePassage.Api.Services;

public sealed class PayPalOptions
{
    public const string SectionName = "PayPal";

    public string Environment { get; set; } = "Sandbox";
    public string BaseUrl { get; set; } = "https://api-m.sandbox.paypal.com";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string WebhookId { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "http://localhost:5092/subscription/paypal-callback?status=success";
    public string CancelUrl { get; set; } = "http://localhost:5092/subscription/paypal-callback?status=cancel";
    public int PendingCheckoutTtlMinutes { get; set; } = 30;
    public Dictionary<string, PayPalPlanOption> Plans { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class PayPalPlanOption
{
    public string PayPalPlanId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PriceDisplay { get; set; } = string.Empty;
    public string Interval { get; set; } = "Month";
}
