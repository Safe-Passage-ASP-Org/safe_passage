using Microsoft.AspNetCore.Mvc;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthFlowService authFlow) : ControllerBase
{
    /// <summary>Validates credentials and starts a device-based MFA challenge.</summary>
    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.LoginAsync(request, cancellationToken);
        if (result.Failure == AuthFlowFailure.None)
            return result.Response!.MfaRequired ? Accepted(result.Response) : Ok(result.Response);

        return ToProblem(result.Failure);
    }

    /// <summary>Approves a pending MFA challenge from the mobile application.</summary>
    [HttpPost("mfa/approve")]
    [ProducesResponseType<MfaActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<ActionResult<MfaActionResponse>> ApproveMfa(
        [FromBody] MfaApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.ApproveMfaAsync(request, cancellationToken);
        if (result.Failure == AuthFlowFailure.None)
            return Ok(result.Response);

        return ToProblem(result.Failure);
    }

    /// <summary>Denies a pending MFA challenge and records a security alert.</summary>
    [HttpPost("mfa/deny")]
    [ProducesResponseType<MfaActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<ActionResult<MfaActionResponse>> DenyMfa(
        [FromBody] MfaDenialRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.DenyMfaAsync(request, cancellationToken);
        if (result.Failure == AuthFlowFailure.None)
            return Ok(result.Response);

        return ToProblem(result.Failure);
    }

    /// <summary>Long-polls an MFA challenge and returns a JWT after approval.</summary>
    [HttpGet("mfa/status/{sessionToken}")]
    [ProducesResponseType<MfaStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MfaStatusResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MfaStatusResponse>> GetMfaStatus(
        [FromRoute] string sessionToken,
        CancellationToken cancellationToken)
    {
        var result = await authFlow.GetMfaStatusAsync(sessionToken, cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);

        if (result.Response!.Status == MfaSessionStatus.Pending)
        {
            Response.Headers.RetryAfter = "1";
            return Accepted(result.Response);
        }

        return Ok(result.Response);
    }

    private ObjectResult ToProblem(AuthFlowFailure failure)
    {
        var (statusCode, title, detail) = failure switch
        {
            AuthFlowFailure.InvalidCredentials => (StatusCodes.Status401Unauthorized, "Sign-in failed", "The supplied credentials are invalid."),
            AuthFlowFailure.NoActiveMobileDevice => (StatusCodes.Status409Conflict, "MFA device unavailable", "No active mobile device is registered for this account."),
            AuthFlowFailure.NotificationFailed => (StatusCodes.Status503ServiceUnavailable, "MFA notification unavailable", "The MFA request could not be delivered. Try again later."),
            AuthFlowFailure.InvalidApprovalSignature => (StatusCodes.Status401Unauthorized, "Approval rejected", "The MFA approval could not be validated."),
            AuthFlowFailure.SessionNotFound => (StatusCodes.Status404NotFound, "MFA session not found", "The requested MFA session does not exist."),
            AuthFlowFailure.SessionExpired => (StatusCodes.Status410Gone, "MFA session expired", "The MFA session has expired."),
            AuthFlowFailure.InvalidSessionState => (StatusCodes.Status409Conflict, "MFA session is not pending", "The MFA session has already been completed."),
            AuthFlowFailure.UserUnavailable => (StatusCodes.Status403Forbidden, "Account unavailable", "The account is no longer active."),
            AuthFlowFailure.DeviceUnavailable => (StatusCodes.Status409Conflict, "MFA device unavailable", "The registered device is no longer active."),
            AuthFlowFailure.DeviceSigningKeyNotRegistered => (StatusCodes.Status409Conflict, "Device key enrollment required", "The approving device must be enrolled with a signing key before it can approve MFA."),
            _ => (StatusCodes.Status500InternalServerError, "Authentication error", "The authentication request could not be completed.")
        };

        return Problem(statusCode: statusCode, title: title, detail: detail);
    }
}
