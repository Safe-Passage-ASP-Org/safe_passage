using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed class AdminLoginOptions
{
    public const string SectionName = "AdminLogin";
    public string? PublicUrl { get; set; }
}

public interface IAdminEmailLoginService
{
    Task RequestOtpAsync(string email, CancellationToken cancellationToken);
    Task<AuthFlowResult<LoginResponse>> CompleteOtpAsync(string email, string code, CancellationToken cancellationToken);
}

public sealed class AdminEmailLoginService(
    ApplicationDbContext dbContext,
    IActivationEmailSender emailSender,
    IJwtTokenService jwtTokenService,
    JwtSettings jwtSettings,
    ILogger<AdminEmailLoginService> logger) : IAdminEmailLoginService
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromDays(7);
    private const int MaximumFailedAttempts = 5;

    public async Task RequestOtpAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate =>
            candidate.Email.ToUpper() == normalizedEmail
            && candidate.IsActive
            && (candidate.Role.ToUpper() == "ADMIN"
                || candidate.Role.ToUpper() == "ADMINISTRATOR"
                || candidate.Role.ToUpper() == "SUPER ADMIN"), cancellationToken);
        if (user is null) return;

        var now = DateTimeOffset.UtcNow;
        await dbContext.AdminLoginTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.UsedAt, (DateTimeOffset?)now),
                cancellationToken);

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var otp = new AdminLoginToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashOtp(normalizedEmail, code),
            CreatedAt = now,
            ExpiresAt = now.Add(OtpLifetime)
        };
        dbContext.AdminLoginTokens.Add(otp);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await emailSender.SendSignInOtpAsync(
                user.Email,
                user.FullName ?? user.Username,
                code,
                (int)OtpLifetime.TotalMinutes,
                isAdminLogin: true,
                cancellationToken);
        }
        catch (Exception exception)
        {
            otp.UsedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            logger.LogError(exception, "Could not deliver an administrator sign-in OTP for user {UserId}.", user.Id);
            throw;
        }
    }

    public async Task<AuthFlowResult<LoginResponse>> CompleteOtpAsync(
        string email,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(code)
            || code.Length != 6
            || code.Any(character => character is < '0' or > '9'))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        var normalizedEmail = NormalizeEmail(email);
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate =>
            candidate.Email.ToUpper() == normalizedEmail
            && candidate.IsActive
            && (candidate.Role.ToUpper() == "ADMIN"
                || candidate.Role.ToUpper() == "ADMINISTRATOR"
                || candidate.Role.ToUpper() == "SUPER ADMIN"), cancellationToken);
        if (user is null) return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        var now = DateTimeOffset.UtcNow;
        var supportsDateTimeOffsetComparison = !dbContext.Database.IsSqlite();
        var otpQuery = dbContext.AdminLoginTokens
            .Where(token => token.UserId == user.Id
                && token.UsedAt == null
                && token.FailedAttempts < MaximumFailedAttempts)
            .AsQueryable();
        if (supportsDateTimeOffsetComparison)
            otpQuery = otpQuery.Where(token => token.ExpiresAt > now);
        var otpCandidates = await otpQuery.ToListAsync(cancellationToken);
        var otp = otpCandidates
            .Where(token => token.ExpiresAt > now)
            .OrderByDescending(token => token.CreatedAt)
            .FirstOrDefault();
        if (otp is null || otp.ExpiresAt <= now)
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        var suppliedHash = Convert.FromHexString(HashOtp(normalizedEmail, code.Trim()));
        var expectedHash = Convert.FromHexString(otp.TokenHash);
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
        {
            var failedAttemptQuery = dbContext.AdminLoginTokens
                .Where(token => token.Id == otp.Id
                    && token.UsedAt == null
                    && token.FailedAttempts < MaximumFailedAttempts)
                .AsQueryable();
            if (supportsDateTimeOffsetComparison)
                failedAttemptQuery = failedAttemptQuery.Where(token => token.ExpiresAt > now);
            await failedAttemptQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(token => token.FailedAttempts, token => token.FailedAttempts + 1)
                    .SetProperty(token => token.UsedAt,
                        token => token.FailedAttempts >= MaximumFailedAttempts - 1 ? (DateTimeOffset?)now : token.UsedAt),
                    cancellationToken);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var consumeQuery = dbContext.AdminLoginTokens
            .Where(token => token.Id == otp.Id
                && token.UserId == user.Id
                && token.UsedAt == null
                && token.FailedAttempts < MaximumFailedAttempts)
            .AsQueryable();
        if (supportsDateTimeOffsetComparison)
            consumeQuery = consumeQuery.Where(token => token.ExpiresAt > now);
        var consumed = await consumeQuery
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.UsedAt, (DateTimeOffset?)now),
                cancellationToken);
        if (consumed != 1)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);
        }

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Timestamp = now,
            Action = "Admin Email OTP",
            Resource = "Single-use email OTP verified; administrator signed in",
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var accessToken = jwtTokenService.CreateAccessToken(user);
        return AuthFlowResult<LoginResponse>.Success(new(
            false, null, null, null, null, accessToken, "Bearer", "Administrator sign-in successful."));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    private string HashOtp(string normalizedEmail, string code) => Convert.ToHexString(
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(jwtSettings.SigningKey),
            Encoding.UTF8.GetBytes($"{normalizedEmail}:{code}")));

    private static bool IsAdministrator(string role) =>
        string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "Super Admin", StringComparison.OrdinalIgnoreCase);
}
