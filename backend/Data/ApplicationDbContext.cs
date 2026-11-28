using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Data;

public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    IUserNotificationPushSender? notificationPushSender = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AccountActivationToken> AccountActivationTokens => Set<AccountActivationToken>();
    public DbSet<AdminLoginToken> AdminLoginTokens => Set<AdminLoginToken>();
    public DbSet<AdminEmailChangeToken> AdminEmailChangeTokens => Set<AdminEmailChangeToken>();
    public DbSet<BackupEmailVerificationToken> BackupEmailVerificationTokens => Set<BackupEmailVerificationToken>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<SecurityPolicy> SecurityPolicies => Set<SecurityPolicy>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<MfaSession> MfaSessions => Set<MfaSession>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<ApplicationUserAccess> ApplicationUserAccesses => Set<ApplicationUserAccess>();
    public DbSet<ApplicationGroupAccess> ApplicationGroupAccesses => Set<ApplicationGroupAccess>();
    public DbSet<ApplicationAccessRequest> ApplicationAccessRequests => Set<ApplicationAccessRequest>();
    public DbSet<UserSubscription> UserSubscriptions => Set<UserSubscription>();
    public DbSet<SubscriptionWebhookEvent> SubscriptionWebhookEvents => Set<SubscriptionWebhookEvent>();
    public DbSet<VaultSite> VaultSites => Set<VaultSite>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareActivityNotifications();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        var notifications = PrepareActivityNotifications();
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await DeliverNotificationsAsync(notifications, cancellationToken);
        return saved;
    }

    private List<UserNotification> PrepareActivityNotifications()
    {
        var notifications = new List<UserNotification>();
        var newActivities = ChangeTracker.Entries<ActivityLog>()
            .Where(entry => entry.State == EntityState.Added)
            .ToList();
        foreach (var entry in newActivities)
        {
            var activity = entry.Entity;
            if (!TryGetNotification(activity, out var category, out var title, out var message, out var href))
                continue;

            var userId = activity.TargetUserId ?? activity.UserId;
            if (userId is null)
                continue;

            var notification = new UserNotification
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                Category = category,
                Title = title,
                Message = message,
                Href = href,
                CreatedAt = activity.Timestamp
            };
            UserNotifications.Add(notification);
            notifications.Add(notification);
        }

        return notifications;
    }

    private async Task DeliverNotificationsAsync(
        IReadOnlyCollection<UserNotification> notifications,
        CancellationToken cancellationToken)
    {
        if (notificationPushSender is null || notifications.Count == 0)
            return;

        foreach (var group in notifications.GroupBy(notification => notification.UserId))
        {
            var tokens = await Devices.AsNoTracking()
                .Where(device => device.UserId == group.Key
                    && device.Status == DeviceStatus.Active
                    && device.PushToken != null)
                .Select(device => device.PushToken!)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var notification in group)
            {
                await notificationPushSender.SendAsync(
                    tokens,
                    notification.Title,
                    notification.Message,
                    notification.Id,
                    notification.Href,
                    cancellationToken);
            }
        }
    }

    private static bool TryGetNotification(
        ActivityLog activity,
        out string category,
        out string title,
        out string message,
        out string? href)
    {
        category = "Security";
        title = activity.Action;
        message = activity.Resource;
        href = "/activity-logs";

        var action = activity.Action.Trim().ToLowerInvariant();
        if (action.Contains("login") && activity.Status == ActivityLogStatus.Denied)
        {
            title = "Sign-in blocked";
            message = string.IsNullOrWhiteSpace(activity.Reason) ? activity.Resource : activity.Reason;
            href = "/notifications";
            return true;
        }

        if (action.Contains("security alert") || action.Contains("device trust blocked"))
        {
            title = "Security alert";
            message = string.IsNullOrWhiteSpace(activity.Reason) ? activity.Resource : activity.Reason;
            href = "/notifications";
            return true;
        }

        if (action.Contains("browser app access notification"))
        {
            title = "Sign-in approval failed";
            message = "A browser sign-in request could not be sent to your trusted device.";
            href = "/notifications";
            return true;
        }

        if (action.Contains("password change"))
        {
            category = "Account";
            title = "Password changed";
            message = "Your account password was changed.";
            href = "/settings";
            return true;
        }

        if (action.Contains("backup email") || action.Contains("admin email changed"))
        {
            category = "Account";
            title = "Account email updated";
            message = "An email address on your account was updated.";
            href = "/settings";
            return true;
        }

        if (action.Contains("profile updated"))
        {
            category = "Account";
            title = "Profile updated";
            message = "Your account profile was updated.";
            href = "/settings";
            return true;
        }

        if (action.Contains("device trusted") || action.Contains("device revoked")
            || action.Contains("device registered") || action.Contains("first device enrollment")
            || action.Contains("device posture updated") || action.Contains("device signing key enrolled")
            || action.Contains("signing key recovery approved"))
        {
            category = "Device";
            title = activity.Action;
            message = activity.Resource;
            href = "/devices";
            return true;
        }

        if (action.Contains("subscription activated") || action.Contains("subscription cancelled"))
        {
            category = "Subscription";
            title = activity.Action;
            message = activity.Resource;
            href = "/subscription";
            return true;
        }

        if (action.Contains("application access verification") && activity.Status == ActivityLogStatus.Denied)
        {
            title = "Application access blocked";
            message = "A sign-in to an application was blocked by your access or security policy.";
            href = "/activity-logs";
            return true;
        }

        return false;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.HasIndex(user => user.Username).IsUnique();
            entity.HasIndex(user => user.Email).IsUnique();
            entity.HasIndex(user => user.BackupEmail).IsUnique()
                .HasFilter("\"BackupEmail\" IS NOT NULL");
            entity.HasIndex(user => user.FacebookUserId).IsUnique()
                .HasFilter("\"FacebookUserId\" IS NOT NULL");
            entity.HasIndex(user => user.GitHubUserId).IsUnique()
                .HasFilter("\"GitHubUserId\" IS NOT NULL");
            entity.HasIndex(user => user.GitLabUserId).IsUnique()
                .HasFilter("\"GitLabUserId\" IS NOT NULL");
            entity.Property(user => user.Username).HasMaxLength(100).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(user => user.Email).HasMaxLength(320).IsRequired();
            entity.Property(user => user.BackupEmail).HasMaxLength(320);
            entity.Property(user => user.FullName).HasMaxLength(200);
            entity.Property(user => user.ProfileImageData).HasMaxLength(1_400_000);
            entity.Property(user => user.PhoneNumber).HasMaxLength(32);
            entity.Property(user => user.Role).HasMaxLength(64).IsRequired();
            entity.Property(user => user.FacebookUserId).HasMaxLength(128);
            entity.Property(user => user.GitHubUserId).HasMaxLength(128);
            entity.Property(user => user.GitLabUserId).HasMaxLength(128);
        });

        modelBuilder.Entity<AccountActivationToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasOne(token => token.User).WithMany()
                .HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminLoginToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.FailedAttempts).HasDefaultValue(0);
            entity.HasOne(token => token.User).WithMany()
                .HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminEmailChangeToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.Property(token => token.NewEmail).HasMaxLength(320).IsRequired();
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.OtpHash).HasMaxLength(64).IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupEmailVerificationToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.Property(token => token.NewEmail).HasMaxLength(320).IsRequired();
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.OtpHash).HasMaxLength(64).IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasKey(group => group.Id);
            entity.HasIndex(group => group.Name).IsUnique();
            entity.Property(group => group.Name).HasMaxLength(120).IsRequired();
            entity.Property(group => group.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.HasKey(link => new { link.UserId, link.GroupId });
            entity.HasOne(link => link.User).WithMany(user => user.UserGroups)
                .HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.Group).WithMany(group => group.UserGroups)
                .HasForeignKey(link => link.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(device => device.Id);
            entity.HasIndex(device => new { device.UserId, device.DeviceId }).IsUnique();
            entity.Property(device => device.DeviceId).HasMaxLength(256).IsRequired();
            entity.Property(device => device.DeviceName).HasMaxLength(200).IsRequired();
            entity.Property(device => device.DeviceType).HasMaxLength(100).IsRequired();
            entity.Property(device => device.OS).HasMaxLength(100).IsRequired();
            entity.Property(device => device.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(device => device.TrustStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(device => device.SigningPublicKey).HasMaxLength(128);
            entity.Property(device => device.PendingSigningPublicKey).HasMaxLength(128);
            entity.HasOne(device => device.User).WithMany(user => user.Devices)
                .HasForeignKey(device => device.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Application>(entity =>
        {
            entity.HasKey(application => application.Id);
            entity.HasIndex(application => application.Name).IsUnique();
            entity.Property(application => application.Name).HasMaxLength(200).IsRequired();
            entity.Property(application => application.Description).HasMaxLength(2000);
            entity.Property(application => application.LaunchUrl).HasMaxLength(2048);
            entity.Property(application => application.Category).HasMaxLength(64).IsRequired();
            entity.Property(application => application.AutoFillUsername).HasMaxLength(320).IsRequired();
            entity.Property(application => application.EncryptedAutoFillPassword).HasMaxLength(2048).IsRequired();
            entity.HasIndex(application => application.ClientId).IsUnique();
            entity.Property(application => application.ClientId).HasMaxLength(200);
            entity.Property(application => application.ClientType).HasMaxLength(50).IsRequired();
            entity.Property(application => application.RedirectUris).HasMaxLength(2000);
            entity.Property(application => application.PostLogoutRedirectUris).HasMaxLength(2000);
            entity.Property(application => application.AllowedScopes).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<ApplicationUserAccess>(entity =>
        {
            entity.HasKey(link => new { link.ApplicationId, link.UserId });
            entity.HasOne(link => link.Application).WithMany(application => application.UserAccess)
                .HasForeignKey(link => link.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.User).WithMany(user => user.ApplicationAccess)
                .HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationGroupAccess>(entity =>
        {
            entity.HasKey(link => new { link.ApplicationId, link.GroupId });
            entity.HasOne(link => link.Application).WithMany(application => application.GroupAccess)
                .HasForeignKey(link => link.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.Group).WithMany(group => group.ApplicationAccess)
                .HasForeignKey(link => link.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationAccessRequest>(entity =>
        {
            entity.HasKey(request => request.Id);
            entity.HasIndex(request => new { request.ApplicationId, request.UserId })
                .IsUnique()
                .HasFilter("\"Status\" = 'Pending'");
            entity.HasIndex(request => new { request.UserId, request.RequestedAt });
            entity.Property(request => request.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken();
            entity.Property(request => request.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(request => request.DecisionNote).HasMaxLength(1000);
            entity.HasOne(request => request.Application).WithMany(application => application.AccessRequests)
                .HasForeignKey(request => request.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(request => request.User).WithMany(user => user.ApplicationAccessRequests)
                .HasForeignKey(request => request.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SecurityPolicy>(entity =>
        {
            entity.HasKey(policy => policy.Id);
            entity.HasIndex(policy => policy.PolicyName).IsUnique();
            entity.Property(policy => policy.PolicyName).HasMaxLength(200).IsRequired();
            entity.Property(policy => policy.PolicyType).HasMaxLength(100).IsRequired();
            entity.Property(policy => policy.RulesJson).IsRequired();
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.HasIndex(log => new { log.UserId, log.Timestamp });
            entity.HasIndex(log => new { log.TargetUserId, log.Timestamp });
            entity.Property(log => log.Action).HasMaxLength(120).IsRequired();
            entity.Property(log => log.Resource).HasMaxLength(512).IsRequired();
            entity.Property(log => log.DeviceId).HasMaxLength(256);
            entity.Property(log => log.Reason).HasMaxLength(512);
            entity.Property(log => log.IpAddress).HasMaxLength(64);
            entity.Property(log => log.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(log => log.User).WithMany(user => user.ActivityLogs)
                .HasForeignKey(log => log.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserNotification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.HasIndex(notification => new { notification.UserId, notification.CreatedAt });
            entity.Property(notification => notification.Category).HasMaxLength(64).IsRequired();
            entity.Property(notification => notification.Title).HasMaxLength(120).IsRequired();
            entity.Property(notification => notification.Message).HasMaxLength(2000).IsRequired();
            entity.Property(notification => notification.Href).HasMaxLength(512);
            entity.HasOne<User>().WithMany().HasForeignKey(notification => notification.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.HasIndex(session => session.TokenJti).IsUnique();
            entity.HasIndex(session => new { session.UserId, session.DeviceId, session.RevokedAt });
            entity.Property(session => session.TokenJti).HasMaxLength(64).IsRequired();
            entity.Property(session => session.DeviceId).HasMaxLength(256);
            entity.Property(session => session.RevokedReason).HasMaxLength(128);
            entity.HasOne(session => session.User).WithMany()
                .HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MfaSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.HasIndex(session => session.SessionToken).IsUnique();
            entity.HasIndex(session => session.ExpiresAt);
            entity.Property(session => session.SessionToken).HasMaxLength(512).IsRequired();
            entity.Property(session => session.Purpose).HasMaxLength(64).IsRequired();
            entity.Property(session => session.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(session => session.User).WithMany(user => user.MfaSessions)
                .HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.Device).WithMany(device => device.MfaSessions)
                .HasForeignKey(session => session.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserSubscription>(entity =>
        {
            entity.HasKey(subscription => subscription.Id);
            entity.HasIndex(subscription => subscription.PayPalSubscriptionId).IsUnique();
            entity.HasIndex(subscription => new { subscription.UserId, subscription.Status });
            entity.Property(subscription => subscription.PayPalSubscriptionId).HasMaxLength(128).IsRequired();
            entity.Property(subscription => subscription.PayPalPlanId).HasMaxLength(128).IsRequired();
            entity.Property(subscription => subscription.PlanCode).HasMaxLength(64).IsRequired();
            entity.Property(subscription => subscription.Status).HasConversion<string>().HasMaxLength(32).IsConcurrencyToken();
            entity.Property(subscription => subscription.ApprovalUrl).HasMaxLength(2048);
            entity.Property(subscription => subscription.ExpirationReminderPeriodEnd);
            entity.HasOne(subscription => subscription.User).WithMany(user => user.Subscriptions)
                .HasForeignKey(subscription => subscription.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionWebhookEvent>(entity =>
        {
            entity.HasKey(evt => evt.Id);
            entity.HasIndex(evt => evt.PayPalEventId).IsUnique();
            entity.Property(evt => evt.PayPalEventId).HasMaxLength(128).IsRequired();
            entity.Property(evt => evt.EventType).HasMaxLength(128).IsRequired();
            entity.Property(evt => evt.PayPalSubscriptionId).HasMaxLength(128);
            entity.Property(evt => evt.PayloadSummary).HasMaxLength(1000);
        });

        modelBuilder.Entity<VaultSite>(entity =>
        {
            entity.HasKey(site => site.Id);
            entity.HasIndex(site => new { site.UserId, site.Origin }).IsUnique();
            entity.HasIndex(site => new { site.VerificationStatus, site.CreatedAt });
            entity.Property(site => site.Name).HasMaxLength(200).IsRequired();
            entity.Property(site => site.LoginUrl).HasMaxLength(2048).IsRequired();
            entity.Property(site => site.Origin).HasMaxLength(512).IsRequired();
            entity.Property(site => site.Category).HasMaxLength(64).IsRequired();
            entity.Property(site => site.Description).HasMaxLength(2000).IsRequired();
            entity.Property(site => site.AutoFillUsername).HasMaxLength(320).IsRequired();
            entity.Property(site => site.EncryptedPassword).HasMaxLength(2048).IsRequired();
            entity.Property(site => site.EncryptedTotpSecret).HasMaxLength(2048).IsRequired();
            entity.Property(site => site.VerificationStatus).HasMaxLength(32).IsRequired();
            entity.HasOne(site => site.User).WithMany(user => user.VaultSites)
                .HasForeignKey(site => site.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
