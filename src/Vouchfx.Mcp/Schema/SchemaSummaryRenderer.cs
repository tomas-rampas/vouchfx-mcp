using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Schema;

/// <summary>
/// Renders <c>get_schema</c>'s <c>format: "summary"</c> digest (US-S2-01): a compact markdown view
/// of one schema section, built ENTIRELY from that section's own <c>description</c> annotations and
/// bounded at <see cref="MaxSummaryBytes"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is authored.</b> Every sentence of prose in the output is a <c>description</c>
/// string copied verbatim out of the composed schema; the renderer contributes only structure
/// (headings, bullets, the field's declared <c>type</c>, and whether it is <c>required</c> — all
/// facts the schema states outright). A field with no <c>description</c> is therefore OMITTED, not
/// listed with filler like "no description available": an acceptance criterion of this story, and
/// the right default for a server that also promises it never hosts a model. The corollary is
/// visible in the tests: a subtree with no annotations at all renders as a heading and nothing
/// more, which is the honest answer.
/// </para>
/// <para>
/// <b>Verbatim means verbatim</b> — descriptions are not re-wrapped, whitespace-collapsed, or
/// truncated mid-string. The composed schema's descriptions are single-line JSON strings (measured
/// against the pinned vendored document), so bullets stay one line each; if a future engine build
/// introduced an embedded newline the bullet would wrap oddly rather than have its text altered,
/// which is the trade this type deliberately takes.
/// </para>
/// <para>
/// <b>Local <c>$ref</c> hops are followed, once.</b> The root document's <c>metadata</c> and
/// <c>environment</c> properties are bare <c>{"$ref": "#/$defs/…"}</c> stubs; not following them
/// would silently drop both fields from the <c>full</c> summary, which reads as "the schema has
/// nothing to say about metadata" — a fabrication by omission. One hop is enough for this schema
/// (measured: no <c>$defs</c> entry is itself a bare <c>$ref</c>) and terminates unconditionally,
/// so a future <c>$ref</c> cycle cannot spin here.
/// </para>
/// <para>
/// <b>The 8&#160;KB bound is a whole-entry bound.</b> When a section's annotations exceed it, the
/// digest stops at the last COMPLETE field entry and says so — a summary cut mid-sentence would be
/// worse than a short one, and a host that needs the rest already has <c>format:
/// "json-schema"</c>. Bytes, not characters: the promise is about wire size, and the schema's prose
/// contains multi-byte punctuation.
/// </para>
/// <para>
/// <b>EVERYTHING is charged against that budget</b>, not just the field entries: the heading, the
/// version line, and the section's own description are all accounted before the first bullet is
/// admitted, so <see cref="MaxSummaryBytes"/> is a postcondition of <see cref="Render"/> rather than
/// a property of the schema that happens to be pinned today. The section description is
/// engine-authored prose of no fixed length; a future pin bump could bring one that alone exceeds
/// the budget, and it is dropped whole (with the same truncation notice) rather than clipped
/// mid-sentence or allowed to breach the bound.
/// </para>
/// </remarks>
public static class SchemaSummaryRenderer
{
    /// <summary>The maximum UTF-8 size of a rendered summary (this story's acceptance criterion).</summary>
    public const int MaxSummaryBytes = 8 * 1024;

    /// <summary>
    /// Bytes held back from <see cref="MaxSummaryBytes"/> so the truncation notice always fits after
    /// the last entry that was admitted. Generous relative to the notice's real size (~140 bytes
    /// with three-digit counts) because under-reserving would be a silent bound violation.
    /// </summary>
    private const int TruncationNoticeReserveBytes = 256;

    /// <summary>Renders the digest for <paramref name="subtree"/>.</summary>
    /// <param name="section">The section token as the caller wrote it — echoed into the heading.</param>
    /// <param name="schemaVersion">The composed schema's own version marker.</param>
    /// <param name="subtree">The addressed subtree (see <see cref="SchemaSectionResolver"/>).</param>
    /// <param name="schemaRoot">The whole document, needed only to follow local <c>$ref</c>s.</param>
    public static string Render(string section, string schemaVersion, JsonElement subtree, JsonElement schemaRoot)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(schemaVersion);

        // A step section resolves to an if/then clause: the fields live under `then`. Descending
        // here rather than in the resolver keeps the resolver's answer faithful to the schema's own
        // structure (the clause IS the step type's definition) while letting the digest show the
        // part an author can act on.
        var described = subtree.ValueKind == JsonValueKind.Object
            && !subtree.TryGetProperty("properties", out _)
            && subtree.TryGetProperty("then", out var then)
            && then.ValueKind == JsonValueKind.Object
                ? then
                : subtree;

        var budget = MaxSummaryBytes - TruncationNoticeReserveBytes;

        var builder = new StringBuilder();
        // `section` is caller-supplied. It reaches here only on the success path, where the resolver
        // has already matched it against a closed token set — so today it is provably a literal or a
        // schema constant. Routed through SanitiseForEcho anyway, exactly like every sibling message
        // in this codebase: the day a fuzzy-matching or suggesting resolver lands, this heading must
        // not be the one place that echoes untrusted text raw into an agent-facing document.
        builder.Append("# vouchfx language schema — `").Append(VfxCode.SanitiseForEcho(section)).Append("`\n\n");
        builder.Append("Schema version: `").Append(schemaVersion).Append("`\n");

