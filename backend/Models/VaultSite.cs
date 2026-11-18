namespace SafePassage.Api.Models;

/// <summary>A user's site credentials, synchronized between the web portal and browser extension.</summary>
public sealed class VaultSite
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string LoginUrl { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public string Description { get; set; } = string.Empty;
    public string AutoFillUsername { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public string EncryptedTotpSecret { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedByUserId { get; set; }
}
