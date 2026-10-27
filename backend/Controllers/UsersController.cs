using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Users;
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
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> GetAll(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.AsNoTracking()
            .Include(user => user.UserGroups)
            .ThenInclude(link => link.Group)
            .OrderBy(user => user.Username)
            .ToListAsync(cancellationToken);

        return Ok(users.Select(ToSummary).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<UserSummary>> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var validation = Validate(request.Username, request.Email, request.Role);
        if (validation is not null)
            return BadRequest(new { message = validation });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 12)
            return BadRequest(new { message = "A password of at least 12 characters is required." });

        var username = request.Username.Trim();
        var email = request.Email.Trim();
        if (await dbContext.Users.AnyAsync(user => user.Username == username || user.Email == email, cancellationToken))
            return Conflict(new { message = "A user with that username or email already exists." });

        var groups = await ResolveGroupsAsync(request.Groups, cancellationToken);
        if (groups is null)
            return BadRequest(new { message = "One or more selected groups do not exist." });

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            Role = request.Role.Trim(),
            IsActive = true
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        foreach (var group in groups)
            user.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id, User = user, Group = group });

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = user.Id }, ToSummary(user));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserSummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .Include(candidate => candidate.UserGroups).ThenInclude(link => link.Group)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return user is null ? NotFound() : Ok(ToSummary(user));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserSummary>> Update(
        Guid id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var validation = Validate(request.Username, request.Email, request.Role);
        if (validation is not null)
            return BadRequest(new { message = validation });

        var user = await dbContext.Users
            .Include(candidate => candidate.UserGroups)
            .ThenInclude(link => link.Group)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        var username = request.Username.Trim();
        var email = request.Email.Trim();
        var duplicate = await dbContext.Users.AnyAsync(candidate => candidate.Id != id
            && (candidate.Username == username || candidate.Email == email), cancellationToken);
        if (duplicate)
            return Conflict(new { message = "A user with that username or email already exists." });

        var groups = await ResolveGroupsAsync(request.Groups, cancellationToken);
        if (groups is null)
            return BadRequest(new { message = "One or more selected groups do not exist." });

        user.Username = username;
        user.Email = email;
        user.Role = request.Role.Trim();
        var selectedGroupIds = groups.Select(group => group.Id).ToHashSet();
        foreach (var membership in user.UserGroups.Where(link => !selectedGroupIds.Contains(link.GroupId)).ToList())
            user.UserGroups.Remove(membership);
        var currentGroupIds = user.UserGroups.Select(link => link.GroupId).ToHashSet();
        foreach (var group in groups.Where(group => !currentGroupIds.Contains(group.Id)))
            user.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id, User = user, Group = group });

        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToSummary(user));
    }

    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<UserSummary>> SetStatus(
        Guid id,
        [FromBody] SetUserStatusRequest request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(candidate => candidate.UserGroups).ThenInclude(link => link.Group)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        if (!request.IsActive && User.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id.ToString())
            return BadRequest(new { message = "You cannot deactivate your own account." });

        user.IsActive = request.IsActive;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToSummary(user));
    }

    private async Task<List<Group>?> ResolveGroupsAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        var normalized = (names ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var groups = await dbContext.Groups.Where(group => normalized.Contains(group.Name))
            .ToListAsync(cancellationToken);
        return groups.Count == normalized.Count ? groups : null;
    }

    private static string? Validate(string? username, string? email, string? role)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > 100)
            return "Username is required and must be 100 characters or fewer.";
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 320 || !email.Contains('@'))
            return "A valid email address is required.";
        if (string.IsNullOrWhiteSpace(role) || role.Trim().Length > 64)
            return "Role is required and must be 64 characters or fewer.";
        return null;
    }

    private static UserSummary ToSummary(User user) => new(
        user.Id,
        user.Username,
        user.Email,
        user.Role,
        user.IsActive,
        user.UserGroups.Select(link => link.Group.Name).OrderBy(name => name).ToList());
}
