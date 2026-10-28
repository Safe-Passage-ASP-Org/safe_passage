using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/users")]
public sealed class UsersController(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher) : ControllerBase
{
    private static readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase)
        { "Admin", "Administrator", "Super Admin", "Auditor", "Operator", "User" };

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> GetAll(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new UserSummary(user.Id, user.Username, user.Email, user.IsActive, user.Role,
                user.UserGroups.Select(link => link.Group.Name).OrderBy(name => name).ToList()))
            .ToListAsync(cancellationToken);
        return Ok(users);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserSummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new UserSummary(candidate.Id, candidate.Username, candidate.Email, candidate.IsActive, candidate.Role,
                candidate.UserGroups.Select(link => link.Group.Name).OrderBy(name => name).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost]
    public async Task<ActionResult<UserSummary>> Create([FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request, requirePassword: true);
        if (validation is not null) return BadRequest(new { message = validation });

        var username = request.Username!.Trim();
        var email = request.Email!.Trim();
        if (await dbContext.Users.AnyAsync(user => user.Username == username || user.Email == email, cancellationToken))
            return Conflict(new { message = "That username or email is already in use." });

        var groupsResult = await ResolveGroupsAsync(request.Groups, cancellationToken);
        if (groupsResult.Error is not null) return BadRequest(new { message = groupsResult.Error });

        var user = new User { Id = Guid.NewGuid(), Username = username, Email = email, Role = request.Role!.Trim(), IsActive = true };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password!);
        foreach (var group in groupsResult.Groups)
            user.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
        dbContext.Users.Add(user);
        AddAudit("User Created", username);
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return Conflict(new { message = "That username or email is already in use." }); }

        return CreatedAtAction(nameof(GetById), new { id = user.Id }, ToSummary(user, groupsResult.Groups));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserSummary>> Update(Guid id, [FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request, requirePassword: false);
        if (validation is not null) return BadRequest(new { message = validation });

        var user = await dbContext.Users.Include(candidate => candidate.UserGroups)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null) return NotFound();

        var username = request.Username!.Trim();
        var email = request.Email!.Trim();
        if (await dbContext.Users.AnyAsync(candidate => candidate.Id != id
            && (candidate.Username == username || candidate.Email == email), cancellationToken))
            return Conflict(new { message = "That username or email is already in use." });

        var groupsResult = await ResolveGroupsAsync(request.Groups, cancellationToken);
        if (groupsResult.Error is not null) return BadRequest(new { message = groupsResult.Error });

        user.Username = username;
        user.Email = email;
        user.Role = request.Role!.Trim();
        dbContext.UserGroups.RemoveRange(user.UserGroups);
        user.UserGroups.Clear();
        foreach (var group in groupsResult.Groups)
            user.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
        AddAudit("User Updated", username);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToSummary(user, groupsResult.Groups));
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] UserStatusRequest request, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null) return NotFound();
        if (!request.IsActive && user.Id.ToString() == User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value)
            return BadRequest(new { message = "You cannot deactivate your own administrator account." });
        user.IsActive = request.IsActive;
        AddAudit(request.IsActive ? "User Reactivated" : "User Suspended", user.Username);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? Validate(UserRequest request, bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Trim().Length > 100)
            return "Username is required and must be at most 100 characters.";
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 320
            || !new EmailAddressAttribute().IsValid(request.Email.Trim()))
            return "Enter a valid email address (maximum 320 characters).";
        if (string.IsNullOrWhiteSpace(request.Role) || !Roles.Contains(request.Role.Trim()))
            return "Choose a supported role.";
        if (requirePassword && (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 12 || request.Password.Length > 1024))
            return "Password is required and must be at least 12 characters.";
        return null;
    }

    private async Task<(List<Group> Groups, string? Error)> ResolveGroupsAsync(List<string>? requested, CancellationToken cancellationToken)
    {
        var names = (requested ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var groups = await dbContext.Groups.Where(group => names.Contains(group.Name)).ToListAsync(cancellationToken);
        if (groups.Count != names.Count) return ([], "One or more selected groups no longer exist. Refresh and try again.");
        return (groups, null);
    }

    private static UserSummary ToSummary(User user, IEnumerable<Group> groups) =>
        new(user.Id, user.Username, user.Email, user.IsActive, user.Role, groups.Select(group => group.Name).OrderBy(name => name).ToList());

    private void AddAudit(string action, string resource)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = Guid.TryParse(claim, out var actorId) ? actorId : null,
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Resource = resource,
            Status = ActivityLogStatus.Success
        });
    }

    public sealed record UserRequest(string? Username, string? Email, string? Password, string? Role, List<string>? Groups);
    public sealed record UserStatusRequest(bool IsActive);
    public sealed record UserSummary(Guid Id, string Username, string Email, bool IsActive, string Role, List<string> Groups);
}
