using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Middleware;

public sealed class SecurityMiddleware(RequestDelegate next, ILogger<SecurityMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        ApplicationDbContext dbContext,
        ISecurityPolicyEvaluator policyEvaluator)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SensitiveApplicationAttribute>() is null)
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.Request.Headers["X-Application-Id"].ToString(), out var applicationId))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Application ID required", "A valid X-Application-Id header is required.");
            return;
        }

        var applicationExists = await dbContext.Applications.AnyAsync(
            application => application.Id == applicationId,
            context.RequestAborted);
        if (!applicationExists)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "Application not found", "The requested application is not registered.");
            return;
        }

        var policy = await policyEvaluator.EvaluateAsync(context.RequestAborted);
        if (!policy.IsValid)
        {
            logger.LogError("An enforced security policy contains invalid RulesJson.");
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "Security policy unavailable", "The device policy could not be evaluated.");
            return;
        }

        if (!policy.RequiresTrustedDevice)
        {
            await next(context);
            return;
        }

        var userClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(userClaim, out var userId))
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Authentication required", "Sign in before accessing this application.");
            return;
        }

        var deviceId = context.Request.Headers["X-Device-Id"].ToString();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Trusted device required", "This application requires a trusted device.");
            return;
        }

        var isTrusted = await dbContext.Devices.AnyAsync(device =>
            device.DeviceId == deviceId
            && device.UserId == userId
            && device.Status == DeviceStatus.Active
            && device.TrustStatus == DeviceTrustStatus.Trusted,
            context.RequestAborted);

        if (!isTrusted)
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Trusted device required", "This device is not trusted for the signed-in user.");
            return;
        }

        await next(context);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        }, context.RequestAborted);
    }
}