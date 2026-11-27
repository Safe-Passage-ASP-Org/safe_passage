using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/groups")]
public sealed class GroupsController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupSummary>>> GetAll(CancellationToken cancellationToken)
    {
        var groups = await dbContext.Groups
            .AsNoTracking()
            .OrderBy(group => group.Name)
            .Include(group => group.UserGroups)
            .ToListAsync(cancellationToken);

        return Ok(groups.Select(ToSummary).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GroupSummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups
            .AsNoTracking()
            .Include(candidate => candidate.UserGroups)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return group is null ? NotFound() : Ok(ToSummary(group));
    }

    [HttpPost]
    public async Task<ActionResult<GroupSummary>> Create([FromBody] GroupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Group name is required." });

        var name = request.Name.Trim();
        var exists = await dbContext.Groups.AnyAsync(candidate => candidate.Name == name, cancellationToken);
        if (exists)
            return Conflict(new { message = "A group with that name already exists." });

        var memberUserIds = await ResolveMemberUserIdsAsync(request.MemberUserIds, cancellationToken);
        if (memberUserIds is null)
            return BadRequest(new { message = "One or more selected users no longer exist." });

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty
        };

        dbContext.Groups.Add(group);
        dbContext.UserGroups.AddRange(memberUserIds.Select(userId => new UserGroup
        {
            UserId = userId,
            GroupId = group.Id
        }));
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = group.Id },
            new GroupSummary(group.Id, group.Name, group.Description, memberUserIds.Count, memberUserIds));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<GroupSummary>> Update(Guid id, [FromBody] GroupRequest request, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups
            .Include(candidate => candidate.UserGroups)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (group is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Group name is required." });

        var name = request.Name.Trim();
        var duplicate = await dbContext.Groups.AnyAsync(candidate => candidate.Id != id && candidate.Name == name, cancellationToken);
        if (duplicate)
            return Conflict(new { message = "A group with that name already exists." });

        var memberUserIds = request.MemberUserIds is null
            ? group.UserGroups.Select(membership => membership.UserId).ToList()
            : await ResolveMemberUserIdsAsync(request.MemberUserIds, cancellationToken);
        if (memberUserIds is null)
            return BadRequest(new { message = "One or more selected users no longer exist." });

        group.Name = name;
        group.Description = request.Description?.Trim() ?? string.Empty;
        var desiredMemberIds = memberUserIds.ToHashSet();
        var existingMemberIds = group.UserGroups.Select(membership => membership.UserId).ToHashSet();
        var existingMembersToRemove = group.UserGroups
            .Where(membership => !desiredMemberIds.Contains(membership.UserId))
            .ToList();
        dbContext.UserGroups.RemoveRange(existingMembersToRemove);
        dbContext.UserGroups.AddRange(memberUserIds
            .Where(userId => !existingMemberIds.Contains(userId))
            .Select(userId => new UserGroup
            {
                UserId = userId,
                GroupId = group.Id
            }));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new GroupSummary(group.Id, group.Name, group.Description, memberUserIds.Count, memberUserIds));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (group is null)
            return NotFound();

        dbContext.Groups.Remove(group);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record GroupRequest(string? Name, string? Description, List<Guid>? MemberUserIds = null);
    public sealed record GroupSummary(Guid Id, string Name, string Description, int MemberCount, List<Guid> MemberUserIds);

    private async Task<List<Guid>?> ResolveMemberUserIdsAsync(
        List<Guid>? requestedUserIds,
        CancellationToken cancellationToken)
    {
        var memberUserIds = (requestedUserIds ?? []).Distinct().ToList();
        var existingUserCount = await dbContext.Users.CountAsync(
            user => memberUserIds.Contains(user.Id),
            cancellationToken);
        return existingUserCount == memberUserIds.Count ? memberUserIds : null;
    }

    private static GroupSummary ToSummary(Group group)
    {
        var memberUserIds = group.UserGroups.Select(membership => membership.UserId).ToList();
        return new GroupSummary(group.Id, group.Name, group.Description, memberUserIds.Count, memberUserIds);
    }
}
