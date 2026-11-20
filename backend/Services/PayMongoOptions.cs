namespace SafePassage.Api.Services;

public sealed class PayMongoOptions
{
    public const string SectionName = "PayMongo";

    public string BaseUrl { get; set; } = "https://api.paymongo.com/v1";
    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string SuccessUrl { get; set; } = "http://localhost:5092/subscription/paymongo-callback?status=success";
    public string CancelUrl { get; set; } = "http://localhost:5092/subscription/paymongo-callback?status=cancel";
    public int PendingCheckoutTtlMinutes { get; set; } = 30;
    public bool DemoMode { get; set; }
    public Dictionary<string, PayMongoPlanOption> Plans { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasCredentials => !string.IsNullOrWhiteSpace(SecretKey);
    public bool IsConfigured(bool isDevelopment) => HasCredentials || DemoMode || isDevelopment;
}

public sealed class PayMongoPlanOption
{
    public int AmountCentavos { get; set; }
    public string Currency { get; set; } = "PHP";
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PriceDisplay { get; set; } = string.Empty;
    public string Interval { get; set; } = "Month";
}
