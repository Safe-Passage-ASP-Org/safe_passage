using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/policies")]
public sealed class PoliciesController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PolicySummary>>> GetAll(CancellationToken cancellationToken)
    {
        var policies = await dbContext.SecurityPolicies
            .AsNoTracking()
            .OrderBy(policy => policy.PolicyName)
            .Select(policy => new PolicySummary(
                policy.Id,
                policy.PolicyName,
                policy.PolicyType,
                policy.IsEnforced,
                policy.RulesJson))
            .ToListAsync(cancellationToken);

        return Ok(policies);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PolicySummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var policy = await dbContext.SecurityPolicies
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new PolicySummary(
                candidate.Id,
                candidate.PolicyName,
                candidate.PolicyType,
                candidate.IsEnforced,
                candidate.RulesJson))
            .SingleOrDefaultAsync(cancellationToken);

        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpPost]
    public async Task<ActionResult<PolicySummary>> Create([FromBody] PolicyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PolicyName))
            return BadRequest(new { message = "Policy name is required." });

        var policy = new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = request.PolicyName.Trim(),
            PolicyType = request.PolicyType?.Trim() ?? "Authentication",
            IsEnforced = request.IsEnforced,
            RulesJson = request.RulesJson ?? "{}"
        };

        dbContext.SecurityPolicies.Add(policy);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = policy.Id }, new PolicySummary(policy.Id, policy.PolicyName, policy.PolicyType, policy.IsEnforced, policy.RulesJson));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PolicySummary>> Update(Guid id, [FromBody] PolicyRequest request, CancellationToken cancellationToken)
    {
        var policy = await dbContext.SecurityPolicies.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (policy is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.PolicyName))
            return BadRequest(new { message = "Policy name is required." });

        policy.PolicyName = request.PolicyName.Trim();
        policy.PolicyType = request.PolicyType?.Trim() ?? "Authentication";
        policy.IsEnforced = request.IsEnforced;
        policy.RulesJson = request.RulesJson ?? policy.RulesJson;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new PolicySummary(policy.Id, policy.PolicyName, policy.PolicyType, policy.IsEnforced, policy.RulesJson));
    }

    public sealed record PolicyRequest(string? PolicyName, string? PolicyType, bool IsEnforced, string? RulesJson);
    public sealed record PolicySummary(Guid Id, string PolicyName, string PolicyType, bool IsEnforced, string RulesJson);
}
