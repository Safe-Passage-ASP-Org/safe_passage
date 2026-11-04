using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public enum AccountActivationResult
{
    Activated,
    PasswordRequired,
    InvalidOrExpired
}

public interface IAccountActivationService
{
    Task CreateAndSendInvitationAsync(
        User user,
        CancellationToken cancellationToken,
        bool requiresPasswordSetup = true);
    Task<AccountActivationResult> ActivateAsync(
        string token,
        string? password,
        CancellationToken cancellationToken);
}

public sealed class AccountActivationService(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IActivationEmailSender emailSender,
    IOptions<AccountActivationOptions> options) : IAccountActivationService
{
    public async Task CreateAndSendInvitationAsync(
        User user,
        CancellationToken cancellationToken,
        bool requiresPasswordSetup = true)
    {
        var settings = options.Value;
        if (!Uri.TryCreate(settings.PublicUrl, UriKind.Absolute, out var publicUrl)
            || (publicUrl.Scheme != Uri.UriSchemeHttps && publicUrl.Scheme != Uri.UriSchemeHttp)
            || (publicUrl.Scheme == Uri.UriSchemeHttp
                && !publicUrl.IsLoopback))
        {
            throw new EmailDeliveryException(
                "Account activation URL is not configured as a public HTTPS URL (HTTP is allowed only for localhost).");
        }

        if (settings.TokenLifetimeMinutes is < 5 or > 1440)
            throw new EmailDeliveryException("Account activation token lifetime must be between 5 minutes and 24 hours.");

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var activationToken = new AccountActivationToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            RequiresPasswordSetup = requiresPasswordSetup,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(settings.TokenLifetimeMinutes)
        };
        dbContext.AccountActivationTokens.Add(activationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var url = QueryHelpers.AddQueryString(publicUrl.ToString(), new Dictionary<string, string?>
        {
            ["token"] = rawToken,
            ["setPassword"] = requiresPasswordSetup.ToString().ToLowerInvariant()
        });
        try
        {
            await emailSender.SendActivationEmailAsync(
                user.Email,
                user.FullName ?? user.Username,
                url,
                requiresPasswordSetup,
                cancellationToken);
        }
        catch (EmailDeliveryException)
        {
            dbContext.AccountActivationTokens.Remove(activationToken);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<AccountActivationResult> ActivateAsync(
        string token,
        string? password,
        CancellationToken cancellationToken)
    {
        var tokenHash = HashToken(token);
        var activationToken = await dbContext.AccountActivationTokens
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (activationToken is null
            || activationToken.UsedAt is not null
            || activationToken.ExpiresAt <= now
            || activationToken.User.IsEmailVerified)
        {
            return AccountActivationResult.InvalidOrExpired;
        }
        if (activationToken.RequiresPasswordSetup
            && (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 1024))
        {
            return AccountActivationResult.PasswordRequired;
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (dbContext.Database.IsRelational())
        {
            var consumed = await dbContext.AccountActivationTokens
                .Where(candidate => candidate.Id == activationToken.Id
                    && candidate.UsedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.UsedAt, (DateTimeOffset?)now),
                    cancellationToken);
            if (consumed != 1)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None);
                return AccountActivationResult.InvalidOrExpired;
            }
        }

        activationToken.UsedAt = now;
        if (activationToken.RequiresPasswordSetup)
            activationToken.User.PasswordHash = passwordHasher.HashPassword(activationToken.User, password!);
        activationToken.User.IsEmailVerified = true;
        activationToken.User.IsActive = true;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = activationToken.UserId,
            Timestamp = now,
            Action = "User Activated",
            Resource = activationToken.User.FullName ?? activationToken.User.Username,
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return AccountActivationResult.Activated;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
