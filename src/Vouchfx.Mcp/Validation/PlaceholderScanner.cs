using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The ONE <c>{placeholder}</c> scanner: the linear, ReDoS-proof, secret-hygiene-aware scan that
/// finds interpolation tokens in a document's string values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extracted from <see cref="SuiteSummaryBuilder"/> so the semantic pass cannot drift from the
/// digest.</b> <c>SuiteSummary.Placeholders</c>, <c>SuiteFacts.Placeholders</c> and US-S2-03's
/// order-aware VFX-D-1203 rule all have to agree on what a placeholder IS — a second, subtly
/// different scan in the rule would make "this placeholder names nothing" true of a token the fact
/// set never recorded, i.e. a wrong finding on a valid suite. One implementation, three callers.
/// </para>
/// <para>
/// <b>A hand-written linear scan rather than a regular expression</b>, deliberately: the input is
/// untrusted suite content up to <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>, and a
/// backtracking pattern over text that size is exactly the shape a catastrophic-backtracking
/// (ReDoS) input targets. This scan is O(n) in the string's length with no backtracking possible,
/// which needs no timeout to be safe.
/// </para>
/// <para>
/// <b><c>${secret:…}</c> is skipped, and that is a hygiene requirement, not a nicety.</b> The
/// engine's secret-reference syntax opens with <c>${</c>; a naive brace scan would report
/// <c>secret:vault/api-token</c> as a "placeholder" and publish the caller's secret STORE LAYOUT
/// (source and path) in a tool result. This server never resolves a secret reference and never
/// echoes one (CLAUDE.md's secret-hygiene invariant), so a <c>{</c> immediately preceded by
/// <c>$</c> opens nothing. The name charset below also excludes <c>/</c>, so a
/// <c>${secret:source/path}</c> could not pass even if the <c>$</c> check were removed — but that
/// is now the only backstop, because <c>:</c> is deliberately admitted for the engine's
/// <c>{svc::…}</c>/<c>{conn::…}</c> forms (see <see cref="IsPlaceholderNameChar"/>).
/// <b>Do not remove the <c>$</c> guard.</b>
/// </para>
/// </remarks>
internal static class PlaceholderScanner
{
    /// <summary>
    /// The separator the engine's RESERVED placeholder forms use — <c>{svc::&lt;name&gt;.&lt;field&gt;}</c>,
    /// <c>{conn::&lt;name&gt;}</c>, <c>{__outcome::…}</c> (see <c>vendored/language-reference.md</c>).
    /// </summary>
    /// <remarks>
    /// Exposed rather than kept private because it is the discriminator VFX-D-1203 needs: a token
    /// carrying it resolves from the ENVIRONMENT, not from a capture or a root variable, so testing
    /// it against <c>captures ∪ variables</c> would report "nothing provides this" for a token the
    /// engine resolves perfectly well — a wrong finding on a valid suite. The digest, by contrast,
    /// wants those tokens listed, so the scan itself does not filter them.
    /// </remarks>
    public const string ReservedFormSeparator = "::";

    /// <summary>Characters a <c>{placeholder}</c> name may contain.</summary>
    /// <remarks>
    /// <para>
    /// <b>Unicode-aware, not ASCII-only.</b> A placeholder names a Vars key, and a Vars key is
    /// whatever a step's <c>capture</c> called it — the composed schema constrains a capture name
    /// only by a reserved-prefix pattern (<c>svc::</c>, <c>conn::</c>, <c>__outcome::</c>, …), never
    /// by a character class. An ASCII-only scan would therefore silently under-report a legitimate
    /// suite written in any language but English.
    /// </para>
    /// <para>
    /// <b><c>:</c> is admitted precisely BECAUSE of those reserved prefixes.</b> The engine's
    /// documented interpolation forms include <c>{svc::&lt;name&gt;.&lt;field&gt;}</c> and
    /// <c>{conn::&lt;name&gt;}</c> — real tokens a real suite writes. Excluding <c>:</c> made the
    /// summary silently under-report every suite that uses a service endpoint or a connection
    /// string, which is most suites with an environment block.
    /// </para>
    /// <para>
    /// The exclusion of <c>"</c> is what keeps inline JSON bodies — an Elasticsearch <c>query</c>, a
    /// DynamoDB <c>key</c> template — from being mined for imaginary placeholders:
    /// <c>{"query":{…}}</c> opens on a quote, so no token starts. Only a real, bare interpolation
    /// token matches.
    /// </para>
    /// </remarks>
    public static bool IsPlaceholderNameChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':';

