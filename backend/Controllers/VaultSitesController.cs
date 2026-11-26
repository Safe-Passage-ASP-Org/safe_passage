using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Contracts.Subscriptions;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/vault/sites")]
public sealed class VaultSitesController(
    ApplicationDbContext dbContext,
    ApplicationCredentialProtector protector,
    ISubscriptionService? subscriptionService = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VaultSiteSummary>>> GetMine(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
        var entitlements = subscriptionService is null
            ? new SubscriptionEntitlementsResponse("free", false, 2, 5, 7, false)
            : await subscriptionService.GetUserEntitlementsAsync(userId, cancellationToken);
        var savedSites = await dbContext.VaultSites.AsNoTracking()
            .Where(site => site.UserId == userId)
            .OrderBy(site => site.Name)
            .ToListAsync(cancellationToken);
        var sites = savedSites.Select((site, index) => ToSummary(
            site,
            !entitlements.IsPro && entitlements.MaxSavedSites is { } limit && index >= limit,
            entitlements.CanGenerateTotpCodes)).ToList();
        return Ok(sites);
    }

    [HttpPost]
    public async Task<ActionResult<VaultSiteSummary>> Create([FromBody] VaultSiteRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
        var entitlements = await GetEntitlementsAsync(userId, cancellationToken);
        var existingSiteCount = await dbContext.VaultSites.CountAsync(site => site.UserId == userId, cancellationToken);
        if (entitlements.MaxSavedSites is { } siteLimit && existingSiteCount >= siteLimit)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Pro plan required",
                detail: $"The Free plan allows up to {siteLimit} saved websites. Remove one or upgrade to Pro to add more.");
        if (!entitlements.CanGenerateTotpCodes && !string.IsNullOrWhiteSpace(request.TotpSecret))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Pro plan required",
                detail: "Saving and generating authenticator codes is a Pro feature. You can still save the site's username and password on Free.");
        if (!TryValidate(request, out var origin, out var error)) return BadRequest(new { message = error });
        if (!TotpCodeGenerator.IsValidSecret(request.TotpSecret)) return BadRequest(new { message = "Enter a valid Base32 2FA secret." });
        if (await dbContext.VaultSites.AnyAsync(site => site.UserId == userId && site.Origin == origin, cancellationToken))
            return Conflict(new { message = "A saved site already uses this website address." });

        var site = new VaultSite
        {
            Id = Guid.NewGuid(), UserId = userId, Name = request.Name.Trim(), LoginUrl = request.LoginUrl.Trim(),
            Origin = origin, Category = request.Category?.Trim() ?? "Other", Description = request.Description?.Trim() ?? "",
            AutoFillUsername = request.AutoFillUsername?.Trim() ?? "", EncryptedPassword = protector.Protect(request.Password),
            EncryptedTotpSecret = protector.Protect(NormalizeTotpSecret(request.TotpSecret)),
            // A site is submitted by its owner into their personal vault. Admin review
            // should not block that user's own extension workflow.
            VerificationStatus = "Verified", VerifiedAt = DateTimeOffset.UtcNow,
            VerifiedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.VaultSites.Add(site);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetMine), null, ToSummary(site));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<VaultSiteSummary>> Update(Guid id, [FromBody] VaultSiteRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
        var entitlements = await GetEntitlementsAsync(userId, cancellationToken);
        if (!entitlements.CanGenerateTotpCodes && !request.ClearTotpSecret && !string.IsNullOrWhiteSpace(request.TotpSecret))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Pro plan required",
                detail: "Saving authenticator secrets is a Pro feature. Upgrade to Pro to add or change this secret.");
        if (!TryValidate(request, out var origin, out var error)) return BadRequest(new { message = error });
        if (!TotpCodeGenerator.IsValidSecret(request.TotpSecret)) return BadRequest(new { message = "Enter a valid Base32 2FA secret." });
        var site = await dbContext.VaultSites.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (site is null) return NotFound();
        if (origin != site.Origin && await dbContext.VaultSites.AnyAsync(candidate => candidate.UserId == userId && candidate.Origin == origin && candidate.Id != id, cancellationToken))
            return Conflict(new { message = "A saved site already uses this website address." });

        if (origin != site.Origin)
        {
            site.Origin = origin;
            site.VerificationStatus = "Verified";
            site.VerifiedAt = DateTimeOffset.UtcNow;
            site.VerifiedByUserId = userId;
        }
        else if (site.VerificationStatus != "Verified")
        {
            site.VerificationStatus = "Verified";
            site.VerifiedAt = DateTimeOffset.UtcNow;
            site.VerifiedByUserId = userId;
        }
        site.Name = request.Name.Trim();
        site.LoginUrl = request.LoginUrl.Trim();
        site.Category = request.Category?.Trim() ?? "Other";
        site.Description = request.Description?.Trim() ?? "";
        site.AutoFillUsername = request.AutoFillUsername?.Trim() ?? "";
        if (!string.IsNullOrEmpty(request.Password)) site.EncryptedPassword = protector.Protect(request.Password);
        if (request.ClearTotpSecret) site.EncryptedTotpSecret = "";
        else if (!string.IsNullOrWhiteSpace(request.TotpSecret)) site.EncryptedTotpSecret = protector.Protect(NormalizeTotpSecret(request.TotpSecret));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToSummary(site));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
        var site = await dbContext.VaultSites.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (site is null) return NotFound();
        dbContext.VaultSites.Remove(site);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/credentials")]
    public async Task<ActionResult<VaultSiteCredentials>> GetCredentials(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)
            || !await IsActiveUserAsync(userId, cancellationToken)
            || !string.Equals(User.FindFirstValue("auth_context"), "BrowserAppAccess", StringComparison.Ordinal)
            || !long.TryParse(User.FindFirstValue("auth_context_expires_at"), out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Mobile approval required", detail: "Approve this site sign-in on your trusted mobile device first.");

        var site = await dbContext.VaultSites.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == id && candidate.UserId == userId && candidate.VerificationStatus == "Verified", cancellationToken);
        if (site is null) return NotFound(new { message = "This saved site is not verified or is unavailable." });
        var entitlements = await GetEntitlementsAsync(userId, cancellationToken);
        if (!entitlements.IsPro && entitlements.MaxSavedSites is { } siteLimit)
        {
            var savedSiteIds = await dbContext.VaultSites.AsNoTracking()
                .Where(candidate => candidate.UserId == userId)
                .OrderBy(candidate => candidate.Name)
                .Select(candidate => candidate.Id)
                .ToListAsync(cancellationToken);
            if (savedSiteIds.IndexOf(id) >= siteLimit)
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Saved website is over the Free plan limit",
                    detail: $"The Free plan includes the first {siteLimit} saved websites. Upgrade to Pro to use this website in the extension.");
        }
        var secret = protector.Unprotect(site.EncryptedTotpSecret);
        var code = string.IsNullOrEmpty(secret) || !entitlements.CanGenerateTotpCodes
            ? null
            : TotpCodeGenerator.Generate(secret, DateTimeOffset.UtcNow);
        return Ok(new VaultSiteCredentials(site.AutoFillUsername, protector.Unprotect(site.EncryptedPassword), code,
            !string.IsNullOrEmpty(secret) && !entitlements.CanGenerateTotpCodes));
    }

    private static string NormalizeTotpSecret(string? secret) => string.Concat((secret ?? "").Where(character => !char.IsWhiteSpace(character))).TrimEnd('=').ToUpperInvariant();

    private static bool TryValidate(VaultSiteRequest request, out string origin, out string error)
    {
        origin = "";
        error = "";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            error = "Site name is required and must be 200 characters or fewer.";
        else if (!Uri.TryCreate(request.LoginUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))))
            error = "Use an HTTPS website address (HTTP is allowed only for localhost).";
        else if ((request.LoginUrl?.Length ?? 0) > 2048 || (request.Category?.Length ?? 0) > 64 || (request.Description?.Length ?? 0) > 2000
            || (request.AutoFillUsername?.Length ?? 0) > 320 || (request.Password?.Length ?? 0) > 1024 || (request.TotpSecret?.Length ?? 0) > 256)
            error = "One or more site details exceed the allowed length.";
        else origin = uri!.GetLeftPart(UriPartial.Authority);
        return error.Length == 0;
    }

    private bool TryGetUserId(out Guid userId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out userId);
    }

    private Task<bool> IsActiveUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken);

    private Task<SubscriptionEntitlementsResponse> GetEntitlementsAsync(Guid userId, CancellationToken cancellationToken) =>
        subscriptionService is null
            ? Task.FromResult(new SubscriptionEntitlementsResponse("free", false, 2, 5, 7, false))
            : subscriptionService.GetUserEntitlementsAsync(userId, cancellationToken);

    private static VaultSiteSummary ToSummary(VaultSite site, bool lockedByPlan = false, bool canGenerateTotpCodes = false) => new(site.Id, site.Name, site.LoginUrl, site.Origin,
        site.Category, site.Description, site.AutoFillUsername, site.EncryptedPassword != "", site.EncryptedTotpSecret != "",
        site.VerificationStatus, site.CreatedAt, lockedByPlan, canGenerateTotpCodes && site.EncryptedTotpSecret != "");

    public sealed record VaultSiteRequest(
        [Required, StringLength(200)] string Name,
        [Required, StringLength(2048)] string LoginUrl,
        string? Category = null,
        string? Description = null,
        string? AutoFillUsername = null,
        string? Password = null,
        string? TotpSecret = null,
        bool ClearTotpSecret = false);

    public sealed record VaultSiteSummary(Guid Id, string Name, string LoginUrl, string Origin, string Category,
        string Description, string AutoFillUsername, bool HasPassword, bool HasTotpSecret, string VerificationStatus, DateTimeOffset CreatedAt,
        bool LockedByPlan = false, bool CanGenerateTotpCode = false);
    public sealed record VaultSiteCredentials(string Username, string Password, string? TotpCode, bool RequiresProForTotp = false);
}
