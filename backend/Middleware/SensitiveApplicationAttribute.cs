namespace SafePassage.Api.Middleware;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class SensitiveApplicationAttribute : Attribute;