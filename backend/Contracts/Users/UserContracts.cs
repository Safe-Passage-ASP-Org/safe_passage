namespace SafePassage.Api.Contracts.Users;

public sealed record UserSummary(
    Guid Id,
    string Username,
    string Email,
    string Role,
    bool IsActive,
    IReadOnlyList<string> Groups);

public sealed record CreateUserRequest(
    string Username,
    string Email,
    string Password,
    string Role,
    IReadOnlyList<string>? Groups);

public sealed record UpdateUserRequest(
    string Username,
    string Email,
    string Role,
    IReadOnlyList<string>? Groups);

public sealed record SetUserStatusRequest(bool IsActive);
