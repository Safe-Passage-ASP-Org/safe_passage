using SafePassage.Api.Contracts.Auth;

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
    DeviceSigningKeyNotRegistered
}

public sealed record AuthFlowResult<T>(T? Response, AuthFlowFailure Failure)
{
    public static AuthFlowResult<T> Success(T response) => new(response, AuthFlowFailure.None);
    public static AuthFlowResult<T> Failed(AuthFlowFailure failure) => new(default, failure);
}

public interface IAuthFlowService
{
    Task<AuthFlowResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaActionResponse>> ApproveMfaAsync(MfaApprovalRequest request, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaActionResponse>> DenyMfaAsync(MfaDenialRequest request, CancellationToken cancellationToken);
    Task<AuthFlowResult<MfaStatusResponse>> GetMfaStatusAsync(string sessionToken, CancellationToken cancellationToken);
}