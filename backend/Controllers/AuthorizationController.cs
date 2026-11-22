using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[ApiController]
public sealed class AuthorizationController(
    ApplicationDbContext db,
    IOpenIddictApplicationManager applications,
    IConfiguration configuration,
    IMemoryCache cache,
    ISecurityPolicyEvaluator policyEvaluator) : ControllerBase
{
    [HttpGet("/connect/authorize")]
    [HttpPost("/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        var request = Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext);
        if (request is null) return BadRequest("Invalid OpenID Connect request.");
        var auth = await HttpContext.AuthenticateAsync("oidc");
        if (!auth.Succeeded || auth.Principal is null)
        {
            var login = configuration["OpenIddict:LoginUrl"] ?? "http://localhost:5092/login";
            var issuer = new Uri(configuration["OpenIddict:Issuer"] ?? "http://localhost:5143");
            var authorizeUrl = new Uri(issuer, Request.Path + Request.QueryString);
            var returnUrl = Uri.EscapeDataString(authorizeUrl.ToString());
            return Redirect($"{login}?returnUrl={returnUrl}");
        }

        var sub = auth.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = Guid.TryParse(sub, out var userId)
            ? await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken)
            : null;
        var client = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken);
        var properties = client is null ? null : await applications.GetPropertiesAsync(client, cancellationToken);
        var appId = properties is not null && properties.TryGetValue("safe_passage_application_id", out var property)
            ? property.GetString() : null;
        var app = Guid.TryParse(appId, out var applicationId)
            ? await db.Applications.SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken)
            : null;
        var assigned = user is not null && app is not null && await db.ApplicationUserAccesses.AnyAsync(
            x => x.ApplicationId == app.Id && x.UserId == user.Id, cancellationToken);
        if (!assigned && user is not null && app is not null)
            assigned = await db.ApplicationGroupAccesses.AnyAsync(x => x.ApplicationId == app.Id
                && db.UserGroups.Any(g => g.UserId == user.Id && g.GroupId == x.GroupId), cancellationToken);
        if (!assigned) return Forbid("oidc");

        var policy = await policyEvaluator.EvaluateAsync(cancellationToken);
        if (!policy.IsValid)
            return Forbid("oidc");

        if (policy.RequiresTrustedDevice || policy.RequiresScreenLock || policy.BlocksRootedDevices || policy.BlocksJailbrokenDevices)
        {
            var requestedDeviceId = Request.Query["device_id"].FirstOrDefault()
                ?? Request.Headers["X-Device-Id"].FirstOrDefault();

            var hasCompliantDevice = false;
            if (!string.IsNullOrWhiteSpace(requestedDeviceId))
            {
                var candidateQuery = db.Devices.AsNoTracking()
                    .Where(d => d.UserId == user!.Id
                        && d.Status == DeviceStatus.Active
                        && d.DeviceId == requestedDeviceId);

                if (policy.RequiresTrustedDevice)
                    candidateQuery = candidateQuery.Where(d => d.TrustStatus == DeviceTrustStatus.Trusted);

                var candidates = await candidateQuery.ToListAsync(cancellationToken);
                hasCompliantDevice = candidates.Any(d => DevicePostureEvaluator.Evaluate(d, policy).IsCompliant);
            }

            if (!hasCompliantDevice)
            {
                db.ActivityLogs.Add(new ActivityLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user!.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Action = "OIDC Authorization",
                    Resource = app?.Name ?? request.ClientId ?? "oidc",
                    Status = ActivityLogStatus.Denied
                });
                await db.SaveChangesAsync(cancellationToken);
                return Forbid("oidc");
            }
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity("OpenIddict.Server.AspNetCore"));
        principal.SetClaim(OpenIddictConstants.Claims.Subject, user!.Id.ToString());
        var scopes = request.GetScopes();
        if (scopes.Contains(OpenIddictConstants.Scopes.Profile))
        {
            principal.SetClaim(OpenIddictConstants.Claims.Name, user.FullName ?? user.Username);
            principal.SetClaim(OpenIddictConstants.Claims.PreferredUsername, user.Username);
        }
        if (scopes.Contains(OpenIddictConstants.Scopes.Email))
            principal.SetClaim(OpenIddictConstants.Claims.Email, user.Email);
        if (scopes.Contains(OpenIddictConstants.Scopes.Roles))
            principal.SetClaim(OpenIddictConstants.Claims.Role, user.Role);
        principal.SetScopes(scopes);
        principal.SetResources("safe-passage");
        foreach (var claim in principal.Claims)
        {
            if (claim.Type == OpenIddictConstants.Claims.Subject)
                claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
            else if (claim.Type is OpenIddictConstants.Claims.Name or OpenIddictConstants.Claims.PreferredUsername or OpenIddictConstants.Claims.Email)
                claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken);
            else
                claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
        }
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // The web login submits its already-validated Safe Passage bearer token here after login/MFA.
    [HttpPost("/connect/session")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult CreateSession([FromBody] OidcSessionRequest request)
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(id, out var userId)) return Unauthorized();
        var ticket = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        cache.Set("oidc-login:" + ticket, userId, TimeSpan.FromSeconds(60));
        var issuer = new Uri(configuration["OpenIddict:Issuer"] ?? "http://localhost:5143");
        if (!Uri.TryCreate(request.ReturnUrl, UriKind.Absolute, out var target) || target.GetLeftPart(UriPartial.Authority) != issuer.GetLeftPart(UriPartial.Authority)
            || target.AbsolutePath != "/connect/authorize") return BadRequest(new { message = "Invalid authorization continuation URL." });
        var completionUrl = new Uri(issuer, "/connect/session/complete?ticket=" + Uri.EscapeDataString(ticket) + "&returnUrl=" + Uri.EscapeDataString(target.ToString()));
        return Ok(new { completionUrl = completionUrl.ToString() });
    }

    [HttpGet("/connect/session/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> CompleteSession([FromQuery] string ticket, [FromQuery] string returnUrl, CancellationToken cancellationToken)
    {
        var key = "oidc-login:" + ticket;
        if (!cache.TryGetValue(key, out Guid userId)) return Unauthorized();
        cache.Remove(key);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (user is null) return Unauthorized();
        var issuer = new Uri(configuration["OpenIddict:Issuer"] ?? "http://localhost:5143");
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var target) || target.GetLeftPart(UriPartial.Authority) != issuer.GetLeftPart(UriPartial.Authority)
            || target.AbsolutePath != "/connect/authorize") return BadRequest("Invalid authorization continuation URL.");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName ?? user.Username)
        }, "oidc"));
        await HttpContext.SignInAsync("oidc", principal, new AuthenticationProperties { IsPersistent = false });
        return Redirect(target.ToString());
    }

    [HttpGet("/connect/userinfo")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    public IActionResult UserInfo()
    {
        var claims = new Dictionary<string, object>();
        foreach (var claim in User.Claims)
            claims[claim.Type] = claim.Value;
        return Ok(claims);
    }

    public sealed record OidcSessionRequest(string ReturnUrl);
}
