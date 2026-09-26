namespace SafePassage.Api.Models;

public sealed class UserGroup
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = null!;
}

public sealed class ApplicationUserAccess
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}

public sealed class ApplicationGroupAccess
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = null!;
}