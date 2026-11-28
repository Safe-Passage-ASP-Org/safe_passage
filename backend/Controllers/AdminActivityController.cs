using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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
        [FromQuery] string? action = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] Guid? groupId = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateFilters(from, to, year, month, fromDate, toDate, status);
        if (validation is not null)
            return BadRequest(new { message = validation });
        if (groupId.HasValue && !await dbContext.Groups.AnyAsync(group => group.Id == groupId, cancellationToken))
            return BadRequest(new { message = "The selected group does not exist." });

        var query = BuildQuery(from, to, status, search, action, userId, groupId, year, month, fromDate, toDate);
        var boundedLimit = Math.Clamp(limit, 1, 2000);
        var orderedQuery = dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? query.OrderByDescending(log => log.Timestamp.ToString())
            : query.OrderByDescending(log => log.Timestamp);
        var records = await orderedQuery
            .Take(boundedLimit)
            .Select(log => new
            {
                log.Id,
                log.Timestamp,
                SubjectUserId = log.TargetUserId ?? log.UserId,
                log.Action,
                log.Resource,
                Status = log.Status.ToString(),
                log.DeviceId,
                log.Reason
            })
            .ToListAsync(cancellationToken);

        var subjectIds = records
            .Where(record => record.SubjectUserId.HasValue)
            .Select(record => record.SubjectUserId!.Value)
            .Distinct()
            .ToList();
        var usernames = await dbContext.Users.AsNoTracking()
            .Where(user => subjectIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Username })
            .ToDictionaryAsync(user => user.Id, user => user.Username, cancellationToken);

        return Ok(records.Select(record => new AdminActivityRecord(
            record.Id,
            record.Timestamp,
            record.SubjectUserId is Guid subjectId && usernames.TryGetValue(subjectId, out var username)
                ? username
                : "Unknown user",
            record.Action,
            record.Resource,
            record.Status,
            record.DeviceId,
            record.Reason,
            record.SubjectUserId)).ToList());
    }

    [HttpGet("filters")]
    public async Task<ActionResult<AdminActivityFilterOptions>> GetFilterOptions(
        CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new ActivityUserOption(user.Id, user.Username, user.FullName))
            .ToListAsync(cancellationToken);
        var groups = await dbContext.Groups.AsNoTracking()
            .OrderBy(group => group.Name)
            .Select(group => new ActivityGroupOption(group.Id, group.Name))
            .ToListAsync(cancellationToken);
        var actions = await dbContext.ActivityLogs.AsNoTracking()
            .Select(log => log.Action)
            .Distinct()
            .OrderBy(action => action)
            .ToListAsync(cancellationToken);
        var years = await dbContext.ActivityLogs.AsNoTracking()
            .Select(log => log.Timestamp.Year)
            .Distinct()
            .OrderByDescending(year => year)
            .ToListAsync(cancellationToken);

        return Ok(new AdminActivityFilterOptions(users, groups, actions, years));
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AdminActivitySummary>> GetSummary(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? action = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] Guid? groupId = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateFilters(from, to, year, month, fromDate, toDate, status);
        if (validation is not null)
            return BadRequest(new { message = validation });
        if (groupId.HasValue && !await dbContext.Groups.AnyAsync(group => group.Id == groupId, cancellationToken))
            return BadRequest(new { message = "The selected group does not exist." });

        var query = BuildQuery(from, to, status, search, action, userId, groupId, year, month, fromDate, toDate);
        var since24h = DateTimeOffset.UtcNow.AddHours(-24);
        var totalCount = await query.CountAsync(cancellationToken);
        var mfaEvents = await query.CountAsync(
            log => log.Action.ToLower().Contains("mfa"),
            cancellationToken);
        var recentFrom = from.HasValue && from.Value > since24h ? from : since24h;
        var recentQuery = BuildQuery(
            recentFrom,
            to,
            status,
            search,
            action,
            userId,
            groupId,
            year,
            month,
            fromDate,
            toDate);
        var mfaLast24h = await recentQuery.CountAsync(
            log => log.Action.ToLower().Contains("mfa"),
            cancellationToken);
        var deniedCount = await query.CountAsync(log => log.Status == ActivityLogStatus.Denied, cancellationToken);
        var deniedLast24h = await recentQuery.CountAsync(
            log => log.Status == ActivityLogStatus.Denied,
            cancellationToken);
        var successfulLogins = await query.CountAsync(
            log => log.Status == ActivityLogStatus.Success && log.Action.ToLower().Contains("login"),
            cancellationToken);
        var deniedLogins = await query.CountAsync(
            log => log.Status == ActivityLogStatus.Denied && log.Action.ToLower().Contains("login"),
            cancellationToken);
        var actionCounts = await query
            .GroupBy(log => log.Action)
            .Select(group => new { Action = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Action)
            .Select(item => new AdminActivityActionCount(item.Action, item.Count))
            .ToListAsync(cancellationToken);

        return Ok(new AdminActivitySummary(
            totalCount,
            mfaLast24h,
            deniedCount,
            deniedLast24h,
            successfulLogins,
            deniedLogins,
            actionCounts,
            mfaEvents));
    }

    private IQueryable<ActivityLog> BuildQuery(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? status,
        string? search,
        string? action,
        Guid? userId,
        Guid? groupId,
        int? year,
        int? month,
        DateOnly? fromDate,
        DateOnly? toDate)
    {
        var query = dbContext.ActivityLogs.AsNoTracking();
        if (dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            && (from.HasValue || to.HasValue || year.HasValue || month.HasValue
                || fromDate.HasValue || toDate.HasValue))
        {
            query = BuildSqliteTimeFilteredQuery(from, to, year, month, fromDate, toDate);
        }
        else
        {
            if (from.HasValue)
                query = query.Where(log => log.Timestamp >= from.Value);
            if (to.HasValue)
                query = query.Where(log => log.Timestamp <= to.Value);
            if (fromDate.HasValue)
            {
                var start = new DateTimeOffset(fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                query = query.Where(log => log.Timestamp >= start);
            }
            if (toDate.HasValue)
            {
                var endExclusive = new DateTimeOffset(toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                query = query.Where(log => log.Timestamp < endExclusive);
            }
            if (year.HasValue)
                query = query.Where(log => log.Timestamp.Year == year.Value);
            if (month.HasValue)
                query = query.Where(log => log.Timestamp.Month == month.Value);
        }
        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<ActivityLogStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
            query = query.Where(log => log.Status == parsedStatus);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var normalizedAction = action.Trim().ToLower();
            query = query.Where(log => log.Action.ToLower() == normalizedAction);
        }
        if (userId.HasValue)
            query = query.Where(log => (log.TargetUserId ?? log.UserId) == userId.Value);
        if (groupId.HasValue)
        {
            var selectedGroupId = groupId.Value;
            query = query.Where(log => dbContext.UserGroups.Any(membership =>
                membership.GroupId == selectedGroupId
                && membership.UserId == (log.TargetUserId ?? log.UserId)));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var matchingUsers = dbContext.Users.AsNoTracking()
                .Where(user => user.Username.ToLower().Contains(term)
                    || (user.FullName != null && user.FullName.ToLower().Contains(term)))
                .Select(user => user.Id);
            query = query.Where(log =>
                matchingUsers.Contains(log.TargetUserId ?? log.UserId ?? Guid.Empty)
                || log.Action.ToLower().Contains(term)
                || log.Resource.ToLower().Contains(term)
                || (log.DeviceId != null && log.DeviceId.ToLower().Contains(term))
                || (log.Reason != null && log.Reason.ToLower().Contains(term)));
        }

        return query;
    }

    private IQueryable<ActivityLog> BuildSqliteTimeFilteredQuery(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? year,
        int? month,
        DateOnly? fromDate,
        DateOnly? toDate)
    {
        var conditions = new List<string>();
        var parameters = new List<object>();

        if (from.HasValue)
        {
            var value = from.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
            conditions.Add($"julianday(\"Timestamp\") >= julianday({{{parameters.Count}}})");
            parameters.Add(value);
        }
        if (to.HasValue)
        {
            var value = to.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
            conditions.Add($"julianday(\"Timestamp\") <= julianday({{{parameters.Count}}})");
            parameters.Add(value);
        }
        if (fromDate.HasValue)
        {
            conditions.Add($"substr(\"Timestamp\", 1, 10) >= {{{parameters.Count}}}");
            parameters.Add(fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        if (toDate.HasValue)
        {
            conditions.Add($"substr(\"Timestamp\", 1, 10) <= {{{parameters.Count}}}");
            parameters.Add(toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        if (year.HasValue)
        {
            conditions.Add($"substr(\"Timestamp\", 1, 4) = {{{parameters.Count}}}");
            parameters.Add(year.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (month.HasValue)
        {
            conditions.Add($"substr(\"Timestamp\", 6, 2) = {{{parameters.Count}}}");
            parameters.Add(month.Value.ToString("D2", CultureInfo.InvariantCulture));
        }

        var sql = $"SELECT * FROM \"ActivityLogs\" WHERE {string.Join(" AND ", conditions)}";
        return dbContext.ActivityLogs.FromSqlRaw(sql, parameters.ToArray()).AsNoTracking();
    }

    private static string? ValidateFilters(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? year,
        int? month,
        DateOnly? fromDate,
        DateOnly? toDate,
        string? status)
    {
        if (from > to)
            return "The start date must be on or before the end date.";
        if (fromDate > toDate)
            return "The start date must be on or before the end date.";
        if (year is < 1 or > 9999)
            return "Year must be between 1 and 9999.";
        if (month is < 1 or > 12)
            return "Month must be between 1 and 12.";
        if (!string.IsNullOrWhiteSpace(status)
            && !Enum.TryParse<ActivityLogStatus>(status.Trim(), ignoreCase: true, out _))
            return "Status must be Success or Denied.";
        return null;
    }

    public sealed record AdminActivityRecord(
        Guid Id,
        DateTimeOffset Timestamp,
        string Username,
        string Action,
        string Resource,
        string Status,
        string? DeviceId = null,
        string? Reason = null,
        Guid? UserId = null);

    public sealed record AdminActivitySummary(
        int TotalCount,
        int MfaLast24h,
        int DeniedCount,
        int DeniedLast24h,
        int SuccessfulLogins = 0,
        int DeniedLogins = 0,
        IReadOnlyList<AdminActivityActionCount>? ActionCounts = null,
        int MfaEvents = 0);

    public sealed record AdminActivityActionCount(string Action, int Count);
    public sealed record ActivityUserOption(Guid Id, string Username, string? FullName);
    public sealed record ActivityGroupOption(Guid Id, string Name);
    public sealed record AdminActivityFilterOptions(
        IReadOnlyList<ActivityUserOption> Users,
        IReadOnlyList<ActivityGroupOption> Groups,
        IReadOnlyList<string> Actions,
        IReadOnlyList<int> Years);
}
