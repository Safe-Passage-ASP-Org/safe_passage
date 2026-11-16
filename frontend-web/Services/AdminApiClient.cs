using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

namespace SafePassage.Web.Services;

public sealed class AdminApiClient(HttpClient http)
{
    public string FacebookSignInUrl => new Uri(http.BaseAddress!, "api/auth/facebook").ToString();
    public string GitHubSignInUrl => new Uri(http.BaseAddress!, "api/auth/github").ToString();
    public string GitLabSignInUrl => new Uri(http.BaseAddress!, "api/auth/gitlab").ToString();
    public string? AccessToken { get; private set; }
    public string? CurrentUsername { get; private set; }
    public string? CurrentEmail { get; private set; }
    public string? CurrentRole { get; private set; }
    public bool IsAdministrator =>
        string.Equals(CurrentRole, "Admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CurrentRole, "Administrator", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CurrentRole, "Super Admin", StringComparison.OrdinalIgnoreCase);

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/auth/login", new { username, password }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(problem?.Detail ?? problem?.Title ?? "Sign in failed. Check your credentials and try again.");
        }

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
        if (body is null)
            throw new InvalidOperationException("The API returned an empty sign-in response.");

        CurrentUsername = username;

        if (!body.MfaRequired && !string.IsNullOrWhiteSpace(body.AccessToken))
            SetToken(body.AccessToken);

