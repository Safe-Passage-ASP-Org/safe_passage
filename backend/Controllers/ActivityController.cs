using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/activity")]
public sealed class ActivityController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetActivities(CancellationToken cancellationToken)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        Guid? userId = Guid.TryParse(claim, out var uid) ? uid : null;

        var query = dbContext.ActivityLogs.AsNoTracking();
        if (userId.HasValue)
        {
            query = query.Where(a => a.UserId == userId.Value || a.UserId == null);
        }

        var logs = await query
            .OrderByDescending(a => a.Timestamp)
            .Take(25)
            .ToListAsync(cancellationToken);

        var result = logs.Select(log =>
        {
            var isSuccess = log.Status == ActivityLogStatus.Success;
            var (icon, color) = log.Action.ToLowerInvariant() switch
            {
                var a when a.Contains("login") && isSuccess => ("log-in", "#10b981"),
                var a when a.Contains("login") && !isSuccess => ("close-circle", "#ef4444"),
                var a when a.Contains("mfa") && isSuccess => ("shield-checkmark", "#10b981"),
                var a when a.Contains("mfa") && !isSuccess => ("warning", "#ef4444"),
                var a when a.Contains("device") => ("hardware-chip", "#f59e0b"),
                var a when a.Contains("app") => ("apps", "#0ea5e9"),
                _ => (isSuccess ? "shield-checkmark" : "close-circle", isSuccess ? "#10b981" : "#ef4444")
            };

            return new
            {
                id = log.Id.ToString(),
                action = log.Action,
                target = log.Resource,
                time = GetRelativeTime(log.Timestamp),
                icon,
                color
            };
        });

        return Ok(result);
    }

    private static string GetRelativeTime(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - timestamp;
        if (elapsed.TotalMinutes < 1) return "Just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} mins ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hours ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} days ago";
        return timestamp.ToString("MMM dd, yyyy");
    }
}
