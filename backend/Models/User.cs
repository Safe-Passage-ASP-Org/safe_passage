namespace SafePassage.Api.Models;

public sealed class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string Role { get; set; } = "User";

    public ICollection<UserGroup> UserGroups { get; set; } = new List<UserGroup>();
    public ICollection<Device> Devices { get; set; } = new List<Device>();
    public ICollection<ApplicationUserAccess> ApplicationAccess { get; set; } = new List<ApplicationUserAccess>();
    public ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();
    public ICollection<MfaSession> MfaSessions { get; set; } = new List<MfaSession>();
}