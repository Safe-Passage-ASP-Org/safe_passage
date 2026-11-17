using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed record MfaRequestContext(
    string? IpAddress = null,
    string? OperatingSystem = null,
    string? Location = null);

public interface INotificationService
{
    Task SendMfaApprovalRequestAsync(User user, Device device, string sessionToken, CancellationToken cancellationToken);

    Task SendMfaApprovalRequestAsync(
        User user,
        Device device,
        string sessionToken,
        MfaRequestContext? context,
        CancellationToken cancellationToken) =>
        SendMfaApprovalRequestAsync(user, device, sessionToken, cancellationToken);
}
