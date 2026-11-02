using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/users")]
public sealed class UsersController(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IAccountActivationService accountActivationService) : ControllerBase
{
    private static readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase)
        { "Admin", "Administrator", "Super Admin", "Auditor", "Operator", "User" };

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> GetAll(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.AsNoTracking()
            .OrderBy(user => user.Username)
            .Select(user => new UserSummary(user.Id, user.Username, user.Email, user.IsActive, user.IsEmailVerified, user.Role,
                user.UserGroups.Select(link => link.Group.Name).OrderBy(name => name).ToList()))
            .ToListAsync(cancellationToken);
        return Ok(users);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserSummary>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new UserSummary(candidate.Id, candidate.Username, candidate.Email, candidate.IsActive, candidate.IsEmailVerified, candidate.Role,
                candidate.UserGroups.Select(link => link.Group.Name).OrderBy(name => name).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost]
    public async Task<ActionResult<UserSummary>> Create([FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (validation is not null) return BadRequest(new { message = validation });

        var username = request.Username!.Trim();
        var email = request.Email!.Trim();
        if (await dbContext.Users.AnyAsync(user => user.Username == username || user.Email == email, cancellationToken))
            return Conflict(new { message = "That username or email is already in use." });

        var groupsResult = await ResolveGroupsAsync(request.Groups, cancellationToken);
        if (groupsResult.Error is not null) return BadRequest(new { message = groupsResult.Error });

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            Role = request.Role!.Trim(),
            IsActive = false,
            IsEmailVerified = false
        };
        var unusedPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.PasswordHash = passwordHasher.HashPassword(user, unusedPassword);
        foreach (var group in groupsResult.Groups)
            user.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        dbContext.Users.Add(user);
        AddAudit("User Invited", username);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            return Conflict(new { message = "That username or email is already in use." });
        }

        try
        {
            await accountActivationService.CreateAndSendInvitationAsync(user, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (EmailDeliveryException exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else
            {
                dbContext.AccountActivationTokens.RemoveRange(
                    dbContext.AccountActivationTokens.Local.Where(token => token.UserId == user.Id));
                dbContext.ActivityLogs.RemoveRange(
                    dbContext.ActivityLogs.Local.Where(activity => activity.Action == "User Invited"
                        && activity.Resource == username));
                dbContext.Users.Remove(user);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = exception.Message });
        }

        return CreatedAtAction(nameof(GetById), new { id = user.Id }, ToSummary(user, groupsResult.Groups));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserSummary>> Update(Guid id, [FromBody] UserRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (validation is not null) return BadRequest(new { message = validation });

        var user = await dbContext.Users.Include(candidate => candidate.UserGroups)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null) return NotFound();

        var username = request.Username!.Trim();
        var email = request.Email!.Trim();
        if (!user.IsEmailVerified && !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "The email address cannot be changed while account activation is pending." });
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
        if (request.IsActive && !user.IsEmailVerified)
            return BadRequest(new { message = "The account must be activated from its email invitation before it can be enabled." });
        if (!request.IsActive && user.Id.ToString() == User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value)
            return BadRequest(new { message = "You cannot deactivate your own administrator account." });
        user.IsActive = request.IsActive;
        AddAudit(request.IsActive ? "User Reactivated" : "User Suspended", user.Username);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? Validate(UserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Trim().Length > 100)
            return "Username is required and must be at most 100 characters.";
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 320
            || !new EmailAddressAttribute().IsValid(request.Email.Trim()))
            return "Enter a valid email address (maximum 320 characters).";
        if (string.IsNullOrWhiteSpace(request.Role) || !Roles.Contains(request.Role.Trim()))
            return "Choose a supported role.";
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
        new(user.Id, user.Username, user.Email, user.IsActive, user.IsEmailVerified, user.Role,
            groups.Select(group => group.Name).OrderBy(name => name).ToList());

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

    public sealed record UserRequest(string? Username, string? Email, string? Role, List<string>? Groups);
    public sealed record UserStatusRequest(bool IsActive);
    public sealed record UserSummary(Guid Id, string Username, string Email, bool IsActive, bool IsEmailVerified,
        string Role, List<string> Groups);
}
