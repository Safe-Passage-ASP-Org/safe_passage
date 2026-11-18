using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/integrations/access")]
public sealed class IntegrationAccessController(
    IApplicationAccessVerificationService accessVerificationService,
    IAuthFlowService authFlow,
    IMemoryCache cache) : ControllerBase
{
    [HttpPost("verify")]
    [ProducesResponseType<ApplicationAccessVerificationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApplicationAccessVerificationResult>> Verify(
        [FromBody] AccessVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ApplicationId == Guid.Empty)
            return BadRequest(new { message = "A valid application ID is required." });

        var userClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(userClaim, out var userId))
            return Unauthorized();

        var result = await accessVerificationService.VerifyAsync(
            userId,
            User.FindFirstValue("device_id"),
            request.ApplicationId,
            cancellationToken);

        if (result.Reason == "security_policy_unavailable")
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Access policy unavailable",
                detail: "The application access decision could not be evaluated.");
        }

        return Ok(result);
    }

    [HttpPost("request-approval")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LoginResponse>> RequestBrowserAccessApproval(
        [FromBody] BrowserAccessRequest request,
        CancellationToken cancellationToken)
    {
        var userClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(userClaim, out var userId))
            return Unauthorized();

        if (!Uri.TryCreate(request.Origin, UriKind.Absolute, out var requestedUri)
            || (requestedUri.Scheme != Uri.UriSchemeHttp && requestedUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { message = "A valid HTTP or HTTPS site origin is required." });
        }

        var origin = requestedUri.GetLeftPart(UriPartial.Authority);
        var cacheKey = $"browser-access:{userId}:{origin}";
        if (cache.TryGetValue(cacheKey, out _))
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Approval request already sent",
                detail: "Wait 15 seconds before requesting approval for this site again.");
        }

        var result = await authFlow.RequestBrowserAccessApprovalAsync(
            userId,
            User.FindFirstValue("device_id"),
            origin,
            cancellationToken);
        if (result.Failure != AuthFlowFailure.None)
            return ToProblem(result.Failure);

        cache.Set(cacheKey, true, TimeSpan.FromSeconds(15));
        return Accepted(result.Response);
    }

    private ObjectResult ToProblem(AuthFlowFailure failure)
    {
        var (statusCode, title, detail) = failure switch
        {
            AuthFlowFailure.NoActiveMobileDevice => (StatusCodes.Status409Conflict, "MFA device unavailable", "No active mobile device is registered for this account."),
            AuthFlowFailure.DeviceUnavailable => (StatusCodes.Status409Conflict, "MFA device unavailable", "The registered approval device is no longer active."),
            AuthFlowFailure.DeviceSigningKeyNotRegistered => (StatusCodes.Status409Conflict, "Device key enrollment required", "The approval device must be enrolled with a signing key."),
            AuthFlowFailure.NotificationFailed => (StatusCodes.Status503ServiceUnavailable, "MFA notification unavailable", "The approval request could not be delivered. Try again later."),
            AuthFlowFailure.UserUnavailable => (StatusCodes.Status403Forbidden, "Account unavailable", "The account is no longer active."),
            _ => (StatusCodes.Status500InternalServerError, "Authentication error", "The approval request could not be completed.")
        };

        return Problem(statusCode: statusCode, title: title, detail: detail);
    }

    public sealed record AccessVerificationRequest(Guid ApplicationId);
    public sealed record BrowserAccessRequest([Required, StringLength(512, MinimumLength = 1)] string Origin);
}