        return new(body.MfaRequired, body.SessionToken, body.Message ?? "Sign-in request submitted.");
    }

    public async Task<bool> IsFacebookSignInEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await http.GetFromJsonAsync<FacebookSignInSettings>("api/auth/facebook/enabled", cancellationToken);
            return settings?.Enabled == true;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<bool> IsGitHubSignInEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await http.GetFromJsonAsync<FacebookSignInSettings>("api/auth/github/enabled", cancellationToken);
            return settings?.Enabled == true;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<bool> IsGitLabSignInEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await http.GetFromJsonAsync<FacebookSignInSettings>("api/auth/gitlab/enabled", cancellationToken);
            return settings?.Enabled == true;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<LoginResult> ExchangeFacebookLoginAsync(string ticket, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/auth/facebook/exchange/{Uri.EscapeDataString(ticket)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(problem?.Detail ?? "The Facebook sign-in expired. Please try again.");
        }
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty Facebook sign-in response.");
        if (!body.MfaRequired && !string.IsNullOrWhiteSpace(body.AccessToken)) SetToken(body.AccessToken);
        return new(body.MfaRequired, body.SessionToken, body.Message ?? "Signed in with Facebook.");
    }

    public async Task<LoginResult> ExchangeGitHubLoginAsync(string ticket, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/auth/github/exchange/{Uri.EscapeDataString(ticket)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(problem?.Detail ?? "The GitHub sign-in expired. Please try again.");
        }
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty GitHub sign-in response.");
        if (!body.MfaRequired && !string.IsNullOrWhiteSpace(body.AccessToken)) SetToken(body.AccessToken);
        return new(body.MfaRequired, body.SessionToken, body.Message ?? "Signed in with GitHub.");
    }

    public async Task<LoginResult> ExchangeGitLabLoginAsync(string ticket, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/auth/gitlab/exchange/{Uri.EscapeDataString(ticket)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(problem?.Detail ?? "The GitLab sign-in expired. Please try again.");
        }
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty GitLab sign-in response.");
        if (!body.MfaRequired && !string.IsNullOrWhiteSpace(body.AccessToken)) SetToken(body.AccessToken);
        return new(body.MfaRequired, body.SessionToken, body.Message ?? "Signed in with GitLab.");
    }

    public async Task<bool> CompleteMfaAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/auth/mfa/status/{Uri.EscapeDataString(sessionToken)}", cancellationToken);
        var status = await response.Content.ReadFromJsonAsync<MfaStatusResponse>(cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return false;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(status?.Status ?? "Could not check MFA status.");
        if (string.Equals(status?.Status, "Approved", StringComparison.OrdinalIgnoreCase)
            && status is not null
            && !string.IsNullOrWhiteSpace(status.AccessToken))
        {
            SetToken(status.AccessToken);
            return true;
        }
        if (string.Equals(status?.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            return false;

        throw new InvalidOperationException($"The MFA request is {status?.Status ?? "unavailable"}. Please sign in again.");
    }

    public async Task ActivateAccountAsync(string token, string? password = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/auth/activate",
            new { token, password },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RegisterAccountAsync(AccountRegistrationForm registration, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/auth/register", new
        {
            registration.FullName,
            registration.Username,
            registration.Email,
            registration.PhoneNumber,
            registration.Password
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void SignOut() { SetToken(null); CurrentUsername = null; CurrentEmail = null; }

    public async Task<List<AdminUser>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminUser>>("api/admin/users", cancellationToken) ?? [];

    public async Task<List<AdminRole>> GetRolesAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminRole>>("api/admin/roles", cancellationToken) ?? [];

    public async Task<List<AdminGroup>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminGroup>>("api/admin/groups", cancellationToken) ?? [];

    public async Task SaveGroupAsync(AdminGroupForm group, Guid? id, CancellationToken cancellationToken = default)
    {
        var payload = new { group.Name, group.Description };
        using var response = id is null
            ? await http.PostAsJsonAsync("api/admin/groups", payload, cancellationToken)
            : await http.PutAsJsonAsync($"api/admin/groups/{id}", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/admin/groups/{id}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SaveUserAsync(AdminUserForm user, Guid? id, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        if (id is null)
        {
            response = await http.PostAsJsonAsync("api/admin/users", new
            {
                Username = user.Username, user.Email, user.Role, user.Groups
            }, cancellationToken);
        }
        else
        {
            response = await http.PutAsJsonAsync($"api/admin/users/{id}", new
            {
                user.Username, user.Email, user.Role, user.Groups
            }, cancellationToken);
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetUserActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/admin/users/{id}/status", new { isActive }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<List<AdminDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminDevice>>("api/devices", cancellationToken) ?? [];

    public async Task<List<AdminApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminApplication>>("api/admin/applications", cancellationToken) ?? [];

    public async Task<List<AccessibleApplication>> GetMyApplicationsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AccessibleApplication>>("api/applications/mine", cancellationToken) ?? [];

    public async Task<List<CatalogApplication>> GetApplicationCatalogAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<CatalogApplication>>("api/applications/catalog", cancellationToken) ?? [];

    public async Task<List<MyApplicationAccessRequest>> GetMyApplicationRequestsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<MyApplicationAccessRequest>>("api/applications/requests/mine", cancellationToken) ?? [];

    public async Task RequestApplicationAccessAsync(Guid applicationId, string reason, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/applications/{applicationId}/requests", new { reason }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<List<AdminAccessRequest>> GetAdminAccessRequestsAsync(string status = "Pending", CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminAccessRequest>>($"api/admin/access-requests?status={Uri.EscapeDataString(status)}", cancellationToken) ?? [];

    public async Task DecideApplicationAccessRequestAsync(Guid requestId, bool approve, string? note, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/admin/access-requests/{requestId}/decision", new { approve, note }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<List<AdminActivity>> GetAdminActivityAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminActivity>>("api/admin/activity", cancellationToken) ?? [];

    public async Task<List<AdminPolicy>> GetPoliciesAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<AdminPolicy>>("api/admin/policies", cancellationToken) ?? [];

    public async Task SavePolicyAsync(AdminPolicyForm policy, Guid? id, CancellationToken cancellationToken = default)
    {
        var payload = new { policy.PolicyName, policy.PolicyType, policy.IsEnforced, policy.RulesJson };
        using var response = id is null
            ? await http.PostAsJsonAsync("api/admin/policies", payload, cancellationToken)
            : await http.PutAsJsonAsync($"api/admin/policies/{id}", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string?> SaveApplicationAsync(AdminApplicationForm application, Guid? id, CancellationToken cancellationToken = default)
    {
        var payload = new { application.Name, application.Description, application.LaunchUrl, application.ClientType, application.RedirectUris, application.PostLogoutRedirectUris, application.AllowedScopes, application.IsPublished, application.AllowAccessRequests };
        using var response = id is null
            ? await http.PostAsJsonAsync("api/admin/applications", payload, cancellationToken)
            : await http.PutAsJsonAsync($"api/admin/applications/{id}", payload, cancellationToken);
        if (!response.IsSuccessStatusCode) await EnsureSuccessAsync(response, cancellationToken);
        var saved = await response.Content.ReadFromJsonAsync<AdminApplication>(cancellationToken: cancellationToken);
        return saved?.ClientSecret;
    }

    public async Task DeleteApplicationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/admin/applications/{id}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string> CreateOidcSessionAsync(string returnUrl, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("connect/session", new { returnUrl }, cancellationToken);
        if (!response.IsSuccessStatusCode) await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<OidcSessionResult>(cancellationToken: cancellationToken);
        return result?.CompletionUrl ?? throw new InvalidOperationException("The API did not return an OIDC sign-in continuation.");
    }

    public void ClearToken() { SetToken(null); CurrentUsername = null; CurrentEmail = null; }

    public async Task<AdminApplicationAccess> GetApplicationAccessAsync(Guid id, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<AdminApplicationAccess>($"api/admin/applications/{id}/access", cancellationToken)
        ?? new([], []);

    public async Task SaveApplicationAccessAsync(Guid id, AdminApplicationAccess access, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/admin/applications/{id}/access", access, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetDeviceTrustedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsync($"api/devices/{id}/trust", content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RevokeDeviceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsync($"api/devices/{id}/revoke", content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(problem?.Detail ?? problem?.Message ?? $"The API returned {(int)response.StatusCode}.");
        }
    }

    private void SetToken(string? token)
    {
        AccessToken = token;
        CurrentRole = ReadClaim(token, "role", "roles", ClaimTypes.Role);
        CurrentUsername = ReadClaim(token, "name", "unique_name", ClaimTypes.Name, "sub") ?? CurrentUsername;
        CurrentEmail = ReadClaim(token, "email", ClaimTypes.Email);
        http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private static string? ReadClaim(string? token, params string[] claimTypes)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length < 2)
            return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');

        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claimType in claimTypes)
            {
                if (!document.RootElement.TryGetProperty(claimType, out var element))
                    continue;

                if (element.ValueKind == JsonValueKind.String)
                {
                    var val = element.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var val = item.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) return val;
                        }
                    }
                }
            }
        }
        catch (FormatException) { }
        catch (JsonException) { }

        return null;
    }

    private sealed record LoginResponse(bool MfaRequired, string? SessionToken, string? AccessToken, string? Message);
    private sealed record MfaStatusResponse(string? Status, string? AccessToken);
    private sealed record ApiProblem(string? Detail, string? Message, string? Title);
    private sealed record OidcSessionResult(string CompletionUrl);
    private sealed record FacebookSignInSettings(bool Enabled);
}

public sealed record LoginResult(bool MfaRequired, string? SessionToken, string Message);
public sealed record AdminUser(Guid Id, string Username, string Email, bool IsActive, bool IsEmailVerified,
    string Role, List<string> Groups, string? FullName = null, string? PhoneNumber = null);
public sealed record AdminGroup(Guid Id, string Name, string Description, int MemberCount);
public sealed class AdminGroupForm { public string Name { get; set; } = ""; public string Description { get; set; } = ""; }
public sealed record AdminDevice(Guid Id, string DeviceId, string DeviceName, string DeviceType, string OS,
    DateTimeOffset RegistrationDate, DateTimeOffset? LastActiveDate, string Status, string TrustStatus, Guid UserId,
    bool HasSigningKey = false, bool HasPendingSigningKey = false);
public sealed record AdminApplication(Guid Id, string Name, string Description, int UserAccessCount, int GroupAccessCount,
    string LaunchUrl = "", string? ClientId = null, string ClientType = "confidential", string RedirectUris = "", string PostLogoutRedirectUris = "", string AllowedScopes = "openid profile email", bool IsPublished = false, bool AllowAccessRequests = false, string? ClientSecret = null);
public sealed record AccessibleApplication(Guid Id, string Name, string Description, string LaunchUrl);
public sealed record CatalogApplication(Guid Id, string Name, string Description, string LaunchUrl, bool IsAssigned, bool AllowAccessRequests, string? RequestStatus);
public sealed record MyApplicationAccessRequest(Guid Id, Guid ApplicationId, string ApplicationName, string Status, string Reason, DateTimeOffset RequestedAt, DateTimeOffset? ReviewedAt, string? DecisionNote);
public sealed record AdminAccessRequest(Guid Id, Guid ApplicationId, string ApplicationName, Guid UserId, string Username, string Email, string Status, string Reason, DateTimeOffset RequestedAt, DateTimeOffset? ReviewedAt, string? DecisionNote);
public sealed class AdminApplicationForm { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string LaunchUrl { get; set; } = ""; public string ClientType { get; set; } = "confidential"; public string RedirectUris { get; set; } = ""; public string PostLogoutRedirectUris { get; set; } = ""; public string AllowedScopes { get; set; } = "openid profile email"; public bool IsPublished { get; set; } public bool AllowAccessRequests { get; set; } }
public sealed record AdminApplicationAccess(List<Guid> UserIds, List<Guid> GroupIds);
public sealed record AdminActivity(Guid Id, DateTimeOffset Timestamp, string Username, string Action, string Resource, string Status);
public sealed record AdminPolicy(Guid Id, string PolicyName, string PolicyType, bool IsEnforced, string RulesJson);
public sealed class AdminPolicyForm { public string PolicyName { get; set; } = ""; public string PolicyType { get; set; } = "Authentication"; public bool IsEnforced { get; set; } = true; public string RulesJson { get; set; } = "{}"; }
public sealed record AdminRole(string Id, string Name, string Description);

public sealed class AdminUserForm
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "User";
    public List<string> Groups { get; set; } = [];
}

public sealed class AccountRegistrationForm
{
    public string FullName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string Password { get; set; } = "";
}
