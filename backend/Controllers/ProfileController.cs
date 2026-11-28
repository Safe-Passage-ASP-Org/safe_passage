using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/profile")]
public sealed class ProfileController(
    ApplicationDbContext dbContext,
    IMemoryCache cache,
    IActivationEmailSender emailSender,
    IConfiguration configuration,
    ILogger<ProfileController> logger) : ControllerBase
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private const int MaxProfileImageBytes = 1024 * 1024;

    [HttpGet]
    public async Task<ActionResult<ProfileResponse>> Get(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        return Ok(ToProfile(user));
    }

    [HttpPut]
    public async Task<ActionResult<ProfileResponse>> Update(
        [FromBody] UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            return Problem(statusCode: 400, title: "Invalid name", detail: "Enter a name up to 200 characters long.");

        user.FullName = name;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = user.Id, Timestamp = DateTimeOffset.UtcNow,
            Action = "Profile updated", Resource = "Profile", Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToProfile(user));
    }

    [HttpPut("image")]
    public async Task<ActionResult<ProfileResponse>> UpdateImage(
        [FromBody] UpdateProfileImageRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        if (!TryValidateProfileImage(request.ImageData, out var error))
            return Problem(statusCode: 400, title: "Invalid profile picture", detail: error);

        user.ProfileImageData = request.ImageData;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = user.Id, Timestamp = DateTimeOffset.UtcNow,
            Action = "Profile picture updated", Resource = "Profile", Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToProfile(user));
    }

    [HttpDelete("image")]
    public async Task<ActionResult<ProfileResponse>> DeleteImage(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        user.ProfileImageData = null;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = user.Id, Timestamp = DateTimeOffset.UtcNow,
            Action = "Profile picture removed", Resource = "Profile", Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToProfile(user));
    }

    [HttpPost("secret-key")]
    public async Task<ActionResult<AccountSecretKeyResponse>> GenerateSecretKey(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        var rawSecretKey = "sp_sk_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;

        user.AccountSecretKeyHash = Hash(rawSecretKey);
        user.AccountSecretKeyPrefix = rawSecretKey[..12];
        user.AccountSecretKeyCreatedAt = now;

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Timestamp = now,
            Action = "Account secret key generated",
            Resource = "Profile",
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new AccountSecretKeyResponse(rawSecretKey, user.AccountSecretKeyPrefix, now));
    }

    [HttpDelete("secret-key")]
    public async Task<ActionResult<ProfileResponse>> RevokeSecretKey(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();

        user.AccountSecretKeyHash = null;
        user.AccountSecretKeyPrefix = null;
        user.AccountSecretKeyCreatedAt = null;

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "Account secret key revoked",
            Resource = "Profile",
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(ToProfile(user));
    }

    [HttpPost("backup-email/request")]
    public async Task<IActionResult> RequestBackupEmail(
        [FromBody] BackupEmailRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320
            || !new EmailAddressAttribute().IsValid(email))
            return Problem(statusCode: 400, title: "Invalid email", detail: "Enter a valid email address.");
        email = email.ToLowerInvariant();
        if (string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: 400, title: "Email already in use", detail: "Your backup email must be different from your sign-in email.");
        if (await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id
            && (candidate.Email.ToUpper() == email.ToUpper()
                || (candidate.BackupEmail != null && candidate.BackupEmail.ToUpper() == email.ToUpper())), cancellationToken))
            return Problem(statusCode: 409, title: "Email already in use", detail: "That email address is already used by another account.");

        var requestLimitKey = "backup-email-request:" + user.Id;
        cache.TryGetValue(requestLimitKey, out int requestCount);
        if (requestCount >= 5)
            return Problem(statusCode: 429, title: "Too many requests", detail: "Please wait before requesting another verification email.");
        cache.Set(requestLimitKey, requestCount + 1, TimeSpan.FromHours(1));

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var now = DateTimeOffset.UtcNow;
        var pending = new BackupEmailVerificationToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, NewEmail = email,
            TokenHash = Hash(rawToken), OtpHash = Hash(otp), CreatedAt = now, ExpiresAt = now + TokenLifetime
        };
        await dbContext.BackupEmailVerificationTokens
            .Where(token => token.UserId == user.Id)
            .ExecuteDeleteAsync(cancellationToken);
        dbContext.BackupEmailVerificationTokens.Add(pending);
        await dbContext.SaveChangesAsync(cancellationToken);

        var configuredFrontend = configuration["Frontend:BaseUrl"]
            ?? configuration["AdminLogin:PublicUrl"]
            ?? configuration["AccountActivation:PublicUrl"]
            ?? "http://localhost:5092";
        var frontendBase = Uri.TryCreate(configuredFrontend, UriKind.Absolute, out var frontendUri)
            ? frontendUri.GetLeftPart(UriPartial.Authority)
            : "http://localhost:5092";
        var link = new Uri(new Uri(frontendBase.TrimEnd('/') + "/"), "verify-backup-email#backupEmailToken=" + Uri.EscapeDataString(rawToken)).ToString();
        try
        {
            await emailSender.SendBackupEmailVerificationAsync(email, user.Username, link, otp,
                (int)TokenLifetime.TotalMinutes, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.BackupEmailVerificationTokens.Remove(pending);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Could not deliver backup email verification to account {UserId}.", user.Id);
            return Problem(statusCode: 503, title: "Email delivery failed", detail: "The verification email could not be sent. Check the address and try again.");
        }
        return Accepted(new { message = "A verification link and 6-digit code were sent to the backup email address." });
    }

    [HttpPost("backup-email/verify-otp")]
    public async Task<IActionResult> VerifyBackupEmailOtp(
        [FromBody] BackupEmailOtpRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        var pending = await dbContext.BackupEmailVerificationTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null && token.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(token => token.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (pending is null) return Problem(statusCode: 401, title: "Verification expired", detail: "Request a new verification email.");
        if (pending.FailedAttempts >= 5) return Problem(statusCode: 429, title: "Too many attempts", detail: "Request a new verification email to try again.");
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length != 6 || !request.Code.All(char.IsDigit)
            || !HashesMatch(pending.OtpHash, Hash(request.Code)))
        {
            pending.FailedAttempts++;
            await dbContext.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: 401, title: "Invalid code", detail: "That verification code is incorrect.");
        }
        return await CompleteVerificationAsync(pending, user, cancellationToken);
    }

    [HttpPost("backup-email/verify-link")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyBackupEmailLink(
        [FromBody] BackupEmailLinkRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) return Problem(statusCode: 400, title: "Invalid link");
        var hash = Hash(request.Token);
        var pending = await dbContext.BackupEmailVerificationTokens.SingleOrDefaultAsync(
            token => token.TokenHash == hash && token.UsedAt == null && token.ExpiresAt > DateTimeOffset.UtcNow,
            cancellationToken);
        if (pending is null) return Problem(statusCode: 401, title: "Link expired", detail: "This verification link is invalid or expired. Request another verification email.");
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == pending.UserId && candidate.IsActive, cancellationToken);
        if (user is null) return Problem(statusCode: 401, title: "Link expired");
        return await CompleteVerificationAsync(pending, user, cancellationToken);
    }

    private async Task<IActionResult> CompleteVerificationAsync(
        BackupEmailVerificationToken pending, User user, CancellationToken cancellationToken)
    {
        var email = pending.NewEmail.Trim().ToLowerInvariant();
        if (await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id
            && (candidate.Email.ToUpper() == email.ToUpper()
                || (candidate.BackupEmail != null && candidate.BackupEmail.ToUpper() == email.ToUpper())), cancellationToken))
            return Problem(statusCode: 409, title: "Email already in use", detail: "That email address is already used by another account. Request another address.");
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.BackupEmailVerificationTokens
            .Where(token => token.Id == pending.Id && token.UsedAt == null && token.ExpiresAt > now && token.FailedAttempts < 5)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAt, now), cancellationToken);
        if (affected != 1) return Problem(statusCode: 401, title: "Verification expired", detail: "Request a new verification email.");

        user.BackupEmail = email;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = user.Id, Timestamp = now,
            Action = "Backup email verified", Resource = "Profile", Status = ActivityLogStatus.Success
        });
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            return Problem(statusCode: 409, title: "Email already in use", detail: "That email address is already used by another account.");
        }
        return Ok(ToProfile(user));
    }

    private async Task<User?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var rawId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(rawId, out var userId)) return null;
        return await dbContext.Users.SingleOrDefaultAsync(user => user.Id == userId && user.IsActive, cancellationToken);
    }

    private static ProfileResponse ToProfile(User user) =>
        new(
            user.FullName ?? user.Username,
            user.Username,
            user.Email,
            user.BackupEmail,
            user.Role,
            user.ProfileImageData,
            !string.IsNullOrWhiteSpace(user.AccountSecretKeyHash),
            user.AccountSecretKeyPrefix,
            user.AccountSecretKeyCreatedAt);

    private static bool TryValidateProfileImage(string? imageData, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(imageData))
        {
            error = "Choose a JPG, PNG, or WebP image to upload.";
            return false;
        }

        var separator = imageData.IndexOf(',');
        if (separator < 0)
        {
            error = "The selected image could not be read.";
            return false;
        }

        var header = imageData[..separator];
        var payload = imageData[(separator + 1)..];
        var contentType = header switch
        {
            "data:image/jpeg;base64" => "image/jpeg",
            "data:image/png;base64" => "image/png",
            "data:image/webp;base64" => "image/webp",
            _ => ""
        };
        if (contentType.Length == 0)
        {
            error = "Use a JPG, PNG, or WebP image.";
            return false;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException)
        {
            error = "The selected image could not be read.";
            return false;
        }

        if (bytes.Length == 0 || bytes.Length > MaxProfileImageBytes)
        {
            error = "Choose an image smaller than 1 MB.";
            return false;
        }

        var validSignature = contentType switch
        {
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/png" => bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A,
            "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!validSignature)
        {
            error = "The selected file is not a valid image of the reported type.";
            return false;
        }

        return true;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool HashesMatch(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }

    public sealed record UpdateProfileRequest(string? Name);
    public sealed record UpdateProfileImageRequest(string? ImageData);
    public sealed record BackupEmailRequest(string? Email);
    public sealed record BackupEmailOtpRequest(string? Code);
    public sealed record BackupEmailLinkRequest(string? Token);
    public sealed record AccountSecretKeyResponse(string SecretKey, string Prefix, DateTimeOffset CreatedAt);
    public sealed record ProfileResponse(
        string Name,
        string Username,
        string Email,
        string? BackupEmail,
        string Role,
        string? ProfileImageData,
        bool HasAccountSecretKey = false,
        string? AccountSecretKeyPrefix = null,
        DateTimeOffset? AccountSecretKeyCreatedAt = null);
}