    /// <summary>
    /// The type prefix whose steps carry C# SOURCE rather than interpolable suite text.
    /// </summary>
    private const string ScriptTypePrefix = "script.";

    /// <summary>
    /// Walks every string value under <paramref name="element"/> and hands each <c>{name}</c> token
    /// it finds to <paramref name="sink"/>, in document order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callable on a whole document or on one step's object — VFX-D-1203 needs the latter, because
    /// its whole question is WHICH step used the token.
    /// </para>
    /// <para>
    /// <b><paramref name="excludeScriptStepSource"/> is a correctness switch, not a tuning knob.</b>
    /// A <c>script.csharp</c> step's <c>code</c> is a C# program, and C# spells its own string
    /// interpolation with braces: <c>$"order {id} created"</c> inside a script yields a
    /// <c>{id}</c> token that this scan cannot tell from a suite placeholder. It is not one — the
    /// engine never interpolates a script body against the Vars context; the script reads
    /// <c>Vars["id"]</c> itself. Mining it produced VFX-D-1203 findings naming variables no suite
    /// placeholder ever used, on valid suites. The step's <c>file</c> property is excluded for the
    /// same reason with an extra one on top: it is a PATH to source this server never reads, so any
    /// brace in it is a directory name.
    /// </para>
    /// <para>
    /// <b>Both production callers pass <see langword="true"/>, deliberately and identically.</b> The
    /// digest (<see cref="SuiteSummaryBuilder"/>) and the VFX-D-1203 rule must agree on what a
    /// placeholder is — that agreement is why this scanner was extracted at all — so a
    /// <c>summary.placeholders</c> list containing <c>{id}</c> mined out of C# would be the same
    /// falsehood as the finding, merely rendered as data. The parameter exists so each call site
    /// STATES the choice rather than inheriting it, and so a test can drive the raw scan.
    /// </para>
    /// </remarks>
    public static void Scan(JsonElement element, Action<string> sink, bool excludeScriptStepSource = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isScriptStep = excludeScriptStepSource && IsScriptStep(element);

                foreach (var property in element.EnumerateObject())
                {
                    if (isScriptStep && property.Name is "code" or "file")
                    {
                        continue;
                    }

                    Scan(property.Value, sink, excludeScriptStepSource);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Scan(item, sink, excludeScriptStepSource);
                }

                break;

            case JsonValueKind.String:
                ScanText(element.GetString(), sink);
                break;

            default:
                // Numbers, booleans, and null carry no interpolation tokens.
                break;
        }
    }

    /// <summary>Hands every <c>{name}</c> token in <paramref name="text"/> to <paramref name="sink"/>.</summary>
    public static void ScanText(string? text, Action<string> sink)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            // `${` opens a secret reference, never a placeholder — see this type's remarks.
            if (i > 0 && text[i - 1] == '$')
            {
                continue;
            }

            var start = i + 1;
            var end = start;
            while (end < text.Length && IsPlaceholderNameChar(text[end]))
            {
                end++;
            }

            if (end > start && end < text.Length && text[end] == '}')
            {
                sink(text[start..end]);

                // Resume after the closing brace: a name cannot nest, so nothing inside the token
                // needs re-scanning.
                i = end;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is one of the engine's RESERVED forms — a token that
    /// resolves from the environment rather than from a capture or a root variable.
    /// </summary>
    public static bool IsReservedForm(string name) =>
        name.Contains(ReservedFormSeparator, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="element"/> is a step object whose <c>type</c> names the
    /// <c>script.*</c> family — the one family whose own properties carry C# source.
    /// </summary>
    /// <remarks>
    /// Prefix-tested on the family rather than matched against <c>script.csharp</c> exactly, so a
    /// second scripting provider added upstream inherits the exclusion instead of silently
    /// reintroducing the false positive.
    /// </remarks>
    public static bool IsScriptStep(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() is { } name &&
        name.StartsWith(ScriptTypePrefix, StringComparison.Ordinal);
}
