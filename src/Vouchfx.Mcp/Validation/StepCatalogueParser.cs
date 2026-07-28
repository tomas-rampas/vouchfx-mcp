using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Parses the engine's shape-level <c>vouchfx list --json</c> document (Spec A wire shape) into
/// <see cref="StepTypeInfo"/> entries. Fail-closed: missing bar-B fields abort the whole parse
/// rather than producing a partial catalogue of type keys only (EDGE-004).
/// </summary>
/// <remarks>
/// Expected camelCase document (frozen at catalogue schemaVersion 1):
/// <code>
/// {
///   "schemaVersion": 1,
///   "engineVersion": "…",
///   "stepTypes": [
///     {
///       "type": "http.rest",
///       "family": "http",
///       "provider": "rest",
///       "requiredFields": ["method", "path", "target"],
///       "optionalFields": ["body", "expect", "headers"],
///       "captureSupported": true,
///       "familyIntent": "Call HTTP endpoints …"
///     }
///   ]
/// }
/// </code>
/// </remarks>
public static class StepCatalogueParser
{
    /// <summary>
    /// Parses <paramref name="json"/> as a complete shape-level catalogue.
    /// </summary>
    /// <exception cref="StepCatalogueParseException">
    /// When the document is not valid JSON, has no <c>stepTypes</c>, is empty, or any entry lacks
    /// bar-B fields (<c>requiredFields</c>, <c>optionalFields</c>, <c>captureSupported</c>,
    /// <c>familyIntent</c>).
    /// </exception>
    public static IReadOnlyList<StepTypeInfo> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new StepCatalogueParseException(
                "Catalogue JSON is not valid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new StepCatalogueParseException(
                    "Catalogue JSON root must be a JSON object.");
            }

            if (!root.TryGetProperty("stepTypes", out var stepTypes) ||
                stepTypes.ValueKind != JsonValueKind.Array)
            {
                throw new StepCatalogueParseException(
                    "Catalogue JSON is missing a 'stepTypes' array (not a shape-level "
                    + "list --json document).");
            }

            var results = new List<StepTypeInfo>();
            var index = 0;
            foreach (var entry in stepTypes.EnumerateArray())
            {
                results.Add(ParseEntry(entry, index));
                index++;
            }

            if (results.Count == 0)
            {
                throw new StepCatalogueParseException(
                    "Catalogue JSON 'stepTypes' array is empty.");
            }

            results.Sort(static (a, b) => string.CompareOrdinal(a.Type, b.Type));
            return results;
        }
    }

    private static StepTypeInfo ParseEntry(JsonElement entry, int index)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new StepCatalogueParseException(
                $"Catalogue stepTypes[{index}] must be a JSON object.");
        }

        var type = RequireNonEmptyString(entry, "type", index);
        var family = RequireNonEmptyString(entry, "family", index);
        var provider = RequireNonEmptyString(entry, "provider", index);

        // EDGE-004: bar-B fields are mandatory. A thin pre-Spec-A list --json (type/family/provider
        // only) fails here rather than silently shipping type keys without field metadata.
        if (!entry.TryGetProperty("requiredFields", out var requiredNode) ||
            requiredNode.ValueKind != JsonValueKind.Array)
        {
            throw new StepCatalogueParseException(
                $"Catalogue entry '{type}' is missing requiredFields (thin catalogue without "
                + "shape-level field metadata).");
        }

        if (!entry.TryGetProperty("optionalFields", out var optionalNode) ||
            optionalNode.ValueKind != JsonValueKind.Array)
        {
            throw new StepCatalogueParseException(
                $"Catalogue entry '{type}' is missing optionalFields (thin catalogue without "
                + "shape-level field metadata).");
        }

        if (!entry.TryGetProperty("captureSupported", out var captureNode) ||
            (captureNode.ValueKind != JsonValueKind.True && captureNode.ValueKind != JsonValueKind.False))
        {
            throw new StepCatalogueParseException(
                $"Catalogue entry '{type}' is missing boolean captureSupported.");
        }

        if (!entry.TryGetProperty("familyIntent", out var intentNode) ||
            intentNode.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(intentNode.GetString()))
        {
            throw new StepCatalogueParseException(
                $"Catalogue entry '{type}' is missing a non-empty familyIntent.");
        }

        var requiredFields = ReadStringArray(requiredNode, type, "requiredFields");
        var optionalFields = ReadStringArray(optionalNode, type, "optionalFields");
        // Deterministic tool output: same ordinal order as the derived Fields list below,
        // independent of the order the engine emitted the arrays.
        requiredFields.Sort(StringComparer.Ordinal);
        optionalFields.Sort(StringComparer.Ordinal);

        var captureSupported = captureNode.GetBoolean();
        var familyIntent = intentNode.GetString()!;

        var fields = new List<StepFieldInfo>(requiredFields.Count + optionalFields.Count);
        foreach (var name in requiredFields)
        {
            fields.Add(new StepFieldInfo(name, Type: null, Description: null, Required: true));
        }

        foreach (var name in optionalFields)
        {
            fields.Add(new StepFieldInfo(name, Type: null, Description: null, Required: false));
        }

        fields.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new StepTypeInfo(
            Type: type,
            Family: family,
            Provider: provider,
            Description: familyIntent,
            Fields: fields,
            RequiredOneOf: null,
            RequiredFields: requiredFields,
            OptionalFields: optionalFields,
            CaptureSupported: captureSupported,
            FamilyIntent: familyIntent);
    }

    private static string RequireNonEmptyString(JsonElement entry, string propertyName, int index)
    {
        if (!entry.TryGetProperty(propertyName, out var node) ||
            node.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(node.GetString()))
        {
            throw new StepCatalogueParseException(
                $"Catalogue stepTypes[{index}] is missing a non-empty '{propertyName}' string.");
        }

        return node.GetString()!;
    }

    private static List<string> ReadStringArray(JsonElement array, string type, string propertyName)
    {
        var names = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                throw new StepCatalogueParseException(
                    $"Catalogue entry '{type}' has a non-string or empty value in {propertyName}.");
            }

            names.Add(item.GetString()!);
        }

        return names;
    }
}
