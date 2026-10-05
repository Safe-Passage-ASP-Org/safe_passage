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
}
