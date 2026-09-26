namespace SafePassage.Api.Models;

public sealed class Application
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public ICollection<ApplicationGroupAccess> GroupAccess { get; set; } = new List<ApplicationGroupAccess>();
    public ICollection<ApplicationUserAccess> UserAccess { get; set; } = new List<ApplicationUserAccess>();
}