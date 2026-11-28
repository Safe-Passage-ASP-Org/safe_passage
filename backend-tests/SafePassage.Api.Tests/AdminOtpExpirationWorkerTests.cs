using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class AdminOtpExpirationWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_SendsWarningOnceWhenOtpExpiresWithin24Hours()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();
        var token = await fixture.SeedTokenAsync(admin.Id, DateTimeOffset.UtcNow.AddHours(12), usedAt: null);

        var firstSent = await fixture.Worker.RunOnceAsync();
        var secondSent = await fixture.Worker.RunOnceAsync();

        Assert.Equal(1, firstSent);
        Assert.Equal(0, secondSent);
        var notice = Assert.Single(fixture.EmailSender.Notices);
        Assert.False(notice.IsExpired);
        Assert.Equal(admin.Email, notice.Email);

        var reloaded = await fixture.DbContext.AdminLoginTokens.AsNoTracking().SingleAsync(item => item.Id == token.Id);
        Assert.NotNull(reloaded.ExpirationWarningSentAt);
        Assert.Null(reloaded.ExpiredNoticeSentAt);
    }

    [Fact]
    public async Task RunOnceAsync_SendsExpiredNoticeOnceWhenOtpExpired()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();
        var token = await fixture.SeedTokenAsync(admin.Id, DateTimeOffset.UtcNow.AddMinutes(-5), usedAt: null);

        var firstSent = await fixture.Worker.RunOnceAsync();
        var secondSent = await fixture.Worker.RunOnceAsync();

        Assert.Equal(1, firstSent);
        Assert.Equal(0, secondSent);
        var notice = Assert.Single(fixture.EmailSender.Notices);
        Assert.True(notice.IsExpired);

        var reloaded = await fixture.DbContext.AdminLoginTokens.AsNoTracking().SingleAsync(item => item.Id == token.Id);
        Assert.NotNull(reloaded.ExpiredNoticeSentAt);
    }

    [Fact]
    public async Task RunOnceAsync_IgnoresRevokedTokens()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var admin = await fixture.SeedAdminAsync();
        await fixture.SeedTokenAsync(admin.Id, DateTimeOffset.UtcNow.AddHours(6), usedAt: DateTimeOffset.UtcNow);

        var sent = await fixture.Worker.RunOnceAsync();

        Assert.Equal(0, sent);
        Assert.Empty(fixture.EmailSender.Notices);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider serviceProvider;

        public ApplicationDbContext DbContext { get; }
        public CapturingExpirationEmailSender EmailSender { get; } = new();
        public AdminOtpExpirationWorker Worker { get; }

        private TestFixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            ApplicationDbContext dbContext,
            CapturingExpirationEmailSender emailSender,
            AdminOtpExpirationWorker worker)
        {
            this.connection = connection;
            this.serviceProvider = serviceProvider;
            DbContext = dbContext;
            EmailSender = emailSender;
            Worker = worker;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var emailSender = new CapturingExpirationEmailSender();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(connection).UseOpenIddict());
            services.AddSingleton<IAdminOtpExpirationEmailSender>(emailSender);

            var provider = services.BuildServiceProvider();
            var dbContext = provider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.MigrateAsync();

            var worker = new AdminOtpExpirationWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AdminOtpExpirationOptions { Enabled = true }),
                new ConfigurationBuilder().Build(),
                NullLogger<AdminOtpExpirationWorker>.Instance);

            return new TestFixture(connection, provider, dbContext, emailSender, worker);
        }

        public async Task<User> SeedAdminAsync()
        {
            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "admin-expiry",
                FullName = "Admin Expiry",
                Email = $"admin-expiry-{Guid.NewGuid():N}@example.com",
                PasswordHash = "unused",
                Role = "Admin",
                IsActive = true,
                IsEmailVerified = true
            };
            DbContext.Users.Add(admin);
            await DbContext.SaveChangesAsync();
            return admin;
        }

        public async Task<AdminLoginToken> SeedTokenAsync(Guid userId, DateTimeOffset expiresAt, DateTimeOffset? usedAt)
        {
            var token = new AdminLoginToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-6),
                ExpiresAt = expiresAt,
                UsedAt = usedAt
            };
            DbContext.AdminLoginTokens.Add(token);
            await DbContext.SaveChangesAsync();
            return token;
        }

        public async ValueTask DisposeAsync()
        {
            await serviceProvider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed record SentNotice(string Email, string Username, DateTimeOffset ExpiresAt, bool IsExpired, string AdminLoginUrl);

    private sealed class CapturingExpirationEmailSender : IAdminOtpExpirationEmailSender
    {
        public List<SentNotice> Notices { get; } = [];

        public Task SendAdminOtpExpirationNoticeAsync(
            string email,
            string username,
            DateTimeOffset expiresAt,
            bool isExpired,
            string adminLoginUrl,
            CancellationToken cancellationToken)
        {
            Notices.Add(new SentNotice(email, username, expiresAt, isExpired, adminLoginUrl));
            return Task.CompletedTask;
        }
    }
}
