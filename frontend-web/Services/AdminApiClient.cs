using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

namespace SafePassage.Web.Services;

public sealed class AdminApiClient(HttpClient http)
{
    public string? AccessToken { get; private set; }
    public string? CurrentUsername { get; private set; }
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

    public void SignOut() { SetToken(null); CurrentUsername = null; }

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

    public async Task SaveApplicationAsync(AdminApplicationForm application, Guid? id, CancellationToken cancellationToken = default)
    {
        var payload = new { application.Name, application.Description };
        using var response = id is null
            ? await http.PostAsJsonAsync("api/admin/applications", payload, cancellationToken)
            : await http.PutAsJsonAsync($"api/admin/applications/{id}", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

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
        CurrentRole = ReadRole(token);
        http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private static string? ReadRole(string? token)
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
            foreach (var claimType in new[] { "role", "roles", ClaimTypes.Role })
            {
                if (!document.RootElement.TryGetProperty(claimType, out var role))
                    continue;

                if (role.ValueKind == JsonValueKind.String)
                    return role.GetString();
                if (role.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in role.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.String)
                            return value.GetString();
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
}

public sealed record LoginResult(bool MfaRequired, string? SessionToken, string Message);
public sealed record AdminUser(Guid Id, string Username, string Email, bool IsActive, bool IsEmailVerified,
    string Role, List<string> Groups, string? FullName = null, string? PhoneNumber = null);
public sealed record AdminGroup(Guid Id, string Name, string Description, int MemberCount);
public sealed class AdminGroupForm { public string Name { get; set; } = ""; public string Description { get; set; } = ""; }
public sealed record AdminDevice(Guid Id, string DeviceId, string DeviceName, string DeviceType, string OS,
    DateTimeOffset RegistrationDate, DateTimeOffset? LastActiveDate, string Status, string TrustStatus, Guid UserId);
public sealed record AdminApplication(Guid Id, string Name, string Description, int UserAccessCount, int GroupAccessCount);
public sealed record AccessibleApplication(Guid Id, string Name, string Description);
public sealed class AdminApplicationForm { public string Name { get; set; } = ""; public string Description { get; set; } = ""; }
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
