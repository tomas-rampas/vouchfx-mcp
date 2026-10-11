using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

/// <summary>Distinct engine event identifiers observed in an events file, never registry identifiers.</summary>
/// <param name="RunIds">Exact, bounded identifiers in first-appearance order; not a per-suite attribution.</param>
/// <param name="Partial">Some identifiers could not be retained, or the events-file read was bounded.</param>
public sealed record EngineCorrelation(IReadOnlyList<string> RunIds, bool Partial)
{
    /// <summary>Maximum distinct identifiers retained from one stream.</summary>
    public const int MaxRunIds = 16;

    /// <summary>Maximum printable ASCII characters in an identifier; longer identifiers are omitted, never shortened.</summary>
    public const int MaxRunIdChars = 64;

    internal EngineCorrelation WithIncompleteRead(bool incomplete) => incomplete && !Partial ? this with { Partial = true } : this;
}

// A wrong-shaped optional runId must not make an otherwise usable event disappear. Decode only a
// bounded scalar: JsonElement.GetString would allocate the entire hostile value before a cap, while
// an ordinary string DTO would make non-string values fail the whole event's deserialization.
internal readonly record struct EngineRunIdValue(string? Value);

internal sealed class EngineRunIdValueConverter : JsonConverter<EngineRunIdValue>
{
    public override EngineRunIdValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var encodedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (reader.TokenType != JsonTokenType.String || encodedLength > EngineCorrelation.MaxRunIdChars * 6)
        {
            reader.Skip();
            return default;
        }

        string? value;
        try
        {
            value = reader.GetString();
        }
        catch (InvalidOperationException)
        {
            return default;
        }

        // Do not sanitise or truncate an identity: two different IDs must never silently collapse.
        // Printable ASCII needs no display sanitation. Unsupported values instead mark correlation
        // partial; the engine's current 32-hex identifiers fit without a format-specific dependency.
        if (string.IsNullOrWhiteSpace(value) || value.Length > EngineCorrelation.MaxRunIdChars
            || value.Any(c => c is < ' ' or > '~'))
        {
            return default;
        }

        return new EngineRunIdValue(value);
    }

    public override void Write(Utf8JsonWriter writer, EngineRunIdValue value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
