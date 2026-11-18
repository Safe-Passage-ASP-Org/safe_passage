using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public interface IJwtTokenService
{
    string CreateAccessToken(User user, string? deviceId = null, string? authContext = null, DateTimeOffset? authContextExpiresAt = null);
}
