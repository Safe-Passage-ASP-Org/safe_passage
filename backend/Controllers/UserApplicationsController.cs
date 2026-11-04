using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/applications")]
public sealed class UserApplicationsController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<ActionResult<IReadOnlyList<AccessibleApplication>>> GetMine(CancellationToken cancellationToken)
    {
        var userClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(userClaim, out var userId))
            return Unauthorized();

        var groupIds = dbContext.UserGroups
            .Where(membership => membership.UserId == userId)
            .Select(membership => membership.GroupId);
        var applications = await dbContext.Applications
            .AsNoTracking()
            .Where(application =>
                application.UserAccess.Any(access => access.UserId == userId)
                || application.GroupAccess.Any(access => groupIds.Contains(access.GroupId)))
            .OrderBy(application => application.Name)
            .Select(application => new AccessibleApplication(
                application.Id,
                application.Name,
                application.Description))
            .ToListAsync(cancellationToken);

        return Ok(applications);
    }

    public sealed record AccessibleApplication(Guid Id, string Name, string Description);
}
