using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;

namespace SafePassage.Api.Services;

public sealed record SecurityPolicyEvaluation(
    bool RequiresTrustedDevice,
    bool AllowsTrustedDeviceMfaBypass,
    bool IsValid);

public interface ISecurityPolicyEvaluator
{
    Task<SecurityPolicyEvaluation> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed class SecurityPolicyEvaluator(ApplicationDbContext dbContext) : ISecurityPolicyEvaluator
{
    public async Task<SecurityPolicyEvaluation> EvaluateAsync(CancellationToken cancellationToken)
    {
        var policies = await dbContext.SecurityPolicies.AsNoTracking()
            .Where(policy => policy.IsEnforced)
            .Select(policy => new { policy.PolicyType, policy.RulesJson })
            .ToListAsync(cancellationToken);

        var requiresTrustedDevice = false;
        var allowsMfaBypass = false;
        var disallowsMfaBypass = false;
        var requiresStepUp = false;

        foreach (var policy in policies)
        {
            var policyType = policy.PolicyType.Replace(" ", "", StringComparison.Ordinal)
                .Replace("_", "", StringComparison.Ordinal);
            if (!policyType.Equals("Device", StringComparison.OrdinalIgnoreCase)
                && !policyType.Equals("DevicePolicy", StringComparison.OrdinalIgnoreCase)
                && !policyType.Equals("Authentication", StringComparison.OrdinalIgnoreCase)
                && !policyType.Equals("AuthenticationPolicy", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var document = JsonDocument.Parse(policy.RulesJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !TryReadBoolean(document.RootElement, "requiresTrustedDevice", out var requiresDevice)
                    || !TryReadBoolean(document.RootElement, "allowTrustedDeviceMfaBypass", out var allowBypass)
                    || !TryReadBoolean(document.RootElement, "requiresStepUp", out var requiresStepUpRule)
                    || !TryReadBoolean(document.RootElement, "requireMfa", out var requireMfa))
                    return new(requiresTrustedDevice, false, false);

                requiresTrustedDevice |= requiresDevice == true;
                allowsMfaBypass |= allowBypass == true;
                disallowsMfaBypass |= allowBypass == false;
                requiresStepUp |= requiresStepUpRule == true || requireMfa == true;
            }
            catch (JsonException)
            {
                return new(requiresTrustedDevice, false, false);
            }
        }

        return new(
            requiresTrustedDevice,
            allowsMfaBypass && !disallowsMfaBypass && !requiresStepUp,
            true);
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName, out bool? value)
    {
        value = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            return false;
        }

        return true;
    }
}