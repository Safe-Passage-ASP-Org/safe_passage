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
    Task<bool> HasActiveOtpAsync(string email, CancellationToken cancellationToken);
    Task RequestOtpAsync(string email, CancellationToken cancellationToken);
    Task RequestOtpAsync(string email, bool forceResend, CancellationToken cancellationToken);
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

    public async Task<bool> HasActiveOtpAsync(string email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var user = await FindActiveAdministratorAsync(email, cancellationToken);
        if (user is null)
            return false;

        var now = DateTimeOffset.UtcNow;
        var activeExpirations = await dbContext.AdminLoginTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null)
            .Select(token => token.ExpiresAt)
            .ToListAsync(cancellationToken);
        return activeExpirations.Any(expiresAt => expiresAt > now);
    }

    public Task RequestOtpAsync(string email, CancellationToken cancellationToken) =>
        RequestOtpAsync(email, forceResend: true, cancellationToken);

    public async Task RequestOtpAsync(string email, bool forceResend, CancellationToken cancellationToken)
    {
        var user = await FindActiveAdministratorAsync(email, cancellationToken);
        if (user is null) return;

        var normalizedEmail = NormalizeEmail(email);
        var now = DateTimeOffset.UtcNow;
        if (!forceResend)
        {
            var activeExpirations = await dbContext.AdminLoginTokens
                .Where(token => token.UserId == user.Id && token.UsedAt == null)
                .Select(token => token.ExpiresAt)
                .ToListAsync(cancellationToken);
            if (activeExpirations.Any(expiresAt => expiresAt > now)) return;
        }

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
        var user = await FindActiveAdministratorAsync(email, cancellationToken);
        if (user is null) return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        var now = DateTimeOffset.UtcNow;
        var activeTokens = await dbContext.AdminLoginTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null)
            .ToListAsync(cancellationToken);
        var otp = activeTokens
            .Where(token => token.ExpiresAt > now)
            .OrderByDescending(token => token.CreatedAt)
            .FirstOrDefault();
        if (otp is null) return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        var suppliedHash = Convert.FromHexString(HashOtp(normalizedEmail, code.Trim()));
        var expectedHash = Convert.FromHexString(otp.TokenHash);
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
            return AuthFlowResult<LoginResponse>.Failed(AuthFlowFailure.InvalidCredentials);

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Timestamp = now,
            Action = "Admin Email OTP",
            Resource = "Administrator email OTP verified; administrator signed in",
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var accessToken = jwtTokenService.CreateAccessToken(user);
        return AuthFlowResult<LoginResponse>.Success(new(
            false, null, null, null, null, accessToken, "Bearer", "Administrator sign-in successful."));
    }

    private async Task<User?> FindActiveAdministratorAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        return await dbContext.Users.SingleOrDefaultAsync(candidate =>
            candidate.Email.ToUpper() == normalizedEmail
            && candidate.IsActive
            && candidate.IsEmailVerified
            && (candidate.Role.ToUpper() == "ADMIN"
                || candidate.Role.ToUpper() == "ADMINISTRATOR"
                || candidate.Role.ToUpper() == "SUPER ADMIN"), cancellationToken);
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
