using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Policy = "OperatorOrAdmin")]
[ApiController]
[Route("api/admin/access-requests")]
public sealed class AdminAccessRequestsController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminAccessRequestSummary>>> GetAll(
        [FromQuery] string status = "Pending", CancellationToken cancellationToken = default)
    {
        var query = dbContext.ApplicationAccessRequests.AsNoTracking();
        if (!string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
            query = query.Where(request => request.Status == status);

        var requests = await query.OrderByDescending(request => request.RequestedAt)
            .Select(request => new AdminAccessRequestSummary(
                request.Id,
                request.ApplicationId,
                request.Application.Name,
                request.UserId,
                request.User.Username,
                request.User.Email,
                request.Status,
                request.Reason,
                request.RequestedAt,
                request.ReviewedAt,
                request.DecisionNote))
            .ToListAsync(cancellationToken);
        return Ok(requests);
    }

    [HttpPost("{id:guid}/decision")]
    public async Task<ActionResult<AdminAccessRequestSummary>> Decide(
        Guid id, [FromBody] AccessRequestDecision? decision, CancellationToken cancellationToken)
    {
        if (decision is null)
            return BadRequest(new { message = "Decision payload is required." });
        if ((decision.Note?.Length ?? 0) > 1000)
            return BadRequest(new { message = "The decision note must be 1000 characters or fewer." });
        var actorClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(actorClaim, out var reviewerId)) return Unauthorized();
        if (!await dbContext.Users.AnyAsync(user => user.Id == reviewerId && user.IsActive, cancellationToken))
            return Unauthorized();

        var request = await dbContext.ApplicationAccessRequests
            .Include(candidate => candidate.Application)
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (request is null) return NotFound();
        if (request.Status != "Pending")
            return Conflict(new { message = "This access request has already been reviewed." });
        if (decision.Approve && (!request.Application.IsPublished || !request.Application.AllowAccessRequests))
            return Conflict(new { message = "This application is no longer accepting access requests." });
        if (decision.Approve && (!request.User.IsActive || !request.User.IsEmailVerified))
            return Conflict(new { message = "The requesting account is inactive or has not completed email verification." });

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var approved = decision.Approve;
        request.Status = approved ? "Approved" : "Denied";
        request.ReviewedByUserId = reviewerId;
        request.ReviewedAt = DateTimeOffset.UtcNow;
        request.DecisionNote = decision.Note?.Trim();
        if (approved && !await dbContext.ApplicationUserAccesses.AnyAsync(
                access => access.ApplicationId == request.ApplicationId && access.UserId == request.UserId,
                cancellationToken))
        {
            dbContext.ApplicationUserAccesses.Add(new ApplicationUserAccess
            {
                ApplicationId = request.ApplicationId,
                UserId = request.UserId
            });
        }

        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = reviewerId,
            Timestamp = DateTimeOffset.UtcNow,
            Action = approved ? "Application Access Request Approved" : "Application Access Request Denied",
            Resource = $"{request.User.Username} → {request.Application.Name}",
            Status = approved ? ActivityLogStatus.Success : ActivityLogStatus.Denied
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            return Conflict(new { message = "Another administrator has already reviewed this request." });
        }

        return Ok(new AdminAccessRequestSummary(
            request.Id, request.ApplicationId, request.Application.Name, request.UserId,
            request.User.Username, request.User.Email, request.Status, request.Reason,
            request.RequestedAt, request.ReviewedAt, request.DecisionNote));
    }

    public sealed record AccessRequestDecision(bool Approve, string? Note);
    public sealed record AdminAccessRequestSummary(
        Guid Id, Guid ApplicationId, string ApplicationName, Guid UserId, string Username,
        string Email, string Status, string Reason, DateTimeOffset RequestedAt,
        DateTimeOffset? ReviewedAt, string? DecisionNote);
}
