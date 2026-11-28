using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace SafePassage.Api.Services;

public sealed class HttpApiActivationEmailSender(
    HttpClient httpClient,
    IConfiguration configuration,
    IOptions<SmtpOptions> smtpOptions) : IActivationEmailSender, ISubscriptionExpirationEmailSender
{
    public async Task SendActivationEmailAsync(
        string email,
        string username,
        string activationUrl,
        bool requiresPasswordSetup,
        CancellationToken cancellationToken)
    {
        var subject = "Finish setting up your Safe Passage account";
        var body = $"Hello {username},\n\n"
            + (requiresPasswordSetup
                ? "Open the following one-time link to activate your Safe Passage account and choose your password:\n\n"
                : "Open the following one-time link to verify your email address and activate your Safe Passage account:\n\n")
            + $"{activationUrl}\n\n"
            + "This link expires soon. If you did not expect this invitation, you can ignore this message.";

        var brevoApiKey = configuration["Brevo:ApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendViaBrevoAsync(brevoApiKey, email, username, subject, body, cancellationToken);
            return;
        }

        var resendApiKey = configuration["Resend:ApiKey"];
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            await SendViaResendAsync(resendApiKey, email, subject, body, cancellationToken);
            return;
        }

        var smtpFallback = new SmtpEmailSender(smtpOptions);
        await smtpFallback.SendActivationEmailAsync(email, username, activationUrl, requiresPasswordSetup, cancellationToken);
    }

    public async Task SendAdminEmailChangeVerificationAsync(
        string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes,
        CancellationToken cancellationToken)
    {
        var subject = "Verify your new Safe Passage admin email";
        var body = $"Hello {username},\n\nUse this one-time link to verify the new email address for your Safe Passage administrator account:\n\n{verificationUrl}\n\nOr enter this 6-digit code in Account Settings:\n\n    {otpCode}\n\nBoth expire in {lifetimeMinutes} minutes. If you did not request this change, ignore this email.";
        var brevoApiKey = configuration["Brevo:ApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendViaBrevoAsync(brevoApiKey, email, username, subject, body, cancellationToken);
            return;
        }
        var resendApiKey = configuration["Resend:ApiKey"];
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            await SendViaResendAsync(resendApiKey, email, subject, body, cancellationToken);
            return;
        }
        await new SmtpEmailSender(smtpOptions).SendAdminEmailChangeVerificationAsync(email, username, verificationUrl, otpCode, lifetimeMinutes, cancellationToken);
    }

    public async Task SendBackupEmailVerificationAsync(
        string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes,
        CancellationToken cancellationToken)
    {
        const string subject = "Verify your Safe Passage backup email";
        var body = $"Hello {username},\n\nUse this one-time link to verify the backup email address for your Safe Passage account:\n\n{verificationUrl}\n\nOr enter this 6-digit code in Profile Settings:\n\n    {otpCode}\n\nBoth expire in {lifetimeMinutes} minutes. If you did not request this, ignore this email.";
        var brevoApiKey = configuration["Brevo:ApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendViaBrevoAsync(brevoApiKey, email, username, subject, body, cancellationToken);
            return;
        }
        var resendApiKey = configuration["Resend:ApiKey"];
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            await SendViaResendAsync(resendApiKey, email, subject, body, cancellationToken);
            return;
        }
        await new SmtpEmailSender(smtpOptions).SendBackupEmailVerificationAsync(email, username, verificationUrl, otpCode, lifetimeMinutes, cancellationToken);
    }

    public async Task SendSubscriptionReceiptAsync(
        SubscriptionReceiptEmail receipt,
        CancellationToken cancellationToken)
    {
        var subject = $"Safe Passage Subscription Receipt ({receipt.ReferenceId})";
        var body = SmtpEmailSender.FormatReceiptBody(receipt);

        var brevoApiKey = configuration["Brevo:ApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendViaBrevoAsync(brevoApiKey, receipt.RecipientEmail, receipt.Username, subject, body, cancellationToken);
            return;
        }

        var resendApiKey = configuration["Resend:ApiKey"];
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            await SendViaResendAsync(resendApiKey, receipt.RecipientEmail, subject, body, cancellationToken);
            return;
        }

        await new SmtpEmailSender(smtpOptions).SendSubscriptionReceiptAsync(receipt, cancellationToken);
    }

    public async Task SendSubscriptionExpirationReminderAsync(
        SubscriptionExpirationReminderEmail reminder,
        CancellationToken cancellationToken)
    {
        const string subject = "Your Safe Passage subscription period is ending soon";
        var body = SmtpEmailSender.FormatSubscriptionExpirationReminderBody(reminder);

        var brevoApiKey = configuration["Brevo:ApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendViaBrevoAsync(brevoApiKey, reminder.RecipientEmail, reminder.Username, subject, body, cancellationToken);
            return;
        }

        var resendApiKey = configuration["Resend:ApiKey"];
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            await SendViaResendAsync(resendApiKey, reminder.RecipientEmail, subject, body, cancellationToken);
            return;
        }

        await new SmtpEmailSender(smtpOptions).SendSubscriptionExpirationReminderAsync(reminder, cancellationToken);
    }

    public async Task SendSignInOtpAsync(
        string email,
        string username,
        string otpCode,
        int lifetimeMinutes,
        bool isAdminLogin,
        CancellationToken cancellationToken)
    {
        var subject = isAdminLogin
            ? "Your Safe Passage administrator verification code"
            : "Your Safe Passage sign-in verification code";
        var body = SmtpEmailSender.FormatOtpBody(username, otpCode, lifetimeMinutes, isAdminLogin);
        var htmlBody = SmtpEmailSender.FormatOtpHtmlBody(username, otpCode, lifetimeMinutes, isAdminLogin, ResolveLogoUrl());

        var brevoApiKey = configuration["Brevo:ApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoApiKey))
        {
            await SendViaBrevoAsync(brevoApiKey, email, username, subject, body, cancellationToken, htmlBody);
            return;
        }

        var resendApiKey = configuration["Resend:ApiKey"];
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            await SendViaResendAsync(resendApiKey, email, subject, body, cancellationToken, htmlBody);
            return;
        }

        await new SmtpEmailSender(smtpOptions).SendSignInOtpAsync(
            email, username, otpCode, lifetimeMinutes, isAdminLogin, cancellationToken);
    }

    private string ResolveLogoUrl()
    {
        var explicitLogoUrl = configuration["EmailBranding:LogoUrl"];
        if (!string.IsNullOrWhiteSpace(explicitLogoUrl))
            return explicitLogoUrl.Trim();

        var frontendUrl = configuration["Facebook:FrontendUrl"]
            ?? configuration["GitHub:FrontendUrl"]
            ?? configuration["GitLab:FrontendUrl"];
        if (!string.IsNullOrWhiteSpace(frontendUrl)
            && Uri.TryCreate(frontendUrl.Trim(), UriKind.Absolute, out var uri)
            && !uri.IsLoopback)
            return $"{uri.GetLeftPart(UriPartial.Authority)}/favicon.png";

        return "https://safe-passage-web.onrender.com/favicon.png";
    }

    private async Task SendViaBrevoAsync(
        string apiKey,
        string recipientEmail,
        string recipientName,
        string subject,
        string textContent,
        CancellationToken cancellationToken,
        string? htmlContent = null)
    {
        var fromEmail = configuration["Brevo:FromAddress"] ?? smtpOptions.Value.FromAddress;
        var fromName = configuration["Brevo:FromName"] ?? smtpOptions.Value.FromName ?? "Safe Passage";
        if (string.IsNullOrWhiteSpace(fromEmail))
            throw new EmailDeliveryException("Configure Brevo:FromAddress to send activation emails via Brevo.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        request.Headers.Add("api-key", apiKey.Trim());
        request.Content = string.IsNullOrWhiteSpace(htmlContent)
            ? JsonContent.Create(new
            {
                sender = new { email = fromEmail.Trim(), name = fromName.Trim() },
                to = new[] { new { email = recipientEmail, name = recipientName } },
                subject,
                textContent
            })
            : JsonContent.Create(new
            {
                sender = new { email = fromEmail.Trim(), name = fromName.Trim() },
                to = new[] { new { email = recipientEmail, name = recipientName } },
                subject,
                textContent,
                htmlContent
            });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new EmailDeliveryException($"Brevo email delivery failed ({(int)response.StatusCode}): {detail}");
        }
    }

    private async Task SendViaResendAsync(
        string apiKey,
        string recipientEmail,
        string subject,
        string text,
        CancellationToken cancellationToken,
        string? html = null)
    {
        var fromEmail = configuration["Resend:FromAddress"] ?? "Safe Passage <onboarding@resend.dev>";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = string.IsNullOrWhiteSpace(html)
            ? JsonContent.Create(new
            {
                from = fromEmail.Trim(),
                to = new[] { recipientEmail },
                subject,
                text
            })
            : JsonContent.Create(new
            {
                from = fromEmail.Trim(),
                to = new[] { recipientEmail },
                subject,
                text,
                html
            });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new EmailDeliveryException($"Resend email delivery failed ({(int)response.StatusCode}): {detail}");
        }
    }
}
