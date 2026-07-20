using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Derives the vouchfx step-type catalogue from the embedded <c>composed-schema.v1.json</c>
/// resource at run time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why derive it instead of hand-maintaining a list:</b> the vendored schema is drift-gated
/// against the pinned engine commit (see <c>vendored/README.md</c>) — a second, hand-written
/// catalogue would silently rot the moment the pin advances and a step type is added, removed,
/// or has its fields changed. Deriving from the schema itself means this catalogue can never
/// disagree with what the embedded schema actually says.
/// </para>
/// <para>
/// <b>Schema-structure notes (STEP 0):</b> the composed schema does not list step types in a
/// flat map keyed by type name. <c>$defs.step.allOf</c> is a sequence of 25 unconditional
/// <c>if</c>/<c>then</c> clauses — one per registered <c>family.provider</c> — where
/// <c>if.properties.type.const</c> names the type and <c>then.required</c> /
/// <c>then.properties</c> carry that type's own fields. Only some of the 25 <c>then</c> blocks
/// carry a type-level <c>description</c> keyword; the rest have only per-field descriptions.
/// Exactly one type, <c>script.csharp</c>, has no flat <c>required</c> array at all — its
/// <c>then</c> block uses <c>oneOf</c> of single-field <c>required</c> groups (<c>code</c> XOR
/// <c>file</c>) instead, which <see cref="StepTypeInfo.RequiredOneOf"/> exists to capture.
/// </para>
/// </remarks>
public static class StepTypeCatalogue
{
    private const string SchemaResourceName = "Vouchfx.Mcp.Vendored.composed-schema.v1.json";

    /// <summary>Every step type the embedded schema defines, sorted by dotted type name.</summary>
    public static IReadOnlyList<StepTypeInfo> All { get; } = Load();

    /// <summary>Finds a step type by its exact dotted <c>family.provider</c> name.</summary>
    /// <returns>The matching <see cref="StepTypeInfo"/>, or <see langword="null"/> if unknown.</returns>
    public static StepTypeInfo? Find(string type) =>
        All.FirstOrDefault(candidate => string.Equals(candidate.Type, type, StringComparison.Ordinal));

    private static List<StepTypeInfo> Load()
    {
        using var stream = OpenSchemaResource();
        using var document = JsonDocument.Parse(stream);

        var allOf = document.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("allOf");

        var results = new List<StepTypeInfo>();
        foreach (var clause in allOf.EnumerateArray())
        {
            var typeConst = clause.GetProperty("if").GetProperty("properties").GetProperty("type").GetProperty("const").GetString()
                ?? throw new InvalidOperationException(
                    "A step allOf clause's if.properties.type.const was not a JSON string.");

            var (family, provider) = SplitType(typeConst);
            var then = clause.GetProperty("then");

            var description = ReadOptionalString(then, "description");
            var requiredNames = ReadFlatRequired(then);
            var fields = ReadFields(then, requiredNames);
            var requiredOneOf = ReadRequiredOneOf(then);

            results.Add(new StepTypeInfo(typeConst, family, provider, description, fields, requiredOneOf));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.Type, b.Type));
        return results;
    }

    private static (string Family, string Provider) SplitType(string type)
    {
        var dotIndex = type.IndexOf('.', StringComparison.Ordinal);
        return dotIndex < 0
            ? (type, string.Empty)
            : (type[..dotIndex], type[(dotIndex + 1)..]);
    }

    private static List<StepFieldInfo> ReadFields(JsonElement then, HashSet<string> requiredNames)
    {
        if (!then.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var fields = new List<StepFieldInfo>();
        foreach (var property in properties.EnumerateObject())
        {
            var fieldSchema = property.Value;
            var description = ReadOptionalString(fieldSchema, "description");
            var type = ReadOptionalString(fieldSchema, "type");
            var required = requiredNames.Contains(property.Name);

            fields.Add(new StepFieldInfo(property.Name, type, description, required));
        }

        fields.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    private static HashSet<string> ReadFlatRequired(JsonElement then)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (then.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    names.Add(item.GetString()!);
                }
            }
        }

        return names;
    }

    private static List<IReadOnlyList<string>>? ReadRequiredOneOf(JsonElement then)
    {
        if (!then.TryGetProperty("oneOf", out var oneOf) || oneOf.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var groups = new List<IReadOnlyList<string>>();
        foreach (var branch in oneOf.EnumerateArray())
        {
            if (!branch.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var names = new List<string>();
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    names.Add(item.GetString()!);
                }
            }

            if (names.Count > 0)
            {
                groups.Add(names);
            }
        }

        return groups.Count == 0 ? null : groups;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Stream OpenSchemaResource()
    {
        var assembly = typeof(StepTypeCatalogue).Assembly;

        return assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{SchemaResourceName}' was not found in '{assembly.FullName}'.");
    }
}
