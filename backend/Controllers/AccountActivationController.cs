using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/auth")]
public sealed class AccountActivationController(
    IAccountActivationService accountActivationService) : ControllerBase
{
    [HttpPost("activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<IActionResult> Activate(
        [FromBody] ActivateAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequest(new { message = "An activation token is required." });
        if (string.IsNullOrWhiteSpace(request.Password)
            || request.Password.Length < 12
            || request.Password.Length > 1024)
        {
            return BadRequest(new { message = "Password must be between 12 and 1024 characters." });
        }

        var result = await accountActivationService.ActivateAsync(
            request.Token,
            request.Password,
            cancellationToken);
        return result == AccountActivationResult.Activated
            ? NoContent()
            : Problem(
                statusCode: StatusCodes.Status410Gone,
                title: "Activation link unavailable",
                detail: "This activation link is invalid, expired, or has already been used.");
    }

    public sealed record ActivateAccountRequest(string? Token, string? Password);
}
