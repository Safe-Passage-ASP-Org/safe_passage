using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

namespace SafePassage.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/notifications")]
public sealed class UserNotificationsController(
    ApplicationDbContext dbContext,
    IUserNotificationPushSender pushSender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserNotificationResponse>>> GetMine(
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var notifications = await dbContext.UserNotifications.AsNoTracking()
            .Where(notification => notification.UserId == userId)
            .OrderByDescending(notification => notification.CreatedAt)
            .Take(100)
            .Select(notification => new UserNotificationResponse(
                notification.Id,
                notification.Category,
                notification.Title,
                notification.Message,
                notification.Href,
                notification.CreatedAt,
                notification.ReadAt))
            .ToListAsync(cancellationToken);
        return Ok(notifications);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var notification = await dbContext.UserNotifications
            .SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, cancellationToken);
        if (notification is null)
            return NotFound();

        notification.ReadAt ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var unread = await dbContext.UserNotifications
            .Where(item => item.UserId == userId && item.ReadAt == null)
            .ToListAsync(cancellationToken);
        var readAt = DateTimeOffset.UtcNow;
        foreach (var notification in unread)
            notification.ReadAt = readAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [Authorize(Roles = "Admin,Administrator,Super Admin")]
    [HttpPost("promotions")]
    public async Task<ActionResult<PromotionResponse>> SendPromotion(
        [FromBody] PromotionRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim();
        var message = request.Message?.Trim();
        var href = request.Href?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120)
            return BadRequest(new { message = "Title is required and must be 120 characters or fewer." });
        if (string.IsNullOrWhiteSpace(message) || message.Length > 2000)
            return BadRequest(new { message = "Message is required and must be 2,000 characters or fewer." });
        if (!string.IsNullOrEmpty(href)
            && (!href.StartsWith('/') || href.StartsWith("//", StringComparison.Ordinal) || href.Contains('\\')))
            return BadRequest(new { message = "Link must be a local application path." });

        var recipients = await dbContext.Users
            .Where(user => user.IsActive
                && user.Role.ToLower() != "admin"
                && user.Role.ToLower() != "administrator"
                && user.Role.ToLower() != "super admin")
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        var createdAt = DateTimeOffset.UtcNow;
        var notifications = recipients.Select(userId => new UserNotification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = "Promotion",
            Title = title,
            Message = message,
            Href = href,
            CreatedAt = createdAt
        }).ToList();

        if (notifications.Count > 0)
        {
            dbContext.UserNotifications.AddRange(notifications);
            await dbContext.SaveChangesAsync(cancellationToken);
            var registeredDevices = await dbContext.Devices.AsNoTracking()
                .Where(device => recipients.Contains(device.UserId)
                    && device.Status == DeviceStatus.Active
                    && device.PushToken != null)
                .Select(device => new { device.UserId, device.PushToken })
                .ToListAsync(cancellationToken);
            foreach (var group in registeredDevices.GroupBy(device => device.UserId))
            {
                var notification = notifications.First(item => item.UserId == group.Key);
                await pushSender.SendAsync(
                    group.Select(device => device.PushToken!),
                    title,
                    message,
                    notification.Id,
                    href,
                    cancellationToken);
            }
        }

        return Ok(new PromotionResponse(notifications.Count));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out userId);
    }

    public sealed record UserNotificationResponse(
        Guid Id,
        string Category,
        string Title,
        string Message,
        string? Href,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ReadAt);

    public sealed record PromotionRequest(string? Title, string? Message, string? Href);
    public sealed record PromotionResponse(int Recipients);
}
