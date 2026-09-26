namespace SafePassage.Api.Models;

public sealed class SecurityPolicy
{
    public Guid Id { get; set; }
    public string PolicyName { get; set; } = string.Empty;
    public string PolicyType { get; set; } = string.Empty;
    public bool IsEnforced { get; set; }
    public string RulesJson { get; set; } = "{}";
}