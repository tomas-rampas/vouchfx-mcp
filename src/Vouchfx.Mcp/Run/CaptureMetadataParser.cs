using System.Text.Json;

namespace Vouchfx.Mcp.Run;

// Private diagnostic evidence from ONE exact step-completed event, never a run_suite wire member.
// Name/path are display metadata only. Captured values and unrelated provenance are never read.
internal sealed record CaptureDeclaration(string Name, string Path, bool TargetSafe);

internal static class CaptureMetadataParser
{
    internal const int MaxEventsWithMetadata = 256;
    internal const int MaxEventChars = 64 * 1024;
    internal const int MaxEntries = 32;
    internal const int MaxNameChars = 120;
    internal const int MaxPathChars = 240;
    private const int MaxRawPathChars = 2000;
    private const int MaxRootProperties = 64;
    private const int MaxEntryProperties = 16;

    internal static CaptureDeclaration? Parse(string line, StepOutcome step)
    {
        // Bound the extra parse/work independently of the events reader's whole-file bound.
        if (line.Length > MaxEventChars)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement? captured = null;
            JsonElement? observation = null;
            JsonElement? stepId = null;
            var examined = 0;
            // RunEvent's Web serializer binds root names case-insensitively, after decoding JSON
            // escapes. Refuse duplicate authority before selecting any optional capture evidence,
            // including type/verdict/identity fields we do not otherwise extract here.
            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var propertyName = property.Name;
                if (++examined > MaxRootProperties || !propertyNames.Add(propertyName))
                {
                    return null;
                }

                if (property.NameEquals("captured"))
                {
                    if (captured is not null) { return null; }
                    captured = property.Value;
                }
                else if (string.Equals(propertyName, "observation", StringComparison.OrdinalIgnoreCase))
                {
                    if (observation is not null) { return null; }
                    observation = property.Value;
                }
                else if (string.Equals(propertyName, "stepId", StringComparison.OrdinalIgnoreCase))
                {
                    if (stepId is not null) { return null; }
                    stepId = property.Value;
                }
            }

            var rawStepId = StringValue(stepId, MaxEventChars);
            if (rawStepId is null || captured is not { ValueKind: JsonValueKind.Array } array
                || array.GetArrayLength() > MaxEntries
                || observation is not { ValueKind: JsonValueKind.Object } observed)
            {
                return null;
            }

            JsonElement? unmet = null;
            examined = 0;
            propertyNames.Clear();
            foreach (var property in observed.EnumerateObject())
            {
                if (++examined > MaxEntryProperties || !propertyNames.Add(property.Name))
                {
                    return null;
                }

                if (property.NameEquals("captureUnmet"))
                {
                    if (unmet is not null) { return null; }
                    unmet = property.Value;
                }
            }

            var rawUnmetName = StringValue(unmet, MaxNameChars);
            if (rawUnmetName is null)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var displayNames = new HashSet<string>(StringComparer.Ordinal);
            CaptureDeclaration? declaration = null;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                JsonElement? name = null;
                JsonElement? path = null;
                JsonElement? matched = null;
                examined = 0;
                propertyNames.Clear();
                foreach (var property in item.EnumerateObject())
                {
                    if (++examined > MaxEntryProperties || !propertyNames.Add(property.Name))
                    {
                        return null;
                    }

                    if (property.NameEquals("name"))
                    {
                        if (name is not null) { return null; }
                        name = property.Value;
                    }
                    else if (property.NameEquals("path"))
                    {
                        if (path is not null) { return null; }
                        path = property.Value;
                    }
                    else if (property.NameEquals("matched"))
                    {
                        if (matched is not null) { return null; }
                        matched = property.Value;
                    }
                }

                var rawName = StringValue(name, MaxNameChars);
                var rawPath = StringValue(path, MaxRawPathChars);
                if (rawName is null || rawPath is null
                    || matched is not { ValueKind: JsonValueKind.True or JsonValueKind.False } matchedValue)
                {
                    return null;
                }

                var displayName = SanitiseAndCap(rawName, MaxNameChars);
                if (!names.Add(rawName) || !displayNames.Add(displayName))
                {
                    // Reject the entire list: duplicate/mixed names or display collisions do not
                    // prove a unique declaration, including ambiguity after sanitisation/capping.
                    return null;
                }

                if (string.Equals(rawName, rawUnmetName, StringComparison.Ordinal)
                    && matchedValue.ValueKind == JsonValueKind.False)
                {
                    declaration = new(displayName, SanitiseAndCap(rawPath, MaxPathChars),
                        string.Equals(rawStepId, step.StepId, StringComparison.Ordinal)
                        && rawStepId.Length <= MaxNameChars
                        && string.Equals(rawName, displayName, StringComparison.Ordinal));
                }
            }

            return declaration;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Malformed optional evidence (including undecodable surrogate escapes) never drops
            // the StepOutcome or the existing observation-only classification.
            return null;
        }
    }

    private static string? StringValue(JsonElement? element, int maxChars)
    {
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return null;
        }

        var text = value.GetString();
        return !string.IsNullOrWhiteSpace(text) && text.Length <= maxChars ? text : null;
    }

    private static string SanitiseAndCap(string text, int limit) =>
        Diagnosis.VerdictReasonClassifier.Cap(TextSanitiser.SanitiseForDisplay(text), limit);
}
