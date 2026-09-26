using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public interface INotificationService
{
    Task SendMfaApprovalRequestAsync(User user, Device device, string sessionToken, CancellationToken cancellationToken);
}