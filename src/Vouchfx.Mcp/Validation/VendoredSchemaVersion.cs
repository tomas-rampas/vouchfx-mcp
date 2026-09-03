using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The vouchfx language schema version, read from the embedded <c>composed-schema.v1.json</c>'s own
/// in-document version marker (US-S1-02). Surfaced to hosts as <c>meta.schemaVersion</c> on every
/// successful tool result (see <see cref="Vouchfx.Mcp.Contracts.ToolMeta"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why read the marker instead of hardcoding "v1" or parsing the file name:</b> the same reason
/// <see cref="StepTypeCatalogue"/> derives its vocabulary from the schema rather than from a
/// hand-written list. The vendored schema is byte-exact from the engine repo at the pinned commit
/// and drift-gated in CI (see <c>vendored/README.md</c>); a second, hand-maintained copy of its
/// version would silently disagree with the schema actually being validated against the first time
/// the pin advanced past a language-version bump. Reading the schema's own marker means this value
/// cannot disagree with the schema it describes.
/// </para>
/// <para>
/// <b>Where the marker lives:</b> the top-level <c>x-vouchfx-schema-version</c> keyword — the
/// schema's purpose-built self-declaration, which exists for exactly this question and nothing
/// else.
/// </para>
/// <para>
/// <b>Why NOT <c>$defs.metadata.properties.schemaVersion.const</c></b> (a review fix — the first
/// version of this type read that, on the false claim it was the only self-declaration): that
/// <c>const</c> is a VALIDATION RULE about what an author may write in a suite's optional
/// <c>metadata.schemaVersion</c> field, not a statement about the schema document. The two happen
/// to carry the same string today, and that coincidence is precisely the trap. During any
/// v1&#8594;v2 transition the natural edit to that field is to widen it — <c>const: "v1"</c>
/// becomes <c>enum: ["v1", "v2"]</c> so both declarations validate — at which point the
/// <c>const</c> disappears entirely and, since this value is stamped onto EVERY tool result, a
/// read from there would take the whole server down on its first call. The top-level keyword has
/// no such failure mode: it is a single scalar whose only job is to name the document's version.
/// </para>
/// <para>
/// Read once, eagerly, into a static — a malformed or restructured embedded schema is a build-time
/// packaging fault, not a per-call condition, so it surfaces as a hard failure the first time
/// anything touches this type rather than as a silently-degraded field on every result.
/// </para>
/// </remarks>
public static class VendoredSchemaVersion
{
    private const string SchemaResourceName = "Vouchfx.Mcp.Vendored.composed-schema.v1.json";

    /// <summary>
    /// The top-level keyword the composed schema declares its own version under. Kept as a named
    /// constant because <c>VendoredArtefactsTests</c> pins the same string against the
    /// repo-checked-in schema file.
    /// </summary>
    internal const string MarkerKeyword = "x-vouchfx-schema-version";

    /// <summary>
    /// The language schema version the embedded composed schema declares (e.g. <c>"v1"</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The embedded schema resource is missing, or no longer carries its top-level
    /// <c>x-vouchfx-schema-version</c> keyword as a non-empty JSON string.
    /// </exception>
    public static string Value { get; } = Read();

    private static string Read()
    {
        using var stream = OpenSchemaResource();
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.TryGetProperty(MarkerKeyword, out var marker)
            && marker.ValueKind == JsonValueKind.String
            && marker.GetString() is { Length: > 0 } version)
        {
            return version;
        }

        throw new InvalidOperationException(
            $"Embedded resource '{SchemaResourceName}' no longer declares its language schema "
            + $"version at the top-level '{MarkerKeyword}' keyword as a non-empty JSON string. The "
            + "vendored schema's structure changed with an engine pin bump; update "
            + $"{nameof(VendoredSchemaVersion)} to read the marker's new location.");
    }

    private static Stream OpenSchemaResource()
    {
        var assembly = typeof(VendoredSchemaVersion).Assembly;

        return assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{SchemaResourceName}' was not found in '{assembly.FullName}'.");
    }
}
