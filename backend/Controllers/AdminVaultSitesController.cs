using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/vault-sites")]
public sealed class AdminVaultSitesController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<PendingVaultSite>>> GetPending(CancellationToken cancellationToken)
    {
        var sites = await dbContext.VaultSites.AsNoTracking()
            .Where(site => site.VerificationStatus == "Pending")
            .OrderBy(site => site.CreatedAt)
            .Select(site => new PendingVaultSite(site.Id, site.Name, site.LoginUrl, site.Origin,
                site.Category, site.Description, site.User.Username, site.User.Email, site.CreatedAt))
            .ToListAsync(cancellationToken);
        return Ok(sites);
    }

    [HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken cancellationToken)
    {
        var site = await dbContext.VaultSites.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (site is null) return NotFound();
        site.VerificationStatus = "Verified";
        site.VerifiedAt = DateTimeOffset.UtcNow;
        site.VerifiedByUserId = GetAdminUserId();
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken cancellationToken)
    {
        var site = await dbContext.VaultSites.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (site is null) return NotFound();
        site.VerificationStatus = "Rejected";
        site.VerifiedAt = null;
        site.VerifiedByUserId = GetAdminUserId();
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private Guid? GetAdminUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    public sealed record PendingVaultSite(Guid Id, string Name, string LoginUrl, string Origin,
        string Category, string Description, string Username, string Email, DateTimeOffset CreatedAt);
}
