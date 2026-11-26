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

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IActivationEmailSender, ISubscriptionExpirationEmailSender
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
            using var message = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(settings.FromName)
                    ? new MailAddress(settings.FromAddress)
                    : new MailAddress(settings.FromAddress, settings.FromName),
                Subject = subject,
                Body = FormatOtpBody(username, otpCode, lifetimeMinutes, isAdminLogin),
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
        catch (SmtpException exception) { throw new EmailDeliveryException("The SMTP server could not deliver the OTP email.", exception); }
        catch (FormatException exception) { throw new EmailDeliveryException("The OTP email address is invalid.", exception); }
        catch (InvalidOperationException exception) { throw new EmailDeliveryException("The SMTP client could not send the OTP email.", exception); }
        catch (ArgumentException exception) { throw new EmailDeliveryException("The SMTP configuration contains an invalid address or host.", exception); }
    }

    internal static string FormatOtpBody(string username, string otpCode, int lifetimeMinutes, bool isAdminLogin)
    {
        var expiry = isAdminLogin && lifetimeMinutes == 7 * 24 * 60
            ? "7 days"
            : $"{lifetimeMinutes} minutes";
        return $"Hello {username},\n\n"
            + (isAdminLogin
                ? "Use this 6-digit verification code on the Safe Passage Admin Login page:\n\n"
                : "Use this 6-digit verification code to complete your Safe Passage sign-in:\n\n")
            + $"    {otpCode}\n\n"
            + $"This code expires in {expiry} and can only be used once.\n"
            + "If you did not request this code, ignore this email and review your account activity.\n";
    }
}
