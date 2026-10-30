using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/applications")]
public sealed class ApplicationsController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationSummary>>> GetAll(CancellationToken cancellationToken)
    {
        var applications = await dbContext.Applications
            .AsNoTracking()
            .OrderBy(application => application.Name)
            .Select(application => new ApplicationSummary(
                application.Id,
                application.Name,
                application.Description,
                application.UserAccess.Count,
                application.GroupAccess.Count))
            .ToListAsync(cancellationToken);

        return Ok(applications);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApplicationSummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new ApplicationSummary(
                candidate.Id,
                candidate.Name,
                candidate.Description,
                candidate.UserAccess.Count,
                candidate.GroupAccess.Count))
            .SingleOrDefaultAsync(cancellationToken);

        return application is null ? NotFound() : Ok(application);
    }

    [HttpGet("{id:guid}/access")]
    public async Task<ActionResult<ApplicationAccessSummary>> GetAccess(Guid id, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Applications.AnyAsync(application => application.Id == id, cancellationToken);
        if (!exists) return NotFound();
        var users = await dbContext.ApplicationUserAccesses.AsNoTracking()
            .Where(access => access.ApplicationId == id).Select(access => access.UserId).ToListAsync(cancellationToken);
        var groups = await dbContext.ApplicationGroupAccesses.AsNoTracking()
            .Where(access => access.ApplicationId == id).Select(access => access.GroupId).ToListAsync(cancellationToken);
        return Ok(new ApplicationAccessSummary(users, groups));
    }

    [HttpPut("{id:guid}/access")]
    public async Task<ActionResult<ApplicationAccessSummary>> SetAccess(
        Guid id, [FromBody] ApplicationAccessRequest request, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Applications.AnyAsync(application => application.Id == id, cancellationToken);
        if (!exists) return NotFound();
        var userIds = (request.UserIds ?? []).Distinct().ToList();
        var groupIds = (request.GroupIds ?? []).Distinct().ToList();
        if (await dbContext.Users.CountAsync(user => userIds.Contains(user.Id), cancellationToken) != userIds.Count
            || await dbContext.Groups.CountAsync(group => groupIds.Contains(group.Id), cancellationToken) != groupIds.Count)
            return BadRequest(new { message = "One or more selected users or groups no longer exist." });

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var existingUsers = await dbContext.ApplicationUserAccesses.Where(access => access.ApplicationId == id).ToListAsync(cancellationToken);
        var existingGroups = await dbContext.ApplicationGroupAccesses.Where(access => access.ApplicationId == id).ToListAsync(cancellationToken);
        dbContext.ApplicationUserAccesses.RemoveRange(existingUsers);
        dbContext.ApplicationGroupAccesses.RemoveRange(existingGroups);
        dbContext.ApplicationUserAccesses.AddRange(userIds.Select(userId => new ApplicationUserAccess { ApplicationId = id, UserId = userId }));
        dbContext.ApplicationGroupAccesses.AddRange(groupIds.Select(groupId => new ApplicationGroupAccess { ApplicationId = id, GroupId = groupId }));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new ApplicationAccessSummary(userIds, groupIds));
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationSummary>> Create([FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Application name is required." });

        var application = new Application
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty
        };

        dbContext.Applications.Add(application);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = application.Id }, new ApplicationSummary(application.Id, application.Name, application.Description, 0, 0));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApplicationSummary>> Update(Guid id, [FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (application is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Application name is required." });

        application.Name = request.Name.Trim();
        application.Description = request.Description?.Trim() ?? string.Empty;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new ApplicationSummary(application.Id, application.Name, application.Description, application.UserAccess.Count, application.GroupAccess.Count));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (application is null)
            return NotFound();

        dbContext.Applications.Remove(application);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record ApplicationRequest(string? Name, string? Description);
    public sealed record ApplicationSummary(Guid Id, string Name, string Description, int UserAccessCount, int GroupAccessCount);
    public sealed record ApplicationAccessRequest(List<Guid>? UserIds, List<Guid>? GroupIds);
    public sealed record ApplicationAccessSummary(List<Guid> UserIds, List<Guid> GroupIds);
}
