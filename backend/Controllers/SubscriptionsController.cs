using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SafePassage.Api.Contracts.Subscriptions;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/subscriptions")]
public sealed class SubscriptionsController(ISubscriptionService subscriptionService) : ControllerBase
{
    [HttpGet("plans")]
    [ProducesResponseType<IReadOnlyList<SubscriptionPlanResponse>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SubscriptionPlanResponse>> GetPlans() =>
        Ok(subscriptionService.GetPlans());

    [HttpGet("me")]
    [ProducesResponseType<UserSubscriptionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserSubscriptionResponse>> GetMySubscription(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var subscription = await subscriptionService.GetUserSubscriptionAsync(userId, cancellationToken);
        return Ok(subscription);
    }

    [HttpPost("checkout")]
    [ProducesResponseType<CreateCheckoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateCheckoutResponse>> CreateCheckout(
        [FromBody] CreateCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await subscriptionService.CreateCheckoutAsync(userId, request.PlanCode, cancellationToken);
        return result.Failure == SubscriptionFailure.None
            ? Ok(result.Data)
            : ToProblem(result.Failure, result.ErrorDetail);
    }

    [HttpPost("confirm")]
    [ProducesResponseType<UserSubscriptionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserSubscriptionResponse>> Confirm(
        [FromBody] ConfirmSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await subscriptionService.ConfirmSubscriptionAsync(userId, request.SubscriptionId, cancellationToken);
        return result.Failure == SubscriptionFailure.None
            ? Ok(result.Data)
            : ToProblem(result.Failure, result.ErrorDetail);
    }

    [HttpPost("abandon")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Abandon(
        [FromBody] AbandonSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await subscriptionService.AbandonPendingAsync(userId, request.SubscriptionId, cancellationToken);
        return result.Failure == SubscriptionFailure.None
            ? NoContent()
            : ToProblem(result.Failure, result.ErrorDetail);
    }

    [HttpPost("cancel")]
    [ProducesResponseType<UserSubscriptionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserSubscriptionResponse>> Cancel(
        [FromBody] CancelSubscriptionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await subscriptionService.CancelSubscriptionAsync(userId, request?.Reason, cancellationToken);
        return result.Failure == SubscriptionFailure.None
            ? Ok(result.Data)
            : ToProblem(result.Failure, result.ErrorDetail);
    }

    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var headers = new PayPalWebhookHeaders(
            Request.Headers["PAYPAL-TRANSMISSION-ID"].ToString(),
            Request.Headers["PAYPAL-TRANSMISSION-TIME"].ToString(),
            Request.Headers["PAYPAL-TRANSMISSION-SIG"].ToString(),
            Request.Headers["PAYPAL-CERT-URL"].ToString(),
            Request.Headers["PAYPAL-AUTH-ALGO"].ToString());

        var result = await subscriptionService.ProcessWebhookAsync(headers, rawBody, cancellationToken);
        return result.Failure == SubscriptionFailure.None
            ? Ok()
            : ToProblem(result.Failure, result.ErrorDetail);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out userId);
    }

    private ObjectResult ToProblem(SubscriptionFailure failure, string? detail)
    {
        var (statusCode, title) = failure switch
        {
            SubscriptionFailure.UserUnavailable => (StatusCodes.Status403Forbidden, "Account unavailable"),
            SubscriptionFailure.InvalidPlan => (StatusCodes.Status400BadRequest, "Invalid subscription plan"),
            SubscriptionFailure.PlanNotConfigured => (StatusCodes.Status503ServiceUnavailable, "PayPal Sandbox not configured"),
            SubscriptionFailure.AlreadySubscribed => (StatusCodes.Status409Conflict, "Subscription already active"),
            SubscriptionFailure.NotFound => (StatusCodes.Status404NotFound, "Subscription not found"),
            SubscriptionFailure.OwnershipMismatch => (StatusCodes.Status403Forbidden, "Subscription ownership mismatch"),
            SubscriptionFailure.InvalidState => (StatusCodes.Status400BadRequest, "Invalid subscription state"),
            SubscriptionFailure.InvalidWebhookSignature => (StatusCodes.Status401Unauthorized, "Invalid PayPal webhook signature"),
            SubscriptionFailure.ProviderError => (StatusCodes.Status502BadGateway, "PayPal API error"),
            _ => (StatusCodes.Status500InternalServerError, "Subscription operation failed")
        };

        return Problem(statusCode: statusCode, title: title, detail: detail);
    }
}
