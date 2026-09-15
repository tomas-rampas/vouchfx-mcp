using System.Globalization;
using System.Text;
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
/// allocation-heavy recursive rewrite of a 167&#160;854-character document (measured) on a path that
/// runs per call.
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

    /// <summary>
    /// <see cref="Canonicalise(string)"/> preceded by <see cref="EscapeRawControlCharacters"/> — the
    /// form <c>get_schema</c>'s cross-verification uses on BOTH sides of every comparison (issue
    /// #89).
    /// </summary>
    /// <remarks>
    /// Kept separate from the strict <see cref="Canonicalise(string)"/> rather than replacing it:
    /// every other caller in this repo (and every test that pins the vendored document's canonical
    /// form) is comparing text this server produced or owns, where a raw control character would be
    /// a genuine defect that should still throw.
    /// </remarks>
    /// <exception cref="JsonException">
    /// <paramref name="json"/> is not well-formed JSON even after the pre-pass.
    /// </exception>
    public static string CanonicaliseTolerant(string json) =>
        Canonicalise(EscapeRawControlCharacters(json));

    /// <summary>
    /// Replaces every RAW C0 control character other than <c>\t</c>, <c>\n</c> and <c>\r</c>
    /// (U+0000–U+0008, U+000B, U+000C, U+000E–U+001F) with its <c>\u00XX</c> JSON escape, leaving
    /// everything else — including text that is ALREADY escaped that way — untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this cannot launder a genuinely broken document into a valid one.</b> Those characters
    /// are illegal RAW anywhere in JSON: inside a string the <c>\u00XX</c> escape is the only legal
    /// spelling, and outside a string they are not whitespace, so a document carrying one fails to
    /// parse either way. Escaping them therefore only ever changes the outcome in the one case this
    /// exists for — a string value whose character the console code page could not represent and
    /// which the engine best-fit-mapped to a control byte before this server ever saw it (MEASURED
    /// 2026-09-15 under cp852: the schema's <c>…</c> U+2026 is emitted as a raw <c>0x07</c>, which
    /// makes <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> throw on the live text).
    /// Outside a string the escape produces a bare <c>\</c>, which is still invalid — the document
    /// stays broken, exactly as it should.
    /// </para>
    /// <para>
    /// <b>The backslash-parity check is what makes that claim airtight.</b> A control character
    /// sitting in an ESCAPE position (an odd number of backslashes immediately before it, e.g.
    /// <c>"\␇"</c>) is left raw: escaping it would turn <c>\</c>+<c>0x07</c> into <c>\\u0007</c>,
    /// which parses as a literal backslash followed by the text <c>u0007</c> — the one shape in
    /// which this pre-pass could otherwise make an invalid document valid with different content.
    /// An odd backslash run outside a string is already invalid JSON regardless, so the rule needs
    /// no string-state tracking to be sound.
    /// </para>
    /// <para>
    /// <b>The SECOND residual, which the tab/LF/CR exemption creates and cannot avoid.</b> Those
    /// three are exempt because they are legal raw BETWEEN tokens — JSON whitespace — so escaping
    /// them would emit a backslash-<c>u000a</c> escape OUTSIDE a string and turn valid JSON invalid,
    /// which is strictly worse than the case this repairs. The consequence is that a best-fit mapping
    /// landing on 0x09/0x0A/0x0D INSIDE a string is UNREPAIRABLE: the export stays unparseable and
    /// <c>get_schema</c> reports a FALSE-POSITIVE VFX-D-1106. MEASURED 2026-09-15, the OEM family
    /// does map characters there — under cp437, cp850 and cp852 alike, <c>○</c> U+25CB → <c>0x09</c>,
    /// <c>◙</c> U+25D9 → <c>0x0A</c> and <c>♪</c> U+266A → <c>0x0D</c>. It is unreachable at the
    /// current pin: the composed schema's only non-ASCII characters are <c>—</c>, <c>§</c> and
    /// <c>…</c> (and their cp437 mappings, <c>0x2D</c>/<c>0x15</c>/<c>0x2E</c>, include a control the
    /// pre-pass DOES repair). It is also the safe direction — a spurious warning on a still-correct
    /// answer, never a suppressed real divergence — so it is recorded here rather than worked around,
    /// and it is the thing to check first if a future schema gains a character outside that set.
    /// </para>
    /// <para>
    /// Allocation-free on the common path: the builder is created only once a replacement is
    /// actually needed, so the 167&#160;854-character vendored document and every clean live export are
    /// returned as the same <see langword="string"/> instance they arrived as.
    /// </para>
    /// </remarks>
    public static string EscapeRawControlCharacters(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        StringBuilder? builder = null;
        var backslashRun = 0;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (c < ' ' && c != '\t' && c != '\n' && c != '\r' && backslashRun % 2 == 0)
            {
                builder ??= new StringBuilder(json.Length + 16).Append(json, 0, i);
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                backslashRun = 0;
                continue;
            }

            backslashRun = c == '\\' ? backslashRun + 1 : 0;
            builder?.Append(c);
        }

        return builder?.ToString() ?? json;
    }
}
