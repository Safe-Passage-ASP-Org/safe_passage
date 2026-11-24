using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class SubscriptionExpirationReminderOptions
{
    public const string SectionName = "SubscriptionExpirationReminder";

    public bool Enabled { get; set; } = true;
    public int LeadTimeDays { get; set; } = 7;
    public int CheckIntervalHours { get; set; } = 24;
    public int InitialDelaySeconds { get; set; } = 60;
}

public sealed class SubscriptionExpirationReminderWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SubscriptionExpirationReminderOptions> options,
    IOptions<PayMongoOptions> payMongoOptions,
    IConfiguration configuration,
    ILogger<SubscriptionExpirationReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Subscription expiration reminder worker is disabled.");
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(settings.InitialDelaySeconds, 0, 3600));
        var checkInterval = TimeSpan.FromHours(Math.Clamp(settings.CheckIntervalHours, 1, 168));
        await Task.Delay(initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Subscription expiration reminder check failed.");
            }

            await Task.Delay(checkInterval, stoppingToken);
        }
    }

    private async Task SendDueRemindersAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<ISubscriptionExpirationEmailSender>();
        var now = DateTimeOffset.UtcNow;
        var dueBefore = now.AddDays(Math.Clamp(options.Value.LeadTimeDays, 1, 30));

        var subscriptions = await dbContext.UserSubscriptions
            .Include(subscription => subscription.User)
            .Where(subscription =>
                (subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.Cancelled)
                && subscription.CurrentPeriodEnd != null
                && subscription.CurrentPeriodEnd > now
                && subscription.CurrentPeriodEnd <= dueBefore
                && subscription.ExpirationReminderPeriodEnd != subscription.CurrentPeriodEnd
                && subscription.User.IsActive
                && subscription.User.IsEmailVerified)
            .OrderBy(subscription => subscription.CurrentPeriodEnd)
            .Take(500)
            .ToListAsync(cancellationToken);

        foreach (var subscription in subscriptions)
        {
            var periodEnd = subscription.CurrentPeriodEnd!.Value;
            if (string.IsNullOrWhiteSpace(subscription.User.Email))
                continue;

            var remainingDays = Math.Max(1, (int)Math.Ceiling((periodEnd - now).TotalDays));
            var planName = payMongoOptions.Value.Plans.TryGetValue(subscription.PlanCode, out var plan)
                && !string.IsNullOrWhiteSpace(plan.Name)
                    ? plan.Name
                    : subscription.PlanCode;

            try
            {
                await emailSender.SendSubscriptionExpirationReminderAsync(
                    new SubscriptionExpirationReminderEmail(
                        subscription.User.Email,
                        subscription.User.Username,
                        planName,
                        periodEnd,
                        remainingDays,
                        ResolveSubscriptionUrl()),
                    cancellationToken);

                subscription.ExpirationReminderPeriodEnd = periodEnd;
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Sent subscription period reminder for subscription {SubscriptionId}, ending {PeriodEnd}.",
                    subscription.Id,
                    periodEnd);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Could not send subscription period reminder for subscription {SubscriptionId}; it will be retried.",
                    subscription.Id);
            }
        }
    }

    private string ResolveSubscriptionUrl()
    {
        var configuredUrl = configuration["Frontend:BaseUrl"]
            ?? configuration["Facebook:FrontendUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUrl)
            && Uri.TryCreate(configuredUrl, UriKind.Absolute, out var frontendUri))
            return new Uri(frontendUri, "/subscription").ToString();

        if (Uri.TryCreate(payMongoOptions.Value.SuccessUrl, UriKind.Absolute, out var fallbackUri))
            return $"{fallbackUri.GetLeftPart(UriPartial.Authority)}/subscription";

        return "http://localhost:5092/subscription";
    }
}
