using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class ProfileSecretKeyTests
{
    [Theory]
    [InlineData("Admin")]
    [InlineData("User")]
    public async Task GenerateSecretKey_AllowsBothAdminsAndUsers_AndStoresOnlyHash(string role)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var user = await fixture.SeedUserAsync(role);
        var controller = fixture.CreateController(user);

        var actionResult = await controller.GenerateSecretKey(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var payload = Assert.IsType<ProfileController.AccountSecretKeyResponse>(ok.Value);

        Assert.StartsWith("sp_sk_", payload.SecretKey, StringComparison.Ordinal);
        Assert.Equal(payload.SecretKey[..12], payload.Prefix);

        var reloaded = await fixture.DbContext.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.SecretKey)));
        Assert.Equal(expectedHash, reloaded.AccountSecretKeyHash);
        Assert.NotEqual(payload.SecretKey, reloaded.AccountSecretKeyHash);
        Assert.Equal(payload.Prefix, reloaded.AccountSecretKeyPrefix);
        Assert.NotNull(reloaded.AccountSecretKeyCreatedAt);

        var profileAction = await controller.Get(CancellationToken.None);
        var profileOk = Assert.IsType<OkObjectResult>(profileAction.Result);
        var profile = Assert.IsType<ProfileController.ProfileResponse>(profileOk.Value);
        Assert.True(profile.HasAccountSecretKey);
        Assert.Equal(payload.Prefix, profile.AccountSecretKeyPrefix);
    }

    [Fact]
    public async Task RegenerateAndRevokeSecretKey_UpdatesAndClearsStoredKey()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var user = await fixture.SeedUserAsync("User");
        var controller = fixture.CreateController(user);

        var first = Assert.IsType<ProfileController.AccountSecretKeyResponse>(
            Assert.IsType<OkObjectResult>((await controller.GenerateSecretKey(CancellationToken.None)).Result).Value);
        var second = Assert.IsType<ProfileController.AccountSecretKeyResponse>(
            Assert.IsType<OkObjectResult>((await controller.GenerateSecretKey(CancellationToken.None)).Result).Value);

        Assert.NotEqual(first.SecretKey, second.SecretKey);
        var afterRegen = await fixture.DbContext.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(second.SecretKey))),
            afterRegen.AccountSecretKeyHash);

        var revokeOk = Assert.IsType<OkObjectResult>((await controller.RevokeSecretKey(CancellationToken.None)).Result);
        var profile = Assert.IsType<ProfileController.ProfileResponse>(revokeOk.Value);
        Assert.False(profile.HasAccountSecretKey);
        Assert.Null(profile.AccountSecretKeyPrefix);
        Assert.Null(profile.AccountSecretKeyCreatedAt);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());

        public ApplicationDbContext DbContext { get; }

        private TestFixture(SqliteConnection connection, ApplicationDbContext dbContext)
        {
            this.connection = connection;
            DbContext = dbContext;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .UseOpenIddict()
                .Options;
            var dbContext = new ApplicationDbContext(options);
            await dbContext.Database.MigrateAsync();
            return new TestFixture(connection, dbContext);
        }

        public async Task<User> SeedUserAsync(string role)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = $"{role.ToLowerInvariant()}-user-{Guid.NewGuid():N}",
                FullName = $"{role} User",
                Email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com",
                PasswordHash = "unused",
                Role = role,
                IsActive = true,
                IsEmailVerified = true
            };
            DbContext.Users.Add(user);
            await DbContext.SaveChangesAsync();
            return user;
        }

        public ProfileController CreateController(User user)
        {
            var controller = new ProfileController(
                DbContext,
                cache,
                new NoopEmailSender(),
                new ConfigurationBuilder().Build(),
                NullLogger<ProfileController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                            new Claim(ClaimTypes.Role, user.Role)
                        ], "Test"))
                    }
                }
            };
            return controller;
        }

        public async ValueTask DisposeAsync()
        {
            cache.Dispose();
            await DbContext.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class NoopEmailSender : IActivationEmailSender
    {
        public Task SendActivationEmailAsync(string email, string username, string activationUrl, bool requiresPasswordSetup, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAdminEmailChangeVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendBackupEmailVerificationAsync(string email, string username, string verificationUrl, string otpCode, int lifetimeMinutes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendSubscriptionReceiptAsync(SubscriptionReceiptEmail receipt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendSignInOtpAsync(string email, string username, string otpCode, int lifetimeMinutes, bool isAdminLogin, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
