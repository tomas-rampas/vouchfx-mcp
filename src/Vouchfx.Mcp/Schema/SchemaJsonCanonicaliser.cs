using System.Text.Json;

namespace Vouchfx.Mcp.Schema;

/// <summary>
/// Reduces a JSON Schema document to a formatting-independent canonical string, so two copies of
/// the same schema that differ only in whitespace or line endings compare EQUAL (US-S2-01's live
/// cross-verification).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a byte comparison would be wrong here, measured rather than assumed.</b>
/// <c>vendored/README.md</c> and <c>CLAUDE.md</c> both record — as a standing instruction, because
/// it has already bitten — that regenerating <c>vendored/composed-schema.v1.json</c> from
/// <c>vouchfx schema</c> fails the SHA-256 drift gate over "CRLF/trailing-newline differences"
/// alone. The two artefacts are the SAME DOCUMENT and differ in bytes by construction. A byte-level
/// (or even trim-level) comparison in <c>get_schema</c>'s cross-verification would therefore report
/// a mismatch on every machine that has the correct pinned CLI installed: the loudest possible
/// false positive, on the one path a user would most reasonably trust.
/// </para>
/// <para>
/// <b>What is deliberately NOT normalised: property ORDER.</b> Two schemas whose objects list the
/// same members in a different order are NOT treated as equal here, and that is the right call for
/// this comparison rather than an omission. JSON object member order is insignificant to a JSON
/// Schema evaluator, but it is fully significant to what this server SERVES: the vendored document
/// is byte-pinned to an engine commit, and an engine that started emitting its members in a
/// different order has changed its generator — a fact a host cross-verifying against the pin
/// deserves to be told about, not one to silently normalise away. Sorting would also cost an
/// allocation-heavy recursive rewrite of a 150&#160;KB document on a path that runs per call.
/// </para>
/// </remarks>
public static class SchemaJsonCanonicaliser
{
    /// <summary>
    /// Parses <paramref name="json"/> and re-emits it with no insignificant whitespace at all.
    /// </summary>
    /// <exception cref="JsonException"><paramref name="json"/> is not well-formed JSON.</exception>
    public static string Canonicalise(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        return Canonicalise(document.RootElement);
    }

    /// <summary>Re-emits <paramref name="element"/> with no insignificant whitespace.</summary>
    public static string Canonicalise(JsonElement element) =>
        // WriteIndented defaults to false, so this collapses every newline and every run of
        // indentation the source happened to carry while preserving values exactly (including the
        // escaping of any character inside a string, which the writer reproduces identically for
        // both sides of a comparison).
        JsonSerializer.Serialize(element);
}
