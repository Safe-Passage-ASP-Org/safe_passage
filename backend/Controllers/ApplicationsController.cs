using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/applications")]
public sealed class ApplicationsController(ApplicationDbContext dbContext, IOpenIddictApplicationManager oidcApplications) : ControllerBase
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
                application.LaunchUrl,
                application.UserAccess.Count,
                application.GroupAccess.Count,
                application.ClientId,
                application.ClientType,
                application.RedirectUris,
                application.PostLogoutRedirectUris,
                application.AllowedScopes,
                application.IsPublished,
                application.AllowAccessRequests))
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
                candidate.LaunchUrl,
                candidate.UserAccess.Count,
                candidate.GroupAccess.Count,
                candidate.ClientId,
                candidate.ClientType,
                candidate.RedirectUris,
                candidate.PostLogoutRedirectUris,
                candidate.AllowedScopes,
                candidate.IsPublished,
                candidate.AllowAccessRequests))
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
            Description = request.Description?.Trim() ?? string.Empty,
            LaunchUrl = NormalizeLaunchUrl(request.LaunchUrl),
            ClientId = Guid.NewGuid().ToString("N"),
            ClientType = request.ClientType == "public" ? "public" : "confidential",
            RedirectUris = NormalizeUris(request.RedirectUris),
            PostLogoutRedirectUris = NormalizeUris(request.PostLogoutRedirectUris),
            AllowedScopes = NormalizeScopes(request.AllowedScopes),
            IsPublished = request.IsPublished,
            AllowAccessRequests = request.IsPublished && request.AllowAccessRequests
        };

        if (!ValidOidcConfiguration(application, out var validationError))
            return BadRequest(new { message = validationError });

        dbContext.Applications.Add(application);
        await dbContext.SaveChangesAsync(cancellationToken);
        var secret = await RegisterClientAsync(application, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = application.Id }, ToSummary(application, 0, 0, secret));
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
        application.LaunchUrl = NormalizeLaunchUrl(request.LaunchUrl);
        application.ClientId ??= Guid.NewGuid().ToString("N");
        application.ClientType = request.ClientType == "public" ? "public" : "confidential";
        application.RedirectUris = NormalizeUris(request.RedirectUris);
        application.PostLogoutRedirectUris = NormalizeUris(request.PostLogoutRedirectUris);
        application.AllowedScopes = NormalizeScopes(request.AllowedScopes);
        application.IsPublished = request.IsPublished;
        application.AllowAccessRequests = request.IsPublished && request.AllowAccessRequests;
        if (!ValidOidcConfiguration(application, out var validationError))
            return BadRequest(new { message = validationError });
        await dbContext.SaveChangesAsync(cancellationToken);
        var secret = await RegisterClientAsync(application, cancellationToken);
        return Ok(ToSummary(application, application.UserAccess.Count, application.GroupAccess.Count, secret));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (application is null)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(application.ClientId))
        {
            var oidcApp = await oidcApplications.FindByClientIdAsync(application.ClientId, cancellationToken);
            if (oidcApp is not null)
                await oidcApplications.DeleteAsync(oidcApp, cancellationToken);
        }

        dbContext.Applications.Remove(application);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string NormalizeUris(string? value) => string.Join('\n', (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal));
    private static string NormalizeScopes(string? value) => string.Join(' ', (value ?? "openid profile email").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal));
    private static string NormalizeLaunchUrl(string? value) => value?.Trim() ?? string.Empty;
    private static bool ValidOidcConfiguration(Application app, out string error)
    {
        var scopes = app.AllowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid") || scopes.Except(new[] { "openid", "profile", "email", "roles" }).Any())
        { error = "Allowed scopes must include openid and may only contain profile, email, and roles."; return false; }
        var uris = app.RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (uris.Length == 0 || uris.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))))
        { error = "Add at least one exact HTTPS callback URL. HTTP is allowed only for localhost development."; return false; }
        var logoutUris = app.PostLogoutRedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (logoutUris.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))))
        { error = "Post logout callback URLs must use HTTPS. HTTP is allowed only for localhost development."; return false; }
        if (!string.IsNullOrEmpty(app.LaunchUrl)
            && (!Uri.TryCreate(app.LaunchUrl, UriKind.Absolute, out var launchUri)
                || (launchUri.Scheme != Uri.UriSchemeHttps && !(launchUri.IsLoopback && launchUri.Scheme == Uri.UriSchemeHttp))))
        { error = "The launch URL must use HTTPS. HTTP is allowed only for localhost development."; return false; }
        error = ""; return true;
    }
    private async Task<string?> RegisterClientAsync(Application app, CancellationToken ct)
    {
        var old = await oidcApplications.FindByClientIdAsync(app.ClientId!, ct);
        var secret = app.ClientType == "confidential" ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(36)) : null;
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = app.ClientId,
            ClientSecret = secret,
            ClientType = app.ClientType == "public" ? OpenIddictConstants.ClientTypes.Public : OpenIddictConstants.ClientTypes.Confidential,
            ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
            DisplayName = app.Name
        };
        descriptor.Properties["safe_passage_application_id"] = System.Text.Json.JsonSerializer.SerializeToElement(app.Id.ToString());
        foreach (var uri in app.RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in app.PostLogoutRedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
        foreach (var scope in app.AllowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scope);
        descriptor.Requirements.Add(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        if (old is null) await oidcApplications.CreateAsync(descriptor, ct);
        else await oidcApplications.UpdateAsync(old, descriptor, ct);
        return secret;
    }
    private static ApplicationSummary ToSummary(Application app, int users, int groups, string? secret = null) => new(app.Id, app.Name, app.Description, app.LaunchUrl, users, groups, app.ClientId, app.ClientType, app.RedirectUris, app.PostLogoutRedirectUris, app.AllowedScopes, app.IsPublished, app.AllowAccessRequests, secret);
    public sealed record ApplicationRequest(string? Name, string? Description, string? ClientType, string? RedirectUris, string? PostLogoutRedirectUris, string? AllowedScopes, string? LaunchUrl = null, bool IsPublished = false, bool AllowAccessRequests = false);
    public sealed record ApplicationSummary(Guid Id, string Name, string Description, string LaunchUrl, int UserAccessCount, int GroupAccessCount, string? ClientId, string ClientType, string RedirectUris, string PostLogoutRedirectUris, string AllowedScopes, bool IsPublished = false, bool AllowAccessRequests = false, string? ClientSecret = null);
    public sealed record ApplicationAccessRequest(List<Guid>? UserIds, List<Guid>? GroupIds);
    public sealed record ApplicationAccessSummary(List<Guid> UserIds, List<Guid> GroupIds);
}
