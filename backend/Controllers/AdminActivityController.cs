using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Policy = "AuditorOrAbove")]
[ApiController]
[Route("api/admin/activity")]
public sealed class AdminActivityController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminActivityRecord>>> GetRecent(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 250,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 2000);
        var query = dbContext.ActivityLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<ActivityLogStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
        {
            query = query.Where(log => log.Status == parsedStatus);
        }

        var logs = await query
            .Select(log => new AdminActivityRecord(
                log.Id,
                log.Timestamp,
                log.User == null ? "Unknown user" : log.User.Username,
                log.Action,
                log.Resource,
                log.Status.ToString(),
                log.DeviceId,
                log.Reason))
            .ToListAsync(cancellationToken);

        IEnumerable<AdminActivityRecord> filtered = logs;
        if (from.HasValue)
            filtered = filtered.Where(l => l.Timestamp >= from.Value);
        if (to.HasValue)
            filtered = filtered.Where(l => l.Timestamp <= to.Value);
        if (!string.IsNullOrWhiteSpace(status))
            filtered = filtered.Where(l => string.Equals(l.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(l =>
                l.Username.Contains(term, StringComparison.OrdinalIgnoreCase)
                || l.Action.Contains(term, StringComparison.OrdinalIgnoreCase)
                || l.Resource.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (l.DeviceId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (l.Reason?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return Ok(filtered.OrderByDescending(log => log.Timestamp).Take(boundedLimit).ToList());
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AdminActivitySummary>> GetSummary(CancellationToken cancellationToken)
    {
        var since24h = DateTimeOffset.UtcNow.AddHours(-24);
        var logs = await dbContext.ActivityLogs.AsNoTracking()
            .Select(log => new { log.Timestamp, log.Action, log.Status })
            .ToListAsync(cancellationToken);

        return Ok(new AdminActivitySummary(
            logs.Count,
            logs.Count(l => l.Timestamp >= since24h && l.Action.Contains("MFA", StringComparison.OrdinalIgnoreCase)),
            logs.Count(l => l.Status == ActivityLogStatus.Denied),
            logs.Count(l => l.Timestamp >= since24h && l.Status == ActivityLogStatus.Denied)));
    }

    public sealed record AdminActivityRecord(Guid Id, DateTimeOffset Timestamp, string Username,
        string Action, string Resource, string Status, string? DeviceId = null, string? Reason = null);

    public sealed record AdminActivitySummary(int TotalCount, int MfaLast24h, int DeniedCount, int DeniedLast24h);
}
