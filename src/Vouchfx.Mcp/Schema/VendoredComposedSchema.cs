using System.Text;
using System.Text.Json;

namespace Vouchfx.Mcp.Schema;

/// <summary>
/// Reads the embedded, drift-gated <c>composed-schema.v1.json</c> once and hands it out as raw JSON
/// text — <c>get_schema</c>'s offline source of truth (US-S2-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists next to <see cref="Vouchfx.Mcp.Validation.VendoredSchemaVersion"/> and
/// <see cref="Vouchfx.Mcp.Validation.StepTypeCatalogue"/> rather than being folded into either.</b>
/// Those two answer ONE narrow question each from the schema (its version marker; its step-type
/// vocabulary) and each already carries its own copy of the resource name — the established
/// precedent in this codebase. This type answers a third: "give me the document itself". Folding a
/// general document accessor into either of them would make a purpose-built type answer a question
/// it was not built for; the resource name is repeated here for the same reason it is repeated
/// there, and <c>VendoredArtefactsTests</c> pins the name against the csproj's own
/// <c>LogicalName</c> so a rename cannot silently break one copy and not the others.
/// </para>
/// <para>
/// <b>Raw text, re-parsed per call, deliberately.</b> The obvious alternative — a static
/// <see cref="JsonDocument"/> whose <c>RootElement</c> every caller reads — would
/// share one <see cref="JsonDocument"/> across every concurrent tool call, and
/// .NET does not document that type's instance members as safe for concurrent use. That is the
/// EXACT hazard <c>Tools/StructuredToolResult</c>'s own remarks record (and solve there by caching
/// immutable bytes instead of a <see cref="JsonElement"/>). A
/// <see langword="string"/> is immutable and therefore free of the question entirely; the ~150&#160;KB
/// parse each <c>get_schema</c> call pays is sub-millisecond and this is a low-frequency authoring
/// tool, not a hot path.
/// </para>
/// </remarks>
public static class VendoredComposedSchema
{
    /// <summary>
    /// The manifest resource name the csproj pins via <c>LogicalName</c>. Same literal as
    /// <see cref="Vouchfx.Mcp.Validation.VendoredSchemaVersion"/>'s and
    /// <see cref="Vouchfx.Mcp.Validation.StepTypeCatalogue"/>'s — see this type's remarks.
    /// </summary>
    private const string SchemaResourceName = "Vouchfx.Mcp.Vendored.composed-schema.v1.json";

    /// <summary>
    /// The embedded composed schema's raw JSON text, byte-exact from the engine repo at the pinned
    /// commit (see <c>vendored/README.md</c>) apart from UTF-8 decoding.
    /// </summary>
    /// <exception cref="InvalidOperationException">The embedded schema resource is missing.</exception>
    public static string RawJson { get; } = Read();

    /// <summary>
    /// Parses <see cref="RawJson"/> into a fresh document the caller owns and must dispose. One
    /// document per call — never a shared static — see this type's remarks.
    /// </summary>
    public static JsonDocument Parse() => JsonDocument.Parse(RawJson);

    private static string Read()
    {
        var assembly = typeof(VendoredComposedSchema).Assembly;

        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{SchemaResourceName}' was not found in '{assembly.FullName}'.");

        // detectEncodingFromByteOrderMarks: the committed vendored file carries no BOM today
        // (measured), but a future engine-side regeneration that added one would otherwise leave a
        // U+FEFF at the head of this string and make every JsonDocument.Parse of it throw.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
