namespace SafePassage.Api.Models;

public sealed class Application
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LaunchUrl { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public string AutoFillUsername { get; set; } = string.Empty;
    public string EncryptedAutoFillPassword { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public string ClientType { get; set; } = "confidential";
    public string RedirectUris { get; set; } = string.Empty;
    public string PostLogoutRedirectUris { get; set; } = string.Empty;
    public string AllowedScopes { get; set; } = "openid profile email";
    public bool IsPublished { get; set; }
    public bool AllowAccessRequests { get; set; }

    public ICollection<ApplicationGroupAccess> GroupAccess { get; set; } = new List<ApplicationGroupAccess>();
    public ICollection<ApplicationUserAccess> UserAccess { get; set; } = new List<ApplicationUserAccess>();
    public ICollection<ApplicationAccessRequest> AccessRequests { get; set; } = new List<ApplicationAccessRequest>();
}
