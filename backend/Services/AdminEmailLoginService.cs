using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class AdminLoginOptions
{
    public const string SectionName = "AdminLogin";
    public int TokenLifetimeMinutes { get; set; } = 10;
    public string? PublicUrl { get; set; }
}

public interface IAdminEmailLoginService
{
    Task RequestLinkAsync(string email, CancellationToken cancellationToken);
    Task<AuthFlowResult<LoginResponse>> CompleteLinkAsync(string token, CancellationToken cancellationToken);
}

public sealed class AdminEmailLoginService(
    ApplicationDbContext dbContext,
    IActivationEmailSender emailSender,
    IAuthFlowService authFlow,
    IConfiguration configuration,
    IOptions<AdminLoginOptions> options,
    ILogger<AdminEmailLoginService> logger) : IAdminEmailLoginService
{
    public async Task RequestLinkAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate =>
            candidate.Email.ToUpper() == normalizedEmail
            && candidate.IsActive
            && candidate.IsEmailVerified
            && (candidate.Role.ToUpper() == "ADMIN"
                || candidate.Role.ToUpper() == "ADMINISTRATOR"
                || candidate.Role.ToUpper() == "SUPER ADMIN"), cancellationToken);

        if (user is null) return;

        var settings = options.Value;
        if (settings.TokenLifetimeMinutes is < 5 or > 30)
            throw new InvalidOperationException("Admin login link lifetime must be between 5 and 30 minutes.");

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        await dbContext.AdminLoginTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.UsedAt, (DateTimeOffset?)now),
                cancellationToken);
        var loginToken = new AdminLoginToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(settings.TokenLifetimeMinutes)
        };
        dbContext.AdminLoginTokens.Add(loginToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var link = new UriBuilder(GetFrontendLoginUrl(settings))
        {
            Fragment = "emailToken=" + Uri.EscapeDataString(rawToken)
        };
        try
        {
            await emailSender.SendAdminLoginLinkAsync(
                user.Email,
                user.FullName ?? user.Username,
                link.Uri.ToString(),
                settings.TokenLifetimeMinutes,
                cancellationToken);
        }
        catch (EmailDeliveryException)
        {
            dbContext.AdminLoginTokens.Remove(loginToken);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            dbContext.AdminLoginTokens.Remove(loginToken);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<AuthFlowResult<LoginResponse>> CompleteLinkAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        var tokenHash = HashToken(token);
        var loginToken = await dbContext.AdminLoginTokens
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (loginToken is null
            || loginToken.UsedAt is not null
            || loginToken.ExpiresAt <= now
            || !IsActiveAdministrator(loginToken.User))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        // Verify MFA device readiness before consuming the single-use email token so
        // administrators without an enrolled trusted device do not burn their link.
        var mobileDevices = await dbContext.Devices
            .AsNoTracking()
            .Where(device => device.UserId == loginToken.UserId
                && device.Status == DeviceStatus.Active
                && device.TrustStatus == DeviceTrustStatus.Trusted
                && (device.DeviceType.ToLower() == "mobile" || device.DeviceType.ToLower() == "phone"
                    || device.OS.ToLower().StartsWith("android") || device.OS.ToLower().StartsWith("ios")))
            .ToListAsync(cancellationToken);

        if (mobileDevices.Count == 0)
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.NoActiveMobileDevice);

        if (!mobileDevices.Any(d => !string.IsNullOrWhiteSpace(d.SigningPublicKey)))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.DeviceSigningKeyNotRegistered);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var consumed = await dbContext.AdminLoginTokens
            .Where(candidate => candidate.Id == loginToken.Id
                && candidate.UsedAt == null
                && candidate.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.UsedAt, (DateTimeOffset?)now),
                cancellationToken);
        if (consumed != 1)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);
        }

        loginToken.UsedAt = now;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = loginToken.UserId,
            Timestamp = now,
            Action = "Admin Email Login",
            Resource = "Email link verified; mobile approval required",
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var result = await authFlow.RequestAdminLoginMfaAsync(loginToken.User, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            logger.LogWarning("Admin email link was verified for user {UserId}, but mobile approval could not start: {Failure}.", loginToken.UserId, result.Failure);
        return result;
    }

    private string GetFrontendLoginUrl(AdminLoginOptions settings)
    {
        var configuredUrl = settings.PublicUrl
            ?? configuration["Frontend:BaseUrl"]
            ?? configuration["AccountActivation:PublicUrl"]
            ?? configuration["Facebook:FrontendUrl"]
            ?? "http://localhost:5092";
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configuredUri)
            || (configuredUri.Scheme != Uri.UriSchemeHttps
                && !(configuredUri.Scheme == Uri.UriSchemeHttp && configuredUri.IsLoopback)))
            throw new InvalidOperationException("AdminLogin:PublicUrl must be HTTPS (HTTP is allowed only for localhost).");

        var builder = new UriBuilder(configuredUri)
        {
            Path = "/admin-login",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static bool IsActiveAdministrator(User user) =>
        user.IsActive
        && user.IsEmailVerified
        && (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(user.Role, "Administrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(user.Role, "Super Admin", StringComparison.OrdinalIgnoreCase));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
