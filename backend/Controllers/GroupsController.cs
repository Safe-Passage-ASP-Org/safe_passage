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
            .Select(group => new GroupSummary(
                group.Id,
                group.Name,
                group.Description,
                group.UserGroups.Count))
            .ToListAsync(cancellationToken);

        return Ok(groups);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GroupSummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new GroupSummary(
                candidate.Id,
                candidate.Name,
                candidate.Description,
                candidate.UserGroups.Count))
            .SingleOrDefaultAsync(cancellationToken);

        return group is null ? NotFound() : Ok(group);
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

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty
        };

        dbContext.Groups.Add(group);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = group.Id }, new GroupSummary(group.Id, group.Name, group.Description, 0));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<GroupSummary>> Update(Guid id, [FromBody] GroupRequest request, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (group is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Group name is required." });

        var name = request.Name.Trim();
        var duplicate = await dbContext.Groups.AnyAsync(candidate => candidate.Id != id && candidate.Name == name, cancellationToken);
        if (duplicate)
            return Conflict(new { message = "A group with that name already exists." });

        group.Name = name;
        group.Description = request.Description?.Trim() ?? string.Empty;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new GroupSummary(group.Id, group.Name, group.Description, group.UserGroups.Count));
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

    public sealed record GroupRequest(string? Name, string? Description);
    public sealed record GroupSummary(Guid Id, string Name, string Description, int MemberCount);
}