        // Counted incrementally rather than by re-measuring the whole builder each time: the latter
        // is quadratic in the entry count, and the loop below runs over every documented field of a
        // section that may have hundreds.
        //
        // The header block is charged against the SAME budget as the field entries, so
        // MaxSummaryBytes is a postcondition of this method rather than an assertion about the
        // schema that happens to be pinned today. The heading and version line are bounded by
        // construction (SanitiseForEcho caps the echo; the version is a short schema constant), but
        // the section description is engine-authored prose of no fixed length — a future pin could
        // bring one that alone exceeds 8 KB, and it must clamp rather than silently blow the bound.
        var usedBytes = Encoding.UTF8.GetByteCount(builder.ToString());

        var descriptionOmitted = false;
        if (ReadDescription(described) is { } sectionDescription)
        {
            var block = "\n" + sectionDescription + "\n";
            var blockBytes = Encoding.UTF8.GetByteCount(block);
            if (usedBytes + blockBytes <= budget)
            {
                builder.Append(block);
                usedBytes += blockBytes;
            }
            else
            {
                // Whole-block, like every other cut here: a description clipped mid-sentence would
                // misrepresent the schema's own words, which this renderer never does.
                descriptionOmitted = true;
            }
        }

        var entries = BuildFieldEntries(described, schemaRoot);
        var rendered = 0;

        if (entries.Count > 0)
        {
            const string FieldsHeader = "\n## Fields\n\n";
            var fieldsHeaderBytes = Encoding.UTF8.GetByteCount(FieldsHeader);
            if (usedBytes + fieldsHeaderBytes <= budget)
            {
                builder.Append(FieldsHeader);
                usedBytes += fieldsHeaderBytes;

                foreach (var entry in entries)
                {
                    var entryBytes = Encoding.UTF8.GetByteCount(entry);
                    if (usedBytes + entryBytes > budget)
                    {
                        break;
                    }

                    builder.Append(entry);
                    usedBytes += entryBytes;
                    rendered++;
                }
            }
        }

        if (descriptionOmitted || rendered < entries.Count)
        {
            builder.Append("\n_Summary truncated at the 8 KB budget");
            if (descriptionOmitted)
            {
                builder.Append(" (this section's own description did not fit)");
            }

            builder.Append(": ").Append(rendered).Append(" of ").Append(entries.Count)
                .Append(" documented fields rendered. Call get_schema again with format \"json-schema\" for the complete section._\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// One markdown bullet per DOCUMENTED property, in the schema's own member order (never
    /// re-sorted: the order an author reads is the order the engine declares).
    /// </summary>
    private static List<string> BuildFieldEntries(JsonElement described, JsonElement schemaRoot)
    {
        var entries = new List<string>();

        if (described.ValueKind != JsonValueKind.Object
            || !described.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return entries;
        }

        var requiredNames = ReadRequiredNames(described);

        foreach (var property in properties.EnumerateObject())
        {
            var fieldSchema = FollowLocalRef(property.Value, schemaRoot);

            // THE omission rule: no description, no entry. Not a placeholder, not a bare name.
            if (ReadDescription(fieldSchema) is not { } description)
            {
                continue;
            }

            var builder = new StringBuilder("- **").Append(property.Name).Append("**");

            var annotations = new List<string>(2);
            if (ReadTypeAnnotation(fieldSchema) is { } typeAnnotation)
            {
                annotations.Add(typeAnnotation);
            }

            if (requiredNames.Contains(property.Name))
            {
                annotations.Add("required");
            }

            if (annotations.Count > 0)
            {
                builder.Append(" (").Append(string.Join(", ", annotations)).Append(')');
            }

            builder.Append(" — ").Append(description).Append('\n');
            entries.Add(builder.ToString());
        }

        return entries;
    }

    /// <summary>
    /// Resolves a bare <c>{"$ref": "#/a/b"}</c> against <paramref name="schemaRoot"/>, ONCE. Any
    /// other shape (including a <c>$ref</c> alongside sibling keywords, or an external/unsupported
    /// pointer) is returned untouched — this helper exists to recover a description, never to
    /// implement JSON Schema reference resolution.
    /// </summary>
    private static JsonElement FollowLocalRef(JsonElement element, JsonElement schemaRoot)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("$ref", out var reference)
            || reference.ValueKind != JsonValueKind.String
            || reference.GetString() is not { } pointer
            || !pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return element;
        }

        var current = schemaRoot;
        foreach (var rawToken in pointer[2..].Split('/'))
        {
            // RFC 6901 escaping: "~1" is '/', "~0" is '~'. Order matters — "~01" must decode to
            // "~1", not to "/".
            var token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(token, out var next))
            {
                return element;
            }

            current = next;
        }

        return current;
    }

    private static HashSet<string> ReadRequiredNames(JsonElement described)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (!described.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? ReadDescription(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("description", out var description)
        && description.ValueKind == JsonValueKind.String
        && description.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// The declared JSON type, as the schema states it — a bare string (<c>"string"</c>) or the
    /// union form (<c>["string", "number"]</c>, which the schema really does use for
    /// <c>step.timeout</c>). Absent when the subschema declares none.
    /// </summary>
    private static string? ReadTypeAnnotation(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type))
        {
            return null;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            JsonValueKind.Array => string.Join(
                " | ",
                type.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())),
            _ => null,
        };
    }
}
