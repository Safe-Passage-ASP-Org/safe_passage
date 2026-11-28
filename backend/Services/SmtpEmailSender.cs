using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace SafePassage.Api.Services;

public sealed record SubscriptionReceiptEmail(
    string RecipientEmail,
    string Username,
    string ReferenceId,
    string PlanName,
    string AmountDisplay,
    string PaymentMethod,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset PaidAt,
    string PortalUrl);

public sealed record SubscriptionExpirationReminderEmail(
    string RecipientEmail,
    string Username,
    string PlanName,
    DateTimeOffset PeriodEnd,
    int DaysRemaining,
    string PortalUrl);

public interface ISubscriptionExpirationEmailSender
{
    Task SendSubscriptionExpirationReminderAsync(
        SubscriptionExpirationReminderEmail reminder,
        CancellationToken cancellationToken);
}

public interface IAdminOtpExpirationEmailSender
{
    Task SendAdminOtpExpirationNoticeAsync(
        string email,
        string username,
        DateTimeOffset expiresAt,
        bool isExpired,
        string adminLoginUrl,
        CancellationToken cancellationToken);
}

public interface IActivationEmailSender
{
    Task SendActivationEmailAsync(
        string email,
        string username,
        string activationUrl,
        bool requiresPasswordSetup,
        CancellationToken cancellationToken);
    Task SendAdminEmailChangeVerificationAsync(
        string email,
        string username,
        string verificationUrl,
        string otpCode,
        int lifetimeMinutes,
        CancellationToken cancellationToken);
    Task SendBackupEmailVerificationAsync(
        string email,
        string username,
        string verificationUrl,
        string otpCode,
        int lifetimeMinutes,
        CancellationToken cancellationToken);
    Task SendSubscriptionReceiptAsync(
        SubscriptionReceiptEmail receipt,
        CancellationToken cancellationToken);
    Task SendSignInOtpAsync(
        string email,
        string username,
        string otpCode,
        int lifetimeMinutes,
        bool isAdminLogin,
        CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options)
    : IActivationEmailSender, ISubscriptionExpirationEmailSender, IAdminOtpExpirationEmailSender
{
    public async Task SendActivationEmailAsync(
        string email,
        string username,
        string activationUrl,
        bool requiresPasswordSetup,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            throw new EmailDeliveryException(
                "SMTP is not configured. Set Smtp:Host and Smtp:FromAddress, then retry.");
        }

        if (string.IsNullOrWhiteSpace(settings.Username) != string.IsNullOrWhiteSpace(settings.Password))
            throw new EmailDeliveryException("Configure both Smtp:Username and Smtp:Password, or neither.");

        try
        {
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName)
                    ? new MailAddress(settings.FromAddress)
                    : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = "Finish setting up your Safe Passage account",
                Body = $"Hello {username},\n\n"
                    + (requiresPasswordSetup
                        ? "Open the following one-time link to activate your Safe Passage account and choose your password:\n\n"
                        : "Open the following one-time link to verify your email address and activate your Safe Passage account:\n\n")
                    + $"{activationUrl}\n\n"
                    + "This link expires soon. If you did not expect this invitation, you can ignore this message.",
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(email));

            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = 15000
            };
            if (!string.IsNullOrWhiteSpace(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);

            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception)
        {
            throw new EmailDeliveryException("The SMTP server could not deliver the activation email.", exception);
        }
        catch (FormatException exception)
        {
            throw new EmailDeliveryException("The SMTP sender or recipient email address is invalid.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new EmailDeliveryException("The SMTP client could not send the activation email.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception);
        }
    }

    public async Task SendAdminEmailChangeVerificationAsync(
        string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new EmailDeliveryException("SMTP is not configured to send administrator email change verification.");
        try
        {
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName) ? new MailAddress(settings.FromAddress) : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = "Verify your new Safe Passage admin email",
                Body = $"Hello {username},\n\nUse this one-time link to verify the new email address for your Safe Passage administrator account:\n\n{verificationUrl}\n\nOr enter this 6-digit code in Account Settings:\n\n    {otpCode}\n\nBoth expire in {lifetimeMinutes} minutes. If you did not request this change, ignore this email.",
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(email));
            using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl, Timeout = 15000 };
            if (!string.IsNullOrWhiteSpace(settings.Username)) client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the email change verification.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The email change recipient address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the email change verification.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    public async Task SendBackupEmailVerificationAsync(
        string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new EmailDeliveryException("SMTP is not configured to send backup email verification.");
        try
        {
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName) ? new MailAddress(settings.FromAddress) : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = "Verify your Safe Passage backup email",
                Body = $"Hello {username},\n\nUse this one-time link to verify the backup email address for your Safe Passage account:\n\n{verificationUrl}\n\nOr enter this 6-digit code in Profile Settings:\n\n    {otpCode}\n\nBoth expire in {lifetimeMinutes} minutes. If you did not request this, ignore this email.",
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(email));
            using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl, Timeout = 15000 };
            if (!string.IsNullOrWhiteSpace(settings.Username)) client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the backup email verification.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The backup email recipient address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the backup email verification.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    public async Task SendSubscriptionReceiptAsync(
        SubscriptionReceiptEmail receipt,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new EmailDeliveryException("SMTP is not configured to send subscription receipt emails.");

        try
        {
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName)
                    ? new MailAddress(settings.FromAddress)
                    : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = $"Safe Passage Subscription Receipt ({receipt.ReferenceId})",
                Body = FormatReceiptBody(receipt),
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(receipt.RecipientEmail));
            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = 15000
            };
            if (!string.IsNullOrWhiteSpace(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the subscription receipt email.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The subscription receipt email address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the subscription receipt email.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    public async Task SendSubscriptionExpirationReminderAsync(
        SubscriptionExpirationReminderEmail reminder,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new EmailDeliveryException("SMTP is not configured to send subscription expiration reminders.");

        try
        {
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName)
                    ? new MailAddress(settings.FromAddress)
                    : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = "Your Safe Passage subscription period is ending soon",
                Body = FormatSubscriptionExpirationReminderBody(reminder),
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(reminder.RecipientEmail));
            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = 15000
            };
            if (!string.IsNullOrWhiteSpace(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the subscription expiration reminder.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The subscription reminder email address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the subscription expiration reminder.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    internal static string FormatSubscriptionExpirationReminderBody(SubscriptionExpirationReminderEmail reminder) =>
        $"Hello {reminder.Username},\n\n"
        + $"Your Safe Passage {reminder.PlanName} subscription period ends on {reminder.PeriodEnd.ToLocalTime():MMMM d, yyyy} (in {reminder.DaysRemaining} day{(reminder.DaysRemaining == 1 ? "" : "s")}).\n\n"
        + "Review your subscription and billing details to make sure your access continues as expected:\n"
        + $"{reminder.PortalUrl}\n\n"
        + "If you have already renewed or updated your subscription, you can ignore this reminder.";

    internal static string FormatReceiptBody(SubscriptionReceiptEmail receipt) =>
        $"Hello {receipt.Username},\n\n"
        + "Thank you for your Safe Passage subscription payment. Here is your receipt summary:\n\n"
        + $"Receipt / Reference ID: {receipt.ReferenceId}\n"
        + $"Plan: {receipt.PlanName}\n"
        + $"Billing Period: {receipt.PeriodStart:MMM dd, yyyy} - {receipt.PeriodEnd:MMM dd, yyyy}\n"
        + $"Amount: {receipt.AmountDisplay}\n"
        + $"Payment Method: {receipt.PaymentMethod}\n"
        + $"Paid At (UTC): {receipt.PaidAt:yyyy-MM-dd HH:mm:ss} UTC\n\n"
        + $"Manage your subscription and billing history in the Safe Passage Portal:\n{receipt.PortalUrl}\n";

    public async Task SendSignInOtpAsync(
        string email,
        string username,
        string otpCode,
        int lifetimeMinutes,
        bool isAdminLogin,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new EmailDeliveryException("SMTP is not configured to send sign-in OTP emails.");

        try
        {
            var subject = isAdminLogin
                ? "Your Safe Passage administrator verification code"
                : "Your Safe Passage sign-in verification code";
            var textBody = FormatOtpBody(username, otpCode, lifetimeMinutes, isAdminLogin);
            var htmlBody = FormatOtpHtmlBody(username, otpCode, lifetimeMinutes, isAdminLogin);
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName)
                    ? new MailAddress(settings.FromAddress)
                    : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = subject,
                Body = textBody,
                IsBodyHtml = false
            };
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html"));
            message.To.Add(new MailAddress(email));
            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = 15000
            };
            if (!string.IsNullOrWhiteSpace(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the OTP email.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The OTP email address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the OTP email.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    public static string FormatOtpBody(string username, string otpCode, int lifetimeMinutes, bool isAdminLogin)
    {
        var expiry = isAdminLogin && lifetimeMinutes == 7 * 24 * 60
            ? "7 days"
            : $"{lifetimeMinutes} minutes";
        return $"Hello {username},\n\n"
            + (isAdminLogin
                ? "Use this 6-digit verification code on the Safe Passage Admin Login page:\n\n"
                : "Use this 6-digit verification code to complete your Safe Passage sign-in:\n\n")
            + $"    {otpCode}\n\n"
            + (isAdminLogin
                ? $"This code expires in {expiry} and can be reused for administrator sign-ins until it expires or a new code is requested.\n"
                : $"This code expires in {expiry} and can only be used once.\n")
            + "If you did not request this code, ignore this email and review your account activity.\n";
    }

    public static string FormatOtpHtmlBody(
        string username,
        string otpCode,
        int lifetimeMinutes,
        bool isAdminLogin,
        string? logoUrl = null)
    {
        var resolvedLogoUrl = string.IsNullOrWhiteSpace(logoUrl)
            ? "https://safe-passage-web.onrender.com/favicon.png"
            : logoUrl.Trim();
        var expiry = isAdminLogin && lifetimeMinutes == 7 * 24 * 60
            ? "7 days"
            : $"{lifetimeMinutes} minutes";
        var eyebrow = isAdminLogin ? "ADMINISTRATOR ACCESS" : "SIGN-IN VERIFICATION";
        var heading = isAdminLogin
            ? "Your administrator verification code"
            : "Your sign-in verification code";
        var intro = isAdminLogin
            ? "Use this 6-digit verification code on the Safe Passage Admin Login page:"
            : "Use this 6-digit verification code to complete your Safe Passage sign-in:";
        var validityNote = isAdminLogin
            ? $"Valid for <strong>{WebUtility.HtmlEncode(expiry)}</strong> &mdash; you can reuse this code for administrator sign-ins until it expires or a new code is requested."
            : $"Expires in <strong>{WebUtility.HtmlEncode(expiry)}</strong> &mdash; single-use verification code.";

        var encodedName = WebUtility.HtmlEncode(username);
        var encodedCode = WebUtility.HtmlEncode(otpCode);
        var encodedLogo = WebUtility.HtmlEncode(resolvedLogoUrl);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>{{WebUtility.HtmlEncode(heading)}}</title>
</head>
<body style="margin:0;padding:0;background-color:#f8fafc;font-family:'Inter','Segoe UI',system-ui,-apple-system,sans-serif;color:#0f172a;">
  <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background-color:#f8fafc;padding:32px 16px;">
    <tr>
      <td align="center">
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:520px;">
          <tr>
            <td align="center" style="padding-bottom:20px;">
              <table role="presentation" cellspacing="0" cellpadding="0">
                <tr>
                  <td style="width:38px;height:38px;border-radius:10px;background-color:#e0f2fe;text-align:center;vertical-align:middle;">
                    <img src="{{encodedLogo}}" alt="Safe Passage" width="38" height="38" style="display:block;width:38px;height:38px;border-radius:10px;border:0;" />
                  </td>
                  <td style="padding-left:11px;font-size:21px;font-weight:700;letter-spacing:-0.5px;color:#0f172a;">
                    Safe Passage
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td style="background-color:#ffffff;border:1px solid #e2e8f0;border-radius:12px;padding:32px;box-shadow:0 12px 32px rgba(15,23,42,0.06);">
              <p style="margin:0 0 8px;font-size:11px;font-weight:800;letter-spacing:1.4px;text-transform:uppercase;color:#0284c7;">
                {{eyebrow}}
              </p>
              <h1 style="margin:0 0 16px;font-size:22px;line-height:1.25;letter-spacing:-0.5px;color:#0f172a;">
                {{WebUtility.HtmlEncode(heading)}}
              </h1>
              <p style="margin:0 0 12px;font-size:14px;line-height:1.6;color:#334155;">
                Hello {{encodedName}},
              </p>
              <p style="margin:0 0 24px;font-size:14px;line-height:1.6;color:#64748b;">
                {{intro}}
              </p>
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin-bottom:24px;">
                <tr>
                  <td align="center" style="background-color:#f8fafc;border:1px solid #bae6fd;border-radius:10px;padding:20px 16px;">
                    <span style="font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;font-size:32px;font-weight:700;letter-spacing:8px;color:#0f172a;">
                      {{encodedCode}}
                    </span>
                  </td>
                </tr>
              </table>
              <p style="margin:0 0 20px;padding:12px 14px;background-color:#f0f9ff;border-left:3px solid #0284c7;border-radius:6px;font-size:13px;line-height:1.5;color:#0c4a6e;">
                {{validityNote}}
              </p>
              <p style="margin:0;font-size:12px;line-height:1.6;color:#64748b;">
                If you did not request this code, ignore this email and review your account activity.
              </p>
            </td>
          </tr>
          <tr>
            <td align="center" style="padding-top:18px;font-size:12px;color:#94a3b8;">
              Safe Passage Identity &amp; Access Security
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>
""";
    }

    public async Task SendAdminOtpExpirationNoticeAsync(
        string email,
        string username,
        DateTimeOffset expiresAt,
        bool isExpired,
        string adminLoginUrl,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new EmailDeliveryException("SMTP is not configured to send administrator OTP expiration emails.");

        var subject = isExpired
            ? "Your Safe Passage administrator code has expired"
            : "Your Safe Passage administrator code expires tomorrow";
        var textBody = FormatAdminOtpExpirationBody(username, expiresAt, isExpired, adminLoginUrl);
        var htmlBody = FormatAdminOtpExpirationHtmlBody(username, expiresAt, isExpired, adminLoginUrl);

        try
        {
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName)
                    ? new MailAddress(settings.FromAddress)
                    : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = subject,
                Body = textBody,
                IsBodyHtml = false
            };
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html"));
            message.To.Add(new MailAddress(email));

            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = 15000
            };
            if (!string.IsNullOrWhiteSpace(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the OTP expiration email.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The OTP expiration email address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the OTP expiration email.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    public static string FormatAdminOtpExpirationBody(
        string username,
        DateTimeOffset expiresAt,
        bool isExpired,
        string adminLoginUrl)
    {
        var formattedDate = expiresAt.ToUniversalTime().ToString("MMM d, yyyy 'at' HH:mm 'UTC'");
        return isExpired
            ? $"Hello {username},\n\n"
                + $"Your 7-day Safe Passage administrator verification code expired on {formattedDate}.\n\n"
                + $"Request a new 6-digit verification code on the Admin Login page:\n{adminLoginUrl}\n"
            : $"Hello {username},\n\n"
                + $"Your 7-day Safe Passage administrator verification code will expire tomorrow ({formattedDate}).\n\n"
                + $"You can continue using your current code until it expires, or request a new code on the Admin Login page:\n{adminLoginUrl}\n";
    }

    public static string FormatAdminOtpExpirationHtmlBody(
        string username,
        DateTimeOffset expiresAt,
        bool isExpired,
        string adminLoginUrl,
        string? logoUrl = null)
    {
        var resolvedLogoUrl = string.IsNullOrWhiteSpace(logoUrl)
            ? "https://safe-passage-web.onrender.com/favicon.png"
            : logoUrl.Trim();
        var heading = isExpired
            ? "Your administrator code has expired"
            : "Your administrator code expires tomorrow";
        var formattedDate = expiresAt.ToUniversalTime().ToString("MMM d, yyyy 'at' HH:mm 'UTC'");
        var summary = isExpired
            ? $"Your 7-day Safe Passage administrator verification code expired on <strong>{WebUtility.HtmlEncode(formattedDate)}</strong>."
            : $"Your 7-day Safe Passage administrator verification code will expire tomorrow (<strong>{WebUtility.HtmlEncode(formattedDate)}</strong>).";
        var actionText = isExpired
            ? "Request a new 6-digit verification code to continue signing in to the Safe Passage Admin Portal."
            : "You can keep using your current code until it expires, or request a new 6-digit code at any time.";
        var buttonText = isExpired ? "Request New Code" : "Open Admin Login";

        var encodedName = WebUtility.HtmlEncode(username);
        var encodedLogo = WebUtility.HtmlEncode(resolvedLogoUrl);
        var encodedUrl = WebUtility.HtmlEncode(adminLoginUrl);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>{{WebUtility.HtmlEncode(heading)}}</title>
</head>
<body style="margin:0;padding:0;background-color:#f8fafc;font-family:'Inter','Segoe UI',system-ui,-apple-system,sans-serif;color:#0f172a;">
  <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background-color:#f8fafc;padding:32px 16px;">
    <tr>
      <td align="center">
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:520px;">
          <tr>
            <td align="center" style="padding-bottom:20px;">
              <table role="presentation" cellspacing="0" cellpadding="0">
                <tr>
                  <td style="width:38px;height:38px;border-radius:10px;background-color:#e0f2fe;text-align:center;vertical-align:middle;">
                    <img src="{{encodedLogo}}" alt="Safe Passage" width="38" height="38" style="display:block;width:38px;height:38px;border-radius:10px;border:0;" />
                  </td>
                  <td style="padding-left:11px;font-size:21px;font-weight:700;letter-spacing:-0.5px;color:#0f172a;">
                    Safe Passage
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td style="background-color:#ffffff;border:1px solid #e2e8f0;border-radius:12px;padding:32px;box-shadow:0 12px 32px rgba(15,23,42,0.06);">
              <p style="margin:0 0 8px;font-size:11px;font-weight:800;letter-spacing:1.4px;text-transform:uppercase;color:#0284c7;">
                ADMINISTRATOR ACCESS
              </p>
              <h1 style="margin:0 0 16px;font-size:22px;line-height:1.25;letter-spacing:-0.5px;color:#0f172a;">
                {{WebUtility.HtmlEncode(heading)}}
              </h1>
              <p style="margin:0 0 12px;font-size:14px;line-height:1.6;color:#334155;">
                Hello {{encodedName}},
              </p>
              <p style="margin:0 0 16px;font-size:14px;line-height:1.6;color:#334155;">
                {{summary}}
              </p>
              <p style="margin:0 0 24px;font-size:14px;line-height:1.6;color:#64748b;">
                {{WebUtility.HtmlEncode(actionText)}}
              </p>
              <table role="presentation" cellspacing="0" cellpadding="0">
                <tr>
                  <td style="border-radius:8px;background-color:#0284c7;">
                    <a href="{{encodedUrl}}" style="display:inline-block;padding:12px 20px;font-size:14px;font-weight:600;color:#ffffff;text-decoration:none;">
                      {{buttonText}}
                    </a>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td align="center" style="padding-top:18px;font-size:12px;color:#94a3b8;">
              Safe Passage Identity &amp; Access Security
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>
""";
    }
}
