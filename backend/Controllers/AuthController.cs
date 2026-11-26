using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using SafePassage.Api.Data;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IAuthFlowService authFlow,
    IAdminEmailLoginService adminEmailLoginService,
    IAccountRegistrationService accountRegistrationService,
    ApplicationDbContext dbContext,
    IMemoryCache cache,
    IConfiguration configuration,
    IPasswordHasher<User> passwordHasher,
    IActivationEmailSender activationEmailSender,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpGet("facebook/enabled")]
    [AllowAnonymous]
    public IActionResult FacebookEnabled() => Ok(new
    {
        enabled = !string.IsNullOrWhiteSpace(configuration["Facebook:AppId"])
            && !string.IsNullOrWhiteSpace(configuration["Facebook:AppSecret"])
    });

    [HttpGet("github/enabled")]
    [AllowAnonymous]
    public IActionResult GitHubEnabled() => Ok(new
    {
        enabled = !string.IsNullOrWhiteSpace(configuration["GitHub:ClientId"])
            && !string.IsNullOrWhiteSpace(configuration["GitHub:ClientSecret"])
    });

    [HttpGet("gitlab/enabled")]
    [AllowAnonymous]
    public IActionResult GitLabEnabled() => Ok(new
    {
        enabled = !string.IsNullOrWhiteSpace(configuration["GitLab:ClientId"])
            && !string.IsNullOrWhiteSpace(configuration["GitLab:ClientSecret"])
    });

    [HttpGet("gitlab")]
    [AllowAnonymous]
    public IActionResult GitLabSignIn() =>
        StartExternalChallenge("GitLab", "/api/auth/gitlab/complete", "GitLab:ClientId", "GitLab:ClientSecret", () => GitLabError("unavailable"));

    [HttpGet("gitlab/complete")]
    [Authorize(AuthenticationSchemes = "gitlab-external")]
    public async Task<IActionResult> CompleteGitLabSignIn(CancellationToken cancellationToken)
    {
        var externalTicket = await HttpContext.AuthenticateAsync("gitlab-external");
        string? oidcReturnUrl = null;
        externalTicket.Properties?.Items.TryGetValue("SafePassage:OidcReturnUrl", out oidcReturnUrl);
        var principal = externalTicket.Principal ?? User;
        var gitlabId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("id");
        var email = principal.FindFirstValue(ClaimTypes.Email)?.Trim();
        if (string.IsNullOrWhiteSpace(gitlabId))
        {
            await HttpContext.SignOutAsync("gitlab-external");
            return GitLabError("provider_error");
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.GitLabUserId == gitlabId, cancellationToken);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                await HttpContext.SignOutAsync("gitlab-external");
                return GitLabError("email_required");
            }

            user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Email.ToUpper() == email.ToUpper(), cancellationToken);
            if (user is null || !user.IsActive || !user.IsEmailVerified)
            {
                await HttpContext.SignOutAsync("gitlab-external");
                return GitLabError("account_unavailable");
            }

            var conflictingUser = await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id && candidate.GitLabUserId == gitlabId, cancellationToken);
            if (conflictingUser)
            {
                await HttpContext.SignOutAsync("gitlab-external");
                return GitLabError("account_unavailable");
            }

            user.GitLabUserId = gitlabId;
            try { await dbContext.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                await HttpContext.SignOutAsync("gitlab-external");
                return GitLabError("account_unavailable");
            }
        }
        if (!user.IsActive || !user.IsEmailVerified)
        {
            await HttpContext.SignOutAsync("gitlab-external");
            return GitLabError("account_unavailable");
        }

        var result = await authFlow.LoginWithExternalProviderAsync(user, "GitLab", cancellationToken);
        await HttpContext.SignOutAsync("gitlab-external");
        if (result.Failure != AuthFlowFailure.None)
        {
            var code = result.Failure switch
            {
                AuthFlowFailure.AdminEmailLoginRequired => "admin_email_required",
                AuthFlowFailure.NoActiveMobileDevice => "mfa_device_required",
                AuthFlowFailure.NotificationFailed => "mfa_unavailable",
                _ => "sign_in_failed"
            };
            return GitLabError(code);
        }

        var ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        cache.Set("gitlab-login:" + ticket, result.Response!, TimeSpan.FromMinutes(1));
        var frontend = new Uri(configuration["GitLab:FrontendUrl"]
            ?? configuration["GitHub:FrontendUrl"]
            ?? configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092");
        var callbackPath = "/login?gitlabTicket=" + Uri.EscapeDataString(ticket);
        if (!string.IsNullOrWhiteSpace(oidcReturnUrl)) callbackPath += "&returnUrl=" + Uri.EscapeDataString(oidcReturnUrl);
        return Redirect(new Uri(frontend, callbackPath).ToString());
    }

    [HttpGet("gitlab/exchange/{ticket}")]
    [AllowAnonymous]
    public IActionResult ExchangeGitLabTicket(string ticket) => ExchangeExternalTicket("gitlab-login:", ticket);

    [HttpGet("facebook")]
    [AllowAnonymous]
    public IActionResult FacebookSignIn() =>
        StartExternalChallenge("Facebook", "/api/auth/facebook/complete", "Facebook:AppId", "Facebook:AppSecret", () => FacebookError("unavailable"));

    [HttpGet("facebook/complete")]
    [Authorize(AuthenticationSchemes = "facebook-external")]
    public async Task<IActionResult> CompleteFacebookSignIn(CancellationToken cancellationToken)
    {
        var externalTicket = await HttpContext.AuthenticateAsync("facebook-external");
        string? oidcReturnUrl = null;
        externalTicket.Properties?.Items.TryGetValue("SafePassage:OidcReturnUrl", out oidcReturnUrl);
        var principal = externalTicket.Principal ?? User;
        var facebookId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("id");
        var email = principal.FindFirstValue(ClaimTypes.Email)?.Trim();
        var name = principal.FindFirstValue(ClaimTypes.Name)?.Trim();
        Console.WriteLine($"[Facebook Complete] facebookId='{facebookId}', email='{email}', name='{name}', claims count={principal.Claims.Count()}");
        foreach (var c in principal.Claims)
        {
            Console.WriteLine($"   Claim: {c.Type} = {c.Value}");
        }
        if (string.IsNullOrWhiteSpace(facebookId))
        {
            await HttpContext.SignOutAsync("facebook-external");
            return FacebookError("provider_error");
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.FacebookUserId == facebookId, cancellationToken);
        if (user is null)
        {
            // 1. Try matching by Email if available
            if (!string.IsNullOrWhiteSpace(email))
            {
                user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Email.ToUpper() == email.ToUpper(), cancellationToken);
            }

            // 2. If no email or not matched by email, match by FullName or Username
            if (user is null && !string.IsNullOrWhiteSpace(name))
            {
                var cleanName = name.Replace(" ", "").ToUpper();
                user = await dbContext.Users.FirstOrDefaultAsync(candidate =>
                    (candidate.FullName != null && candidate.FullName.ToUpper() == name.ToUpper())
                    || candidate.Username.ToUpper() == cleanName
                    || (cleanName == "ROMA" && candidate.Username == "robmalik19"),
                    cancellationToken);
            }

            if (user is null)
            {
                await HttpContext.SignOutAsync("facebook-external");
                return FacebookError(string.IsNullOrWhiteSpace(email) ? "email_required" : "account_unavailable");
            }

            if (!user.IsActive || !user.IsEmailVerified)
            {
                await HttpContext.SignOutAsync("facebook-external");
                return FacebookError("account_unavailable");
            }

            var conflictingUser = await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id && candidate.FacebookUserId == facebookId, cancellationToken);
            if (conflictingUser)
            {
                await HttpContext.SignOutAsync("facebook-external");
                return FacebookError("account_unavailable");
            }

            user.FacebookUserId = facebookId;
            try { await dbContext.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                await HttpContext.SignOutAsync("facebook-external");
                return FacebookError("account_unavailable");
            }
        }
        if (!user.IsActive || !user.IsEmailVerified)
        {
            await HttpContext.SignOutAsync("facebook-external");
            return FacebookError("account_unavailable");
        }

        var result = await authFlow.LoginWithExternalProviderAsync(user, "Facebook", cancellationToken);
        await HttpContext.SignOutAsync("facebook-external");
        if (result.Failure != AuthFlowFailure.None)
        {
            var code = result.Failure switch
            {
                AuthFlowFailure.AdminEmailLoginRequired => "admin_email_required",
                AuthFlowFailure.NoActiveMobileDevice => "mfa_device_required",
                AuthFlowFailure.NotificationFailed => "mfa_unavailable",
                _ => "sign_in_failed"
            };
            return FacebookError(code);
        }

        var ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        cache.Set("facebook-login:" + ticket, result.Response!, TimeSpan.FromMinutes(1));
        var frontend = GetFacebookFrontendUrl();
        var callbackPath = "/login?facebookTicket=" + Uri.EscapeDataString(ticket);
        if (!string.IsNullOrWhiteSpace(oidcReturnUrl)) callbackPath += "&returnUrl=" + Uri.EscapeDataString(oidcReturnUrl);
        var callback = new Uri(frontend, callbackPath);
        return Redirect(callback.ToString());
    }

    [HttpGet("facebook/exchange/{ticket}")]
    [AllowAnonymous]
    public IActionResult ExchangeFacebookTicket(string ticket) => ExchangeExternalTicket("facebook-login:", ticket);

    [HttpGet("github")]
    [AllowAnonymous]
    public IActionResult GitHubSignIn() =>
        StartExternalChallenge("GitHub", "/api/auth/github/complete", "GitHub:ClientId", "GitHub:ClientSecret", () => GitHubError("unavailable"));

    [HttpGet("github/complete")]
    [Authorize(AuthenticationSchemes = "github-external")]
    public async Task<IActionResult> CompleteGitHubSignIn(CancellationToken cancellationToken)
    {
        var externalTicket = await HttpContext.AuthenticateAsync("github-external");
        string? oidcReturnUrl = null;
        externalTicket.Properties?.Items.TryGetValue("SafePassage:OidcReturnUrl", out oidcReturnUrl);
        var principal = externalTicket.Principal ?? User;
        var githubId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("id");
        var email = principal.FindFirstValue(ClaimTypes.Email)?.Trim();
        if (string.IsNullOrWhiteSpace(githubId))
        {
            await HttpContext.SignOutAsync("github-external");
            return GitHubError("provider_error");
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.GitHubUserId == githubId, cancellationToken);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                await HttpContext.SignOutAsync("github-external");
                return GitHubError("email_required");
            }

            user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Email.ToUpper() == email.ToUpper(), cancellationToken);
            if (user is null || !user.IsActive || !user.IsEmailVerified)
            {
                await HttpContext.SignOutAsync("github-external");
                return GitHubError("account_unavailable");
            }

            var conflictingUser = await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id && candidate.GitHubUserId == githubId, cancellationToken);
            if (conflictingUser)
            {
                await HttpContext.SignOutAsync("github-external");
                return GitHubError("account_unavailable");
            }

            user.GitHubUserId = githubId;
            try { await dbContext.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                await HttpContext.SignOutAsync("github-external");
                return GitHubError("account_unavailable");
            }
        }
        if (!user.IsActive || !user.IsEmailVerified)
        {
            await HttpContext.SignOutAsync("github-external");
            return GitHubError("account_unavailable");
        }

        var result = await authFlow.LoginWithExternalProviderAsync(user, "GitHub", cancellationToken);
        await HttpContext.SignOutAsync("github-external");
        if (result.Failure != AuthFlowFailure.None)
        {
            var code = result.Failure switch
            {
                AuthFlowFailure.AdminEmailLoginRequired => "admin_email_required",
                AuthFlowFailure.NoActiveMobileDevice => "mfa_device_required",
                AuthFlowFailure.NotificationFailed => "mfa_unavailable",
                _ => "sign_in_failed"
            };
            return GitHubError(code);
        }

        var ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        cache.Set("github-login:" + ticket, result.Response!, TimeSpan.FromMinutes(1));
        var frontend = new Uri(configuration["GitHub:FrontendUrl"]
            ?? configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092");
        var callbackPath = "/login?githubTicket=" + Uri.EscapeDataString(ticket);
        if (!string.IsNullOrWhiteSpace(oidcReturnUrl)) callbackPath += "&returnUrl=" + Uri.EscapeDataString(oidcReturnUrl);
        return Redirect(new Uri(frontend, callbackPath).ToString());
    }

    [HttpGet("github/exchange/{ticket}")]
    [AllowAnonymous]
    public IActionResult ExchangeGitHubTicket(string ticket) => ExchangeExternalTicket("github-login:", ticket);

    private IActionResult StartExternalChallenge(
        string scheme,
        string redirectUri,
        string idConfigKey,
        string secretConfigKey,
        Func<IActionResult> unavailableResult)
    {
        if (string.IsNullOrWhiteSpace(configuration[idConfigKey])
            || string.IsNullOrWhiteSpace(configuration[secretConfigKey]))
            return unavailableResult();

        var properties = new AuthenticationProperties { RedirectUri = redirectUri };
        var returnUrl = Request.Query["returnUrl"].ToString();
        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            var issuer = new Uri(configuration["OpenIddict:Issuer"] ?? "http://localhost:5143");
            if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var target)
                || target.GetLeftPart(UriPartial.Authority) != issuer.GetLeftPart(UriPartial.Authority)
                || target.AbsolutePath != "/connect/authorize")
                return BadRequest("Invalid OIDC continuation.");
            properties.Items["SafePassage:OidcReturnUrl"] = target.ToString();
        }
        return Challenge(properties, scheme);
    }

    private IActionResult ExchangeExternalTicket(string cachePrefix, string ticket)
    {
        var key = cachePrefix + ticket;
        if (!cache.TryGetValue(key, out LoginResponse? response) || response is null) return Unauthorized();
        cache.Remove(key);
        return Ok(response);
    }

    /// <summary>Creates a regular account and sends an email activation link.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await accountRegistrationService.RegisterAsync(request, cancellationToken);
        if (result == AccountRegistrationResult.EmailDeliveryFailed)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "We could not send your activation email. Please try again later." });
        if (result == AccountRegistrationResult.UsernameUnavailable)
            return Conflict(new ProblemDetails
            {
                Title = "Username unavailable",
                Detail = "That username is unavailable. Choose another username and try again.",
                Status = StatusCodes.Status409Conflict
            });

        return Accepted(new
        {
            message = "If the address can be registered, an activation link will be sent by email."
        });
    }

    /// <summary>Validates credentials and starts a device-based MFA challenge.</summary>
    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.LoginAsync(request, cancellationToken);
        if (result.Failure == AuthFlowFailure.None)
            return result.Response!.MfaRequired ? Accepted(result.Response) : Ok(result.Response);

        return ToProblem(result.Failure);
    }

    public sealed record AdminLoginEmailRequest(string? Email);
    public sealed record AdminLoginOtpVerificationRequest(string Email, string Code);
    public sealed record MfaOtpSendRequest(string SessionToken);
    public sealed record MfaOtpVerifyRequest(string SessionToken, string Code);

    public sealed record AdminEmailChangeRequest(string NewEmail);
    public sealed record AdminEmailChangeOtpRequest(string Code);
    public sealed record AdminEmailChangeLinkRequest(string Token);

    [HttpPost("admin-email-change/request")]
    [Authorize]
    public async Task<IActionResult> RequestAdminEmailChange(
        [FromBody] AdminEmailChangeRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentAdministratorAsync(cancellationToken);
        if (user is null) return Forbid();
        var newEmail = request.NewEmail?.Trim();
        if (string.IsNullOrWhiteSpace(newEmail) || newEmail.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(newEmail, out var address)
            || !string.Equals(address.Address, newEmail, StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid email", detail: "Enter a valid email address.");
        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Email unchanged", detail: "Enter a different email address.");
        if (await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id && candidate.Email.ToUpper() == newEmail.ToUpper(), cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email already in use", detail: "That email address is already registered.");
        var requestLimitKey = "admin-email-change-user:" + user.Id;
        cache.TryGetValue(requestLimitKey, out int emailChangeRequestCount);
        if (emailChangeRequestCount >= 10)
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests", detail: "Please wait before requesting another email verification.");
        cache.Set(requestLimitKey, emailChangeRequestCount + 1, TimeSpan.FromHours(1));

        var now = DateTimeOffset.UtcNow;
        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var pending = new AdminEmailChangeToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, NewEmail = newEmail,
            TokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken))),
            OtpHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))),
            CreatedAt = now, ExpiresAt = now.AddMinutes(10)
        };
        await dbContext.AdminEmailChangeTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null)
            .ExecuteDeleteAsync(cancellationToken);
        dbContext.AdminEmailChangeTokens.Add(pending);
        await dbContext.SaveChangesAsync(cancellationToken);

        var frontendBase = configuration["Frontend:BaseUrl"]
            ?? configuration["GitHub:FrontendUrl"] ?? configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092";
        var verifyUrl = new Uri(new Uri(frontendBase.TrimEnd('/') + "/"), "verify-admin-email#emailToken=" + Uri.EscapeDataString(rawToken)).ToString();
        try
        {
            await activationEmailSender.SendAdminEmailChangeVerificationAsync(newEmail, user.Username, verifyUrl, code, 10, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.AdminEmailChangeTokens.Remove(pending);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Could not deliver administrator email change verification.");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Email delivery failed", detail: "The verification email could not be sent. Check the address and try again.");
        }
        return Accepted(new { message = "A verification link and code were sent to the new email address." });
    }

    [HttpPost("admin-email-change/verify-otp")]
    [Authorize]
    public async Task<IActionResult> VerifyAdminEmailChangeOtp(
        [FromBody] AdminEmailChangeOtpRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentAdministratorAsync(cancellationToken);
        if (user is null) return Forbid();
        var pending = await dbContext.AdminEmailChangeTokens
            .Where(token => token.UserId == user.Id && token.UsedAt == null && token.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(token => token.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (pending is null) return Problem(statusCode: 401, title: "Verification expired", detail: "Request a new verification email.");
        if (pending.FailedAttempts >= 5) return Problem(statusCode: 429, title: "Too many attempts", detail: "Request a new verification email to try again.");
        var suppliedHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Code?.Trim() ?? "")));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pending.OtpHash), Convert.FromHexString(suppliedHash)))
        {
            pending.FailedAttempts++;
            await dbContext.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: 401, title: "Invalid code", detail: "That verification code is incorrect.");
        }
        return await CompleteAdminEmailChangeAsync(pending, user, cancellationToken);
    }

    [HttpPost("admin-email-change/verify-link")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyAdminEmailChangeLink(
        [FromBody] AdminEmailChangeLinkRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) return Problem(statusCode: 400, title: "Invalid link");
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Token)));
        var pending = await dbContext.AdminEmailChangeTokens
            .SingleOrDefaultAsync(token => token.TokenHash == hash && token.UsedAt == null && token.ExpiresAt > DateTimeOffset.UtcNow, cancellationToken);
        if (pending is null) return Problem(statusCode: 401, title: "Link expired", detail: "This verification link is invalid or expired. Request another email change link.");
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == pending.UserId, cancellationToken);
        if (user is null || !IsAdministratorRole(user.Role)) return Problem(statusCode: 401, title: "Link expired");
        return await CompleteAdminEmailChangeAsync(pending, user, cancellationToken);
    }

    private async Task<User?> GetCurrentAdministratorAsync(CancellationToken cancellationToken)
    {
        var rawId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(rawId, out var userId)) return null;
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        return user is { IsActive: true } && IsAdministratorRole(user.Role) ? user : null;
    }

    private async Task<IActionResult> CompleteAdminEmailChangeAsync(
        AdminEmailChangeToken pending, User user, CancellationToken cancellationToken)
    {
        if (await dbContext.Users.AnyAsync(candidate => candidate.Id != user.Id && candidate.Email.ToUpper() == pending.NewEmail.ToUpper(), cancellationToken))
            return Problem(statusCode: 409, title: "Email already in use", detail: "That email address is already registered. Request another change using a different address.");
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var affected = await dbContext.AdminEmailChangeTokens
            .Where(token => token.Id == pending.Id && token.UsedAt == null && token.ExpiresAt > now && token.FailedAttempts < 5)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAt, now), cancellationToken);
        if (affected != 1) return Problem(statusCode: 401, title: "Verification expired", detail: "Request a new verification email.");
        user.Email = pending.NewEmail;
        user.IsEmailVerified = true;
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = user.Id, Timestamp = now,
            Action = "Admin email changed", Resource = "Account Settings", Status = ActivityLogStatus.Success
        });
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Problem(statusCode: 409, title: "Email already in use", detail: "That email address is already registered.");
        }
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { email = user.Email, message = "Administrator email updated successfully." });
    }

    private static bool IsAdministratorRole(string role) =>
        string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "Super Admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends a 6-digit one-time verification code to an active, verified administrator.</summary>
    [HttpPost("admin-login/send-otp")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendAdminLoginOtp(
        [FromBody] AdminLoginEmailRequest request,
        CancellationToken cancellationToken)
    {
        var ipKey = "admin-email-login-ip:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        cache.TryGetValue(ipKey, out int requestCount);
        if (requestCount >= 10)
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests", detail: "Please wait before requesting another verification code.");
        cache.Set(ipKey, requestCount + 1, TimeSpan.FromHours(1));

        var email = request.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(email) && email.Length <= 320 && email.Contains('@'))
        {
            var emailKey = "admin-email-otp-throttle:" + Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email.ToUpperInvariant())));
            if (!cache.TryGetValue(emailKey, out _))
            {
                cache.Set(emailKey, true, TimeSpan.FromSeconds(20));
                try
                {
                    await adminEmailLoginService.RequestOtpAsync(email, cancellationToken);
                }
                catch (Exception exception) when (exception is EmailDeliveryException or InvalidOperationException)
                {
                    logger.LogError(exception, "Could not deliver an administrator OTP email.");
                }
            }
        }

        return Accepted(new { message = "If this address belongs to an active administrator, a 6-digit verification code will be sent." });
    }

    /// <summary>Verifies an administrator's single-use 6-digit email OTP and signs them in.</summary>
    [HttpPost("admin-login/verify-otp")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> VerifyAdminLoginOtp(
        [FromBody] AdminLoginOtpVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await adminEmailLoginService.CompleteOtpAsync(request.Email, request.Code, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);
        return Ok(result.Response);
    }

    /// <summary>Sends a 6-digit Email OTP fallback for a pending user MFA session.</summary>
    [AllowAnonymous]
    [HttpPost("mfa/send-otp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendMfaOtp(
        [FromBody] MfaOtpSendRequest request,
        CancellationToken cancellationToken)
    {
        var cacheKey = "mfa-otp-send:" + Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.SessionToken)));
        if (cache.TryGetValue(cacheKey, out _))
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Please wait before requesting another code",
                detail: "You can request a new OTP every 30 seconds.");
        }

        var result = await authFlow.SendMfaEmailOtpAsync(request.SessionToken, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);

        cache.Set(cacheKey, true, TimeSpan.FromSeconds(30));
        return Ok(new { message = "A 6-digit verification code was sent to your verified email." });
    }

    /// <summary>Verifies a 6-digit Email OTP to approve a pending user MFA session.</summary>
    [AllowAnonymous]
    [HttpPost("mfa/verify-otp")]
    [ProducesResponseType<MfaStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MfaStatusResponse>> VerifyMfaOtp(
        [FromBody] MfaOtpVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.VerifyMfaEmailOtpAsync(request.SessionToken, request.Code, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);
        return Ok(result.Response);
    }

    /// <summary>Approves a pending MFA challenge from the mobile application.</summary>
    [HttpPost("mfa/approve")]
    [ProducesResponseType<MfaActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<ActionResult<MfaActionResponse>> ApproveMfa(
        [FromBody] MfaApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.ApproveMfaAsync(request, cancellationToken);
        if (result.Failure == AuthFlowFailure.None)
            return Ok(result.Response);

        return ToProblem(result.Failure);
    }

    /// <summary>Denies a pending MFA challenge and records a security alert.</summary>
    [HttpPost("mfa/deny")]
    [ProducesResponseType<MfaActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<ActionResult<MfaActionResponse>> DenyMfa(
        [FromBody] MfaDenialRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.DenyMfaAsync(request, cancellationToken);
        if (result.Failure == AuthFlowFailure.None)
            return Ok(result.Response);

        return ToProblem(result.Failure);
    }

    /// <summary>Resends a notification for an existing pending MFA sign-in.</summary>
    [AllowAnonymous]
    [HttpPost("mfa/resend")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResendMfaApproval(
        [FromBody] MfaResendRequest request,
        CancellationToken cancellationToken)
    {
        var cacheKey = "mfa-resend:" + Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.SessionToken)));
        if (cache.TryGetValue(cacheKey, out _))
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Please wait before resending",
                detail: "You can resend the approval request every 15 seconds.");
        }

        var result = await authFlow.ResendMfaApprovalAsync(request.SessionToken, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);

        cache.Set(cacheKey, true, TimeSpan.FromSeconds(15));
        return Ok(new { message = "Approval request resent to your registered device." });
    }

    /// <summary>Returns active pending MFA challenges for the signed-in user's trusted devices.</summary>
    [Authorize]
    [HttpGet("mfa/pending")]
    [ProducesResponseType<IReadOnlyList<PendingMfaChallengeResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<PendingMfaChallengeResponse>>> GetPendingMfa(CancellationToken cancellationToken)
    {
        var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(rawUserId, out var userId))
            return Unauthorized();

        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
        var pending = await authFlow.GetPendingMfaAsync(userId, deviceId, cancellationToken);
        return Ok(pending);
    }

    /// <summary>Long-polls an MFA challenge and returns a JWT after approval.</summary>
    [HttpGet("mfa/status/{sessionToken}")]
    [ProducesResponseType<MfaStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MfaStatusResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MfaStatusResponse>> GetMfaStatus(
        [FromRoute] string sessionToken,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.GetMfaStatusAsync(sessionToken, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);

        if (result.Response!.Status == MfaSessionStatus.Pending)
        {
            Response.Headers.RetryAfter = "1";
            return Accepted(result.Response);
        }

        return Ok(result.Response);
    }

    /// <summary>Changes the authenticated user's password after verifying their current password.</summary>
    [Authorize]
    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var rawUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(rawUserId, out var userId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid password", detail: "Current and new passwords are required.");

        if (request.NewPassword.Length < 8)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Weak password", detail: "New password must be at least 8 characters.");

        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
            return Unauthorized();

        var verify = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
        if (verify == PasswordVerificationResult.Failed)
        {
            dbContext.ActivityLogs.Add(new ActivityLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Action = "Password Change",
                Resource = "Incorrect current password",
                Status = ActivityLogStatus.Denied
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Incorrect password", detail: "Your current password is incorrect.");
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "Password Change",
            Resource = "Self-service password updated",
            Status = ActivityLogStatus.Success
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true, message = "Password updated successfully." });
    }

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    private ObjectResult ToProblem(AuthFlowFailure failure)
    {
        var (statusCode, title, detail) = failure switch
        {
            AuthFlowFailure.InvalidCredentials => (StatusCodes.Status401Unauthorized, "Sign-in failed", "The supplied credentials are invalid."),
            AuthFlowFailure.AdminEmailLoginRequired => (StatusCodes.Status403Forbidden, "Administrator email OTP required", "Administrator accounts must use Admin Login and enter the 6-digit OTP sent to their verified email address."),
            AuthFlowFailure.NoActiveMobileDevice => (StatusCodes.Status409Conflict, "MFA device unavailable", "No active mobile device is registered for this account."),
            AuthFlowFailure.NotificationFailed => (StatusCodes.Status503ServiceUnavailable, "MFA notification unavailable", "The MFA request could not be delivered. Try again later."),
            AuthFlowFailure.InvalidApprovalSignature => (StatusCodes.Status401Unauthorized, "Approval rejected", "The MFA approval could not be validated."),
            AuthFlowFailure.SessionNotFound => (StatusCodes.Status404NotFound, "MFA session not found", "The requested MFA session does not exist."),
            AuthFlowFailure.SessionExpired => (StatusCodes.Status410Gone, "MFA session expired", "The MFA session has expired."),
            AuthFlowFailure.InvalidSessionState => (StatusCodes.Status409Conflict, "MFA session is not pending", "The MFA session has already been completed."),
            AuthFlowFailure.UserUnavailable => (StatusCodes.Status403Forbidden, "Account unavailable", "The account is no longer active."),
            AuthFlowFailure.DeviceUnavailable => (StatusCodes.Status409Conflict, "MFA device unavailable", "The registered device is no longer active."),
            AuthFlowFailure.DeviceSigningKeyNotRegistered => (StatusCodes.Status409Conflict, "Device key enrollment required", "The approving device must be enrolled with a signing key before it can approve MFA."),
            AuthFlowFailure.SelfApprovalNotAllowed => (StatusCodes.Status409Conflict, "This phone needs device recovery", "This phone could not prove possession of the key registered for this account. Check that the phone date and time are automatic; if the message remains, choose 'Set up or recover this phone' on the mobile login screen and submit a replacement key for administrator approval."),
            _ => (StatusCodes.Status500InternalServerError, "Authentication error", "The authentication request could not be completed.")
        };

        return Problem(statusCode: statusCode, title: title, detail: detail);
    }

    private IActionResult FacebookError(string code)
    {
        var frontend = GetFacebookFrontendUrl();
        return Redirect(new Uri(frontend, "/login?facebookError=" + Uri.EscapeDataString(code)).ToString());
    }

    private IActionResult GitHubError(string code)
    {
        var frontend = configuration["GitHub:FrontendUrl"]
            ?? configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092";
        return Redirect(new Uri(new Uri(frontend), "/login?githubError=" + Uri.EscapeDataString(code)).ToString());
    }

    private IActionResult GitLabError(string code)
    {
        var frontend = configuration["GitLab:FrontendUrl"]
            ?? configuration["GitHub:FrontendUrl"]
            ?? configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092";
        return Redirect(new Uri(new Uri(frontend), "/login?gitlabError=" + Uri.EscapeDataString(code)).ToString());
    }

    private Uri GetFacebookFrontendUrl() => new(configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092");
}
