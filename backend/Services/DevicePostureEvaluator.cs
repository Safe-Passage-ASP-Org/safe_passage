using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public sealed record DevicePostureEvaluationResult(bool IsCompliant, string? FailureReason = null);

public static class DevicePostureEvaluator
{
    public static DevicePostureEvaluationResult Evaluate(Device device, SecurityPolicyEvaluation policy)
    {
        if (!policy.IsValid)
            return new(false, "The enforced device security policy is invalid.");

        if (policy.RequiresScreenLock)
        {
            if (device.ScreenLockEnabled == false)
                return new(false, "Device screen lock is disabled and required by policy.");

            if (device.ScreenLockEnabled is null)
                return new(false, "Device screen lock status is unknown or unavailable and required by policy.");
        }

        if (policy.BlocksRootedDevices || policy.BlocksJailbrokenDevices)
        {
            if (device.IsRootedOrJailbroken == true)
                return new(false, "Rooted or jailbroken devices are blocked by security policy.");

            if (device.IsRootedOrJailbroken is null)
                return new(false, "Device integrity status is unknown or unavailable and required by policy.");
        }

        return new(true);
    }
}
