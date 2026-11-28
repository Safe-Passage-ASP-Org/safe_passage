using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Data;

namespace SafePassage.Api.Services;

public sealed class AdminOtpExpirationOptions
{
    public const string SectionName = "AdminOtpExpiration";

    public bool Enabled { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 60;
    public int InitialDelaySeconds { get; set; } = 60;
}

public sealed class AdminOtpExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminOtpExpirationOptions> options,
    IConfiguration configuration,
    ILogger<AdminOtpExpirationWorker> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Admin OTP expiration notification job is disabled.");
            return 0;
        }

        return await SendDueNoticesAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Admin OTP expiration worker is disabled.");
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(settings.InitialDelaySeconds, 0, 3600));
        var checkInterval = TimeSpan.FromMinutes(Math.Clamp(settings.CheckIntervalMinutes, 1, 1440));
        await Task.Delay(initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueNoticesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Admin OTP expiration check failed.");
            }

            await Task.Delay(checkInterval, stoppingToken);
        }
    }

    private async Task<int> SendDueNoticesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IAdminOtpExpirationEmailSender>();
        var now = DateTimeOffset.UtcNow;
        var warningCutoff = now.AddDays(1);
        var adminLoginUrl = ResolveAdminLoginUrl();

        var tokens = await dbContext.AdminLoginTokens
            .Include(token => token.User)
            .Where(token =>
                token.UsedAt == null
                && token.User.IsActive
                && token.User.IsEmailVerified
                && (token.ExpirationWarningSentAt == null || token.ExpiredNoticeSentAt == null))
            .ToListAsync(cancellationToken);

        var sentCount = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.User.Email))
                continue;

            if (token.ExpiresAt <= now && token.ExpiredNoticeSentAt is null)
            {
                try
                {
                    await emailSender.SendAdminOtpExpirationNoticeAsync(
                        token.User.Email,
                        token.User.FullName ?? token.User.Username,
                        token.ExpiresAt,
                        isExpired: true,
                        adminLoginUrl,
                        cancellationToken);
                    token.ExpiredNoticeSentAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    sentCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not send expired admin OTP notice for token {TokenId}.", token.Id);
                }
            }
            else if (token.ExpiresAt > now && token.ExpiresAt <= warningCutoff && token.ExpirationWarningSentAt is null)
            {
                try
                {
                    await emailSender.SendAdminOtpExpirationNoticeAsync(
                        token.User.Email,
                        token.User.FullName ?? token.User.Username,
                        token.ExpiresAt,
                        isExpired: false,
                        adminLoginUrl,
                        cancellationToken);
                    token.ExpirationWarningSentAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    sentCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not send expiring-tomorrow admin OTP notice for token {TokenId}.", token.Id);
                }
            }
        }

        return sentCount;
    }

    private string ResolveAdminLoginUrl()
    {
        var configuredUrl = configuration["AdminLogin:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
            return configuredUrl.Trim();

        var frontendUrl = configuration["Frontend:BaseUrl"]
            ?? configuration["Facebook:FrontendUrl"]
            ?? configuration["GitHub:FrontendUrl"]
            ?? configuration["GitLab:FrontendUrl"];
        if (!string.IsNullOrWhiteSpace(frontendUrl)
            && Uri.TryCreate(frontendUrl.Trim(), UriKind.Absolute, out var frontendUri))
            return new Uri(frontendUri, "/admin-login").ToString();

        return "https://safe-passage-web.onrender.com/admin-login";
    }
}
