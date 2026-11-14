using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace SafePassage.Api.Services;

public interface IActivationEmailSender
{
    Task SendActivationEmailAsync(
        string email,
        string username,
        string activationUrl,
        bool requiresPasswordSetup,
        CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IActivationEmailSender
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
}
