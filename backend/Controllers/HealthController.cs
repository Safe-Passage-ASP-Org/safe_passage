using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SafePassage.Api.Data;

namespace SafePassage.Api.Controllers;

[AllowAnonymous]
[ApiController]
public sealed class HealthController(
    ApplicationDbContext dbContext,
    ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>Process liveness check for platform health probes.</summary>
    [HttpGet("/health")]
    [HttpGet("/health/live")]
    [ProducesResponseType<HealthCheckResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthCheckResponse> GetLive() =>
        Ok(new HealthCheckResponse(
            Status: "Healthy",
            Check: "liveness",
            Timestamp: DateTimeOffset.UtcNow,
            Dependencies: null));

    /// <summary>Readiness check verifying database connectivity without exposing sensitive details.</summary>
    [HttpGet("/health/ready")]
    [ProducesResponseType<HealthCheckResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthCheckResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthCheckResponse>> GetReady(CancellationToken cancellationToken)
    {
        var databaseHealthy = false;
        try
        {
            databaseHealthy = await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Readiness check failed while verifying database connectivity ({ExceptionType}).",
                ex.GetType().Name);
        }

        var response = new HealthCheckResponse(
            Status: databaseHealthy ? "Ready" : "Unhealthy",
            Check: "readiness",
            Timestamp: DateTimeOffset.UtcNow,
            Dependencies: new HealthDependenciesResponse(databaseHealthy ? "Healthy" : "Unhealthy"));

        return databaseHealthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}

public sealed record HealthCheckResponse(
    string Status,
    string Check,
    DateTimeOffset Timestamp,
    HealthDependenciesResponse? Dependencies);

public sealed record HealthDependenciesResponse(string Database);
