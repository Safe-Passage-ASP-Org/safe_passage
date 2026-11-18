using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/vault/sites")]
public sealed class VaultSitesController(ApplicationDbContext dbContext, ApplicationCredentialProtector protector) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VaultSiteSummary>>> GetMine(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
        var sites = await dbContext.VaultSites.AsNoTracking()
            .Where(site => site.UserId == userId)
            .OrderBy(site => site.Name)
            .Select(site => new VaultSiteSummary(site.Id, site.Name, site.LoginUrl, site.Origin, site.Category,
                site.Description, site.AutoFillUsername, site.EncryptedPassword != "", site.EncryptedTotpSecret != "",
                site.VerificationStatus, site.CreatedAt))
            .ToListAsync(cancellationToken);
        return Ok(sites);
    }

    [HttpPost]
    public async Task<ActionResult<VaultSiteSummary>> Create([FromBody] VaultSiteRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
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
            VerificationStatus = "Pending", CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.VaultSites.Add(site);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetMine), null, ToSummary(site));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<VaultSiteSummary>> Update(Guid id, [FromBody] VaultSiteRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await IsActiveUserAsync(userId, cancellationToken)) return Unauthorized();
        if (!TryValidate(request, out var origin, out var error)) return BadRequest(new { message = error });
        if (!TotpCodeGenerator.IsValidSecret(request.TotpSecret)) return BadRequest(new { message = "Enter a valid Base32 2FA secret." });
        var site = await dbContext.VaultSites.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (site is null) return NotFound();
        if (origin != site.Origin && await dbContext.VaultSites.AnyAsync(candidate => candidate.UserId == userId && candidate.Origin == origin && candidate.Id != id, cancellationToken))
            return Conflict(new { message = "A saved site already uses this website address." });

        if (origin != site.Origin)
        {
            site.Origin = origin;
            site.VerificationStatus = "Pending";
            site.VerifiedAt = null;
            site.VerifiedByUserId = null;
        }
        else if (site.VerificationStatus == "Rejected")
        {
            site.VerificationStatus = "Pending";
            site.VerifiedAt = null;
            site.VerifiedByUserId = null;
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
        var secret = protector.Unprotect(site.EncryptedTotpSecret);
        var code = string.IsNullOrEmpty(secret) ? null : TotpCodeGenerator.Generate(secret, DateTimeOffset.UtcNow);
        return Ok(new VaultSiteCredentials(site.AutoFillUsername, protector.Unprotect(site.EncryptedPassword), code));
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

    private static VaultSiteSummary ToSummary(VaultSite site) => new(site.Id, site.Name, site.LoginUrl, site.Origin,
        site.Category, site.Description, site.AutoFillUsername, site.EncryptedPassword != "", site.EncryptedTotpSecret != "",
        site.VerificationStatus, site.CreatedAt);

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
        string Description, string AutoFillUsername, bool HasPassword, bool HasTotpSecret, string VerificationStatus, DateTimeOffset CreatedAt);
    public sealed record VaultSiteCredentials(string Username, string Password, string? TotpCode);
}
