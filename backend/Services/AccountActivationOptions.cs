namespace SafePassage.Api.Services;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
}

public sealed class AccountActivationOptions
{
    public const string SectionName = "AccountActivation";

    public string? PublicUrl { get; set; }
    public int TokenLifetimeMinutes { get; set; } = 60;
}

public sealed class EmailDeliveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);
