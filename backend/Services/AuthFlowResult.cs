using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public enum AuthFlowFailure
{
    None,
    InvalidCredentials,
    NoActiveMobileDevice,
    NotificationFailed,
    InvalidApprovalSignature,
    SessionNotFound,
    SessionExpired,
    InvalidSessionState,
    UserUnavailable,
    DeviceUnavailable,
    DeviceSigningKeyNotRegistered,
    SelfApprovalNotAllowed
}

public sealed record AuthFlowResult<T>(T? Response, AuthFlowFailure Failure)
{
    public static AuthFlowResult<T> Success(T response) => new(response, AuthFlowFailure.None);
    public static AuthFlowResult<T> Failed(AuthFlowFailure failure) => new(default, failure);
}

public interface IAuthFlowService
{
    Task<AuthFlowResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AuthFlowResult<LoginResponse>> LoginWithExternalProviderAsync(User user, string provider, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaActionResponse>> ApproveMfaAsync(MfaApprovalRequest request, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaActionResponse>> DenyMfaAsync(MfaDenialRequest request, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaActionResponse>> ResendMfaApprovalAsync(string sessionToken, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaStatusResponse>> GetMfaStatusAsync(string sessionToken, CancellationToken cancellationToken);
}
