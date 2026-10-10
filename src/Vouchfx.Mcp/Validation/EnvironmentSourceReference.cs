using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>Recognises an external source reference without validating its driver or opening files.</summary>
internal static class EnvironmentSourceReference
{
    public static bool IsDeclared(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("environment", out var environment)
        && environment.ValueKind == JsonValueKind.Object
        && environment.TryGetProperty("source", out var source)
        && source.ValueKind == JsonValueKind.Object
        && HasNonEmptyString(source, "driver")
        && HasNonEmptyString(source, "config");

    private static bool HasNonEmptyString(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());
}
