using System.Collections.Frozen;
using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The dependency KINDS the pinned engine recognises — <c>postgres</c>, <c>kafka</c>,
/// <c>mailpit</c>, … — derived from the embedded composed schema's own
/// <c>$defs.dependency.properties.type.enum</c> rather than hand-listed here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, for exactly the reason <see cref="StepTypeCatalogue"/> is derived</b> (see that
/// type's remarks): the vendored schema is drift-gated against the pinned engine commit, so a
/// hand-written copy of this vocabulary would silently rot the moment <c>ENGINE_PIN</c> advances and
/// a kind is added or renamed — and it would rot INVISIBLY, since a stale entry stops matching
/// rather than failing.
/// </para>
/// <para>
/// <b>The two members serve different callers, and confusing them is how the rot gets back in.</b>
/// <see cref="DeclaredIn"/> is what PRODUCTION reads —
/// <c>Validation/Semantics/UndeclaredDependencyRule</c> asks it what one document declares.
/// <see cref="All"/> has no production caller at all: it exists so
/// <c>UndeclaredDependencyRuleTests</c> can gate that rule's HAND-WRITTEN step-type → kind table
/// against the schema's own enum, which is the check that turns an <c>ENGINE_PIN</c> bump renaming a
/// kind into a named test failure instead of a rule that quietly never fires again.
/// </para>
/// <para>
/// <b>A KIND is not a NAME.</b> <c>environment.dependencies</c> is an object keyed by the author's
/// own logical name (<c>orders-db</c>), and each entry's <c>type</c> is the kind
/// (<c>postgres</c>). <see cref="SuiteFacts.Dependencies"/> carries the NAMES — which is what a
/// <c>target</c> resolves against — so a rule asking "does this suite declare a kafka dependency?"
/// cannot use the fact set and reads the document instead, via <see cref="DeclaredIn"/>.
/// </para>
/// </remarks>
internal static class DependencyKinds
{
    private const string SchemaResourceName = "Vouchfx.Mcp.Vendored.composed-schema.v1.json";

    /// <summary>Every dependency kind the embedded composed schema's enum accepts.</summary>
    public static FrozenSet<string> All { get; } = Load();

    /// <summary>
    /// The kinds <paramref name="root"/>'s <c>environment.dependencies</c> entries actually declare,
    /// read straight out of the already-parsed document.
    /// </summary>
    /// <remarks>
    /// Tolerant of every shape that is not the expected one — a scalar <c>environment</c>, a
    /// dependency whose <c>type</c> is a number — because those are the SCHEMA pass's findings to
    /// report, and a semantic rule that threw on one would degrade the whole call to
    /// <c>VFX-E-1901</c> (see <c>ISemanticRule</c>'s "a rule must not throw" contract).
    /// </remarks>
    public static HashSet<string> DeclaredIn(JsonElement root)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("environment", out var environment) ||
            environment.ValueKind != JsonValueKind.Object ||
            !environment.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object)
        {
            return declared;
        }

        foreach (var entry in dependencies.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object &&
                entry.Value.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() is { Length: > 0 } kind)
            {
                declared.Add(kind);
            }
        }

        return declared;
    }

    private static FrozenSet<string> Load()
    {
        var assembly = typeof(DependencyKinds).Assembly;
        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{SchemaResourceName}' was not found in '{assembly.FullName}'.");

        using var document = JsonDocument.Parse(stream);

        var kinds = document.RootElement
            .GetProperty("$defs")
            .GetProperty("dependency")
            .GetProperty("properties")
            .GetProperty("type")
            .GetProperty("enum")
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!);

        // Ordinal, matching the schema's own $comment: the engine's dependency registry looks kinds
        // up with StringComparer.Ordinal, so 'Postgres' is a rejection there and must be one here.
        return kinds.ToFrozenSet(StringComparer.Ordinal);
    }
}
