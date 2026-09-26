namespace SafePassage.Api.Models;

public sealed class Group
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public ICollection<UserGroup> UserGroups { get; set; } = new List<UserGroup>();
    public ICollection<ApplicationGroupAccess> ApplicationAccess { get; set; } = new List<ApplicationGroupAccess>();
}