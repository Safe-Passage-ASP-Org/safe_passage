using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/integrations/access")]
public sealed class IntegrationAccessController(
    IApplicationAccessVerificationService accessVerificationService) : ControllerBase
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

    public sealed record AccessVerificationRequest(Guid ApplicationId);
}
