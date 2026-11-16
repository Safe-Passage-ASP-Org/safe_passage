using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Models;

namespace SafePassage.Api.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AccountActivationToken> AccountActivationTokens => Set<AccountActivationToken>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<SecurityPolicy> SecurityPolicies => Set<SecurityPolicy>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<MfaSession> MfaSessions => Set<MfaSession>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<ApplicationUserAccess> ApplicationUserAccesses => Set<ApplicationUserAccess>();
    public DbSet<ApplicationGroupAccess> ApplicationGroupAccesses => Set<ApplicationGroupAccess>();
    public DbSet<ApplicationAccessRequest> ApplicationAccessRequests => Set<ApplicationAccessRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.HasIndex(user => user.Username).IsUnique();
            entity.HasIndex(user => user.Email).IsUnique();
            entity.HasIndex(user => user.FacebookUserId).IsUnique()
                .HasFilter("\"FacebookUserId\" IS NOT NULL");
            entity.HasIndex(user => user.GitHubUserId).IsUnique()
                .HasFilter("\"GitHubUserId\" IS NOT NULL");
            entity.HasIndex(user => user.GitLabUserId).IsUnique()
                .HasFilter("\"GitLabUserId\" IS NOT NULL");
            entity.Property(user => user.Username).HasMaxLength(100).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(user => user.Email).HasMaxLength(320).IsRequired();
            entity.Property(user => user.FullName).HasMaxLength(200);
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
            entity.Property(log => log.Action).HasMaxLength(120).IsRequired();
            entity.Property(log => log.Resource).HasMaxLength(512).IsRequired();
            entity.Property(log => log.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(log => log.User).WithMany(user => user.ActivityLogs)
                .HasForeignKey(log => log.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MfaSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.HasIndex(session => session.SessionToken).IsUnique();
            entity.HasIndex(session => session.ExpiresAt);
            entity.Property(session => session.SessionToken).HasMaxLength(512).IsRequired();
            entity.Property(session => session.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(session => session.User).WithMany(user => user.MfaSessions)
                .HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.Device).WithMany(device => device.MfaSessions)
                .HasForeignKey(session => session.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
