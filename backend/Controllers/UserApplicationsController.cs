using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/applications")]
public sealed class UserApplicationsController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<ActionResult<IReadOnlyList<AccessibleApplication>>> GetMine(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await dbContext.Users.AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken))
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
                application.Description,
                application.LaunchUrl,
                application.Category,
                application.AutoFillUsername,
                !string.IsNullOrEmpty(application.EncryptedAutoFillPassword),
                application.Id == DbInitializer.FinanceTestApplicationId))
            .ToListAsync(cancellationToken);

        return Ok(applications);
    }

    [HttpGet("catalog")]
    public async Task<ActionResult<IReadOnlyList<CatalogApplication>>> GetCatalog(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await dbContext.Users.AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken))
            return Unauthorized();

        var groupIds = dbContext.UserGroups.Where(membership => membership.UserId == userId).Select(membership => membership.GroupId);
        var directAccess = await dbContext.ApplicationUserAccesses.AsNoTracking()
            .Where(access => access.UserId == userId).Select(access => access.ApplicationId).ToListAsync(cancellationToken);
        var groupAccess = await dbContext.ApplicationGroupAccesses.AsNoTracking()
            .Where(access => groupIds.Contains(access.GroupId)).Select(access => access.ApplicationId).ToListAsync(cancellationToken);
        var assignedIds = directAccess.Concat(groupAccess).ToHashSet();
        var recentRequests = await dbContext.ApplicationAccessRequests.AsNoTracking()
            .Where(request => request.UserId == userId)
            .OrderByDescending(request => request.RequestedAt)
            .Select(request => new { request.ApplicationId, request.Status })
            .ToListAsync(cancellationToken);
        var requestStatus = recentRequests.GroupBy(request => request.ApplicationId)
            .ToDictionary(group => group.Key, group => group.First().Status);

        var applications = await dbContext.Applications.AsNoTracking()
            .Where(application => application.IsPublished)
            .OrderBy(application => application.Name)
            .Select(application => new { application.Id, application.Name, application.Description, application.LaunchUrl, application.AllowAccessRequests })
            .ToListAsync(cancellationToken);

        return Ok(applications.Select(application => new CatalogApplication(
            application.Id,
            application.Name,
            application.Description,
            application.LaunchUrl,
            assignedIds.Contains(application.Id),
            application.AllowAccessRequests,
            requestStatus.GetValueOrDefault(application.Id))));
    }

    [HttpGet("requests/mine")]
    public async Task<ActionResult<IReadOnlyList<MyAccessRequest>>> GetMyRequests(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId) || !await dbContext.Users.AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken))
            return Unauthorized();

        var requests = await dbContext.ApplicationAccessRequests.AsNoTracking()
            .Where(request => request.UserId == userId)
            .OrderByDescending(request => request.RequestedAt)
            .Select(request => new MyAccessRequest(
                request.Id,
                request.ApplicationId,
                request.Application.Name,
                request.Status,
                request.Reason,
                request.RequestedAt,
                request.ReviewedAt,
                request.DecisionNote))
            .ToListAsync(cancellationToken);
        return Ok(requests);
    }

    [HttpPost("{applicationId:guid}/requests")]
    public async Task<ActionResult<MyAccessRequest>> RequestAccess(
        Guid applicationId, [FromBody] AccessRequestSubmission? submission, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId && candidate.IsActive, cancellationToken);
        if (user is null) return Unauthorized();
        if ((submission?.Reason?.Length ?? 0) > 1000)
            return BadRequest(new { message = "The request reason must be 1000 characters or fewer." });

        var application = await dbContext.Applications.SingleOrDefaultAsync(
            candidate => candidate.Id == applicationId && candidate.IsPublished && candidate.AllowAccessRequests,
            cancellationToken);
        if (application is null) return NotFound(new { message = "This application is not accepting access requests." });

        var groupIds = dbContext.UserGroups.Where(membership => membership.UserId == userId).Select(membership => membership.GroupId);
        var alreadyAssigned = await dbContext.ApplicationUserAccesses.AnyAsync(
                access => access.ApplicationId == applicationId && access.UserId == userId, cancellationToken)
            || await dbContext.ApplicationGroupAccesses.AnyAsync(
                access => access.ApplicationId == applicationId && groupIds.Contains(access.GroupId), cancellationToken);
        if (alreadyAssigned)
            return Conflict(new { message = "You already have access to this application." });
        if (await dbContext.ApplicationAccessRequests.AnyAsync(
                request => request.ApplicationId == applicationId && request.UserId == userId && request.Status == "Pending",
                cancellationToken))
            return Conflict(new { message = "You already have a pending request for this application." });

        var request = new ApplicationAccessRequest
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            UserId = userId,
            Reason = submission?.Reason?.Trim() ?? string.Empty,
            Status = "Pending",
            RequestedAt = DateTimeOffset.UtcNow
        };
        dbContext.ApplicationAccessRequests.Add(request);
        AddAudit(userId, "Application Access Requested", application.Name, ActivityLogStatus.Success);
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A pending request already exists for this application." });
        }

        return CreatedAtAction(nameof(GetMyRequests), new { }, new MyAccessRequest(
            request.Id, application.Id, application.Name, request.Status, request.Reason,
            request.RequestedAt, request.ReviewedAt, request.DecisionNote));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out userId);
    }

    private void AddAudit(Guid userId, string action, string resource, ActivityLogStatus status)
    {
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(), UserId = userId, Timestamp = DateTimeOffset.UtcNow,
            Action = action, Resource = resource, Status = status
        });
    }

public sealed record AccessibleApplication(
    Guid Id,
    string Name,
    string Description,
    string LaunchUrl,
    string Category,
    string AutoFillUsername,
    bool HasAutoFillPassword,
    bool UsesSafePassageNativeAuthentication = false);
    public sealed record CatalogApplication(Guid Id, string Name, string Description, string LaunchUrl, bool IsAssigned, bool AllowAccessRequests, string? RequestStatus);
    public sealed record MyAccessRequest(Guid Id, Guid ApplicationId, string ApplicationName, string Status, string Reason, DateTimeOffset RequestedAt, DateTimeOffset? ReviewedAt, string? DecisionNote);
    public sealed record AccessRequestSubmission(string? Reason);
}
