using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin,Auditor")]
[ApiController]
[Route("api/admin/activity")]
public sealed class AdminActivityController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminActivityRecord>>> GetRecent(CancellationToken cancellationToken)
    {
        var logs = await dbContext.ActivityLogs.AsNoTracking()
            .OrderByDescending(log => log.Timestamp)
            .Take(250)
            .Select(log => new AdminActivityRecord(log.Id, log.Timestamp,
                log.User == null ? "Unknown user" : log.User.Username,
                log.Action, log.Resource, log.Status.ToString()))
            .ToListAsync(cancellationToken);
        return Ok(logs);
    }

    public sealed record AdminActivityRecord(Guid Id, DateTimeOffset Timestamp, string Username,
        string Action, string Resource, string Status);
}
