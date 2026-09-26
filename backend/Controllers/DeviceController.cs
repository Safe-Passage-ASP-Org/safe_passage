using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SafePassage.Api.Contracts.Devices;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

    [Authorize]
[Route("api/devices")]
public sealed class DeviceController(IDeviceManagementService deviceManagement) : ControllerBase
{
    /// <summary>Registers this authenticated user's device for administrator approval.</summary>
    [Authorize]
    [HttpPost("register")]
    [ProducesResponseType<DeviceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceResponse>> Register(
        [FromBody] DeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await deviceManagement.RegisterAsync(userId, request, cancellationToken);
        if (result.Failure == DeviceManagementFailure.None)
            return CreatedAtAction(nameof(GetById), new { id = result.Response!.Id }, result.Response);

        return ToProblem(result.Failure);
    }

    /// <summary>Lists registered devices for administrators.</summary>
    [Authorize(Roles = "Admin,Administrator,Super Admin")]
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DeviceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeviceResponse>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await deviceManagement.GetAllAsync(cancellationToken));

    /// <summary>Returns one registered device to its owner or an administrator.</summary>
    [Authorize(Roles = "Admin,Administrator,Super Admin")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DeviceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DeviceResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized();

        var result = await deviceManagement.GetByIdAsync(id, cancellationToken);
        if (result.Failure == DeviceManagementFailure.None
            && !IsAdministrator()
            && result.Response!.UserId != requesterId)
            return Forbid();

        return result.Failure == DeviceManagementFailure.None
            ? Ok(result.Response)
            : ToProblem(result.Failure);
    }

    /// <summary>Approves and trusts a registered device.</summary>
    [Authorize(Roles = "Admin,Administrator,Super Admin")]
    [HttpPut("{id:guid}/trust")]
    [ProducesResponseType<DeviceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceResponse>> Trust(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var actorId))
            return Unauthorized();

        var result = await deviceManagement.TrustAsync(id, actorId, cancellationToken);
        return result.Failure == DeviceManagementFailure.None
            ? Ok(result.Response)
            : ToProblem(result.Failure);
    }

    /// <summary>Revokes and disables a registered device.</summary>
    [Authorize(Roles = "Admin,Administrator,Super Admin")]
    [HttpPut("{id:guid}/revoke")]
    [ProducesResponseType<DeviceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceResponse>> Revoke(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var actorId))
            return Unauthorized();

        var result = await deviceManagement.RevokeAsync(id, actorId, cancellationToken);
        return result.Failure == DeviceManagementFailure.None
            ? Ok(result.Response)
            : ToProblem(result.Failure);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out userId);
    }

    private bool IsAdministrator() =>
        User.IsInRole("Admin") || User.IsInRole("Administrator") || User.IsInRole("Super Admin");

    private ObjectResult ToProblem(DeviceManagementFailure failure)
    {
        var (statusCode, title, detail) = failure switch
        {
            DeviceManagementFailure.UserUnavailable => (StatusCodes.Status403Forbidden, "Account unavailable", "The account is not active."),
            DeviceManagementFailure.DuplicateDevice => (StatusCodes.Status409Conflict, "Device already registered", "This hardware fingerprint is already registered."),
            DeviceManagementFailure.NotFound => (StatusCodes.Status404NotFound, "Device not found", "The requested device does not exist."),
            _ => (StatusCodes.Status500InternalServerError, "Device operation failed", "The device operation could not be completed.")
        };

        return Problem(statusCode: statusCode, title: title, detail: detail);
    }
}