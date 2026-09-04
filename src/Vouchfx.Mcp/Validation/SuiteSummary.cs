using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// A bounded, factual digest of what a parsed suite document actually contains (Sprint 2 /
/// US-S2-02): what an authoring agent would otherwise have to re-derive by re-reading the YAML it
/// just asked this server to validate.
/// </summary>
/// <param name="Steps">How many entries the document's top-level <c>steps</c> array has.</param>
/// <param name="StepTypes">The distinct <c>type</c> values those steps declare, in first-appearance order.</param>
/// <param name="Services">The logical names under <c>environment.services</c>.</param>
/// <param name="Dependencies">The logical names under <c>environment.dependencies</c>.</param>
/// <param name="Captures">The distinct capture variable names any step's <c>capture</c> map declares.</param>
/// <param name="Placeholders">
/// The distinct <c>{name}</c> interpolation tokens used anywhere in the document's string values.
/// <b>Never a <c>${secret:…}</c> reference</b> — see <see cref="SuiteSummaryBuilder"/>.
/// </param>
/// <remarks>
/// <para>
/// <b>No field ever carries a <c>${secret:…}</c> reference — values AND names alike.</b> The
/// obvious half is <see cref="Placeholders"/>, which is scanned out of string VALUES. The
/// non-obvious half is that a secret reference can also appear as a NAME: nothing stops an author
/// writing a capture variable, service, dependency, or step type whose identifier is literally
/// <c>${secret:vault/prod-db-password}</c>, and those four lists are built from identifiers taken
/// verbatim out of the document. Echoing one back would publish the caller's secret STORE LAYOUT
/// (source and path) in a tool result — the same disclosure the placeholder scan exists to prevent,
/// arriving through a different door, and on a <c>valid: true</c> result at that. The rule is
/// therefore applied in ONE place for ALL five lists (<c>SuiteSummaryBuilder.OrderedNameSet.Add</c>
/// drops any name containing <c>${</c>), rather than per collection site where a sixth list added
/// later could forget it.
/// </para>
/// <para>
/// <b>Every list is capped at <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> (1 000) entries,
/// and truncation is silent</b> — there is no "truncated" flag and no diagnostic. A summary is a
/// digest for orientation, not an inventory to compute against: a document with more than a
/// thousand distinct step types, service names, capture variables, or placeholder tokens is already
/// past the point where reading a flat list helps. Treat a list of exactly 1 000 entries as
/// possibly incomplete. See <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> for why the cap
/// exists at all.
/// </para>
/// <para>
/// <b>Descriptive, never prescriptive.</b> Every field states something the document says; none of
/// them is a judgement about whether it should say it. "This capture is never used" and "this
/// placeholder names nothing" are SEMANTIC FINDINGS and belong in the semantic channel
/// (<c>Validation/Semantics</c>, US-S2-03's rules) — never here. Keeping the two apart is what lets
/// a host present the summary unconditionally, including for a suite that failed validation.
/// </para>
/// <para>
/// <b>Order is document order, never sorted.</b> A reader lines these lists up against the file in
/// front of them; sorting would break that correspondence for no gain.
/// </para>
/// <para>
/// <b>Do not compare two summaries with <c>==</c>.</b> A record's generated equality uses
/// <c>EqualityComparer&lt;T&gt;.Default</c> per member, which for an
/// <see cref="IReadOnlyList{T}"/> is REFERENCE equality — so two summaries with byte-identical
/// contents compare unequal. Nothing in this server compares them; a test that needs to (see
/// <c>SemanticSeamTests</c>) compares the six fields itself. Left as-is rather than given a custom
/// <c>Equals</c>: a hand-written one on a six-member record is more surface to keep correct than
/// the one call site it would serve.
/// </para>
/// </remarks>
public sealed record SuiteSummary(
    int Steps,
    IReadOnlyList<string> StepTypes,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Captures,
    IReadOnlyList<string> Placeholders);

/// <summary>
/// Derives a <see cref="SuiteSummary"/> from the JSON projection of a suite document —
/// <b>the one <see cref="SuiteValidator"/> already built for schema evaluation</b>, never a second
/// parse of the same YAML.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-parse discipline is a measured constraint, not a preference.</b> See
/// <see cref="SuiteValidator.ValidateYaml"/>'s own note: an earlier revision re-parsed per finding
/// and was measured at 31.9 seconds on a 2 000-error suite against the validation worker's
/// 10-second budget. This type therefore takes a <see cref="JsonElement"/> — a view into a document
/// that already exists — and can only ever be called where one is in hand.
/// </para>
/// <para>
/// <b>Every field is derived from what the composed schema actually defines</b> (see
/// <c>vendored/composed-schema.v1.json</c>): <c>environment.services</c> and
/// <c>environment.dependencies</c> are objects KEYED by logical name, so their names are property
/// names, not values; a step's <c>capture</c> is likewise a map of variable name to extractor
/// expression, so the names are its keys and the JSONPath/XPath expressions are deliberately not
/// reported.
/// </para>
/// </remarks>
public static class SuiteSummaryBuilder
{
    /// <summary>
    /// The most entries any one summary list carries. A summary is a digest, not a second copy of
    /// the suite: a 5&#160;MB document (<see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>) full of
    /// distinct placeholder tokens could otherwise produce a list with hundreds of thousands of
    /// entries — bounded only by the worker's 50&#160;MB output cap, which would report the whole
    /// call as a worker failure rather than degrading gracefully. Truncating here keeps a
    /// pathological document's summary useless-but-harmless instead of turning it into an error.
    /// </summary>
    public const int MaxEntriesPerList = 1000;

    /// <summary>Characters a <c>{placeholder}</c> name may contain — see <see cref="CollectPlaceholders"/>.</summary>
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
    /// <c>{conn::&lt;name&gt;}</c> (see <c>vendored/language-reference.md</c>) — real tokens a real
    /// suite writes. Excluding <c>:</c> made the summary silently under-report every suite that uses
    /// a service endpoint or a connection string, which is most suites with an environment block.
    /// </para>
    /// <para>
    /// <b>What still keeps a <c>${secret:…}</c> reference out is the <c>$</c> guard in
    /// <see cref="CollectPlaceholdersFromText"/>, and it always was.</b> That guard — a <c>{</c>
    /// immediately preceded by <c>$</c> opens nothing — is the load-bearing half of the hygiene
    /// rule; excluding <c>:</c> here was only ever a second, incidental line of defence. <c>/</c>
    /// stays excluded, so a <c>${secret:source/path}</c> could still not be mined as a token even if
    /// the guard were removed, and <c>"</c> stays excluded so an inline JSON body (an Elasticsearch
    /// <c>query</c>, a DynamoDB <c>key</c> template) is not mined for imaginary tokens: a
    /// <c>{"query":…}</c> opens on a quote, so no token starts.
    /// </para>
    /// </remarks>
    private static bool IsPlaceholderNameChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':';

    /// <summary>Builds the summary for <paramref name="root"/>, the suite document's JSON projection.</summary>
    /// <remarks>
    /// Never throws for a document of an unexpected shape: a suite whose <c>steps</c> is a scalar,
    /// or whose <c>environment</c> is a string, is a SCHEMA violation the schema pass reports — this
    /// type's job is to describe what is there, so anything that is not the shape it expects simply
    /// contributes nothing rather than becoming a second, differently-worded complaint.
    /// </remarks>
    public static SuiteSummary Build(JsonElement root)
    {
        var stepCount = 0;
        var stepTypes = new OrderedNameSet();
        var captures = new OrderedNameSet();

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("steps", out var steps) &&
            steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                stepCount++;

                if (step.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (step.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    stepTypes.Add(type.GetString());
                }

                if (step.TryGetProperty("capture", out var capture) && capture.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in capture.EnumerateObject())
                    {
                        captures.Add(entry.Name);
                    }
                }
            }
        }

        var services = CollectMapKeys(root, "services");
        var dependencies = CollectMapKeys(root, "dependencies");

        var placeholders = new OrderedNameSet();
        CollectPlaceholders(root, placeholders);

        return new SuiteSummary(
            stepCount,
            stepTypes.Ordered,
            services,
            dependencies,
            captures.Ordered,
            placeholders.Ordered);
    }

    /// <summary>
    /// The property names of <c>environment.&lt;mapName&gt;</c> — the logical names the schema keys
    /// its <c>services</c>/<c>dependencies</c> objects by.
    /// </summary>
    private static List<string> CollectMapKeys(JsonElement root, string mapName)
    {
        var names = new OrderedNameSet();

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("environment", out var environment) &&
            environment.ValueKind == JsonValueKind.Object &&
            environment.TryGetProperty(mapName, out var map) &&
            map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                names.Add(entry.Name);
            }
        }

        return names.Ordered;
    }

    /// <summary>
    /// Walks every string value in the document and collects the <c>{name}</c> interpolation tokens
    /// they contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A hand-written linear scan rather than a regular expression</b>, deliberately: the input
    /// is untrusted suite content up to <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>, and a
    /// backtracking pattern over text that size is exactly the shape a catastrophic-backtracking
    /// (ReDoS) input targets. This scan is O(n) in the string's length with no backtracking
    /// possible, which needs no timeout to be safe.
    /// </para>
    /// <para>
    /// <b><c>${secret:…}</c> is skipped, and that is a hygiene requirement, not a nicety.</b> The
    /// engine's secret-reference syntax opens with <c>${</c>; a naive brace scan would report
    /// <c>secret:vault/api-token</c> as a "placeholder" and publish the caller's secret STORE LAYOUT
    /// (source and path) in a tool result. This server never resolves a secret reference and never
    /// echoes one (CLAUDE.md's secret-hygiene invariant), so a <c>{</c> immediately preceded by
    /// <c>$</c> opens nothing. The name charset below also excludes <c>/</c>, so a
    /// <c>${secret:source/path}</c> could not pass even if the <c>$</c> check were removed — but
    /// that is now the only backstop, because <c>:</c> is deliberately admitted for the engine's
    /// <c>{svc::…}</c>/<c>{conn::…}</c> forms (see <see cref="IsPlaceholderNameChar"/>).
    /// <b>Do not remove the <c>$</c> guard.</b>
    /// </para>
    /// <para>
    /// The charset's exclusion of <c>"</c> is what keeps inline JSON bodies — an Elasticsearch
    /// <c>query</c>, a DynamoDB <c>key</c> template — from being mined for imaginary placeholders:
    /// <c>{"query":{…}}</c> opens on a quote, so no token starts. Only a real, bare interpolation
    /// token matches.
    /// </para>
    /// </remarks>
    private static void CollectPlaceholders(JsonElement element, OrderedNameSet sink)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectPlaceholders(property.Value, sink);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPlaceholders(item, sink);
                }

                break;

            case JsonValueKind.String:
                CollectPlaceholdersFromText(element.GetString(), sink);
                break;

            default:
                // Numbers, booleans, and null carry no interpolation tokens.
                break;
        }
    }

    private static void CollectPlaceholdersFromText(string? text, OrderedNameSet sink)
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

            // `${` opens a secret reference, never a placeholder — see this method's remarks.
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
                sink.Add(text[start..end]);

                // Resume after the closing brace: a name cannot nest, so nothing inside the token
                // needs re-scanning.
                i = end;
            }
        }
    }

    /// <summary>
    /// A distinct, insertion-ordered, capped collection of names — the shape every summary list
    /// has, written once rather than four times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cap is applied on ADD rather than on materialisation so a pathological document never
    /// accumulates an unbounded intermediate list in the worker's memory either — the set stops
    /// growing at <see cref="MaxEntriesPerList"/> and keeps rejecting cheaply from there.
    /// </para>
    /// <para>
    /// The secret-reference exclusion lives here for the same reason: it is a property of EVERY
    /// summary list, not of the four or five sites that happen to feed them today. See
    /// <see cref="Add"/>.
    /// </para>
    /// </remarks>
    private sealed class OrderedNameSet
    {
        private readonly List<string> _ordered = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        /// <summary>
        /// Records <paramref name="name"/> unless it is empty, would exceed
        /// <see cref="MaxEntriesPerList"/>, or contains a <c>${…}</c> reference.
        /// </summary>
        /// <remarks>
        /// <b>The <c>${</c> test is the secret-hygiene rule applied to NAMES</b>, and it is here
        /// rather than at the call sites so no future list can be added without it. The four
        /// name-derived lists (step types, service names, dependency names, capture variables) take
        /// their entries verbatim from the document, and nothing prevents an author naming a capture
        /// <c>${secret:vault/prod-db-password}</c> — an identifier that would then be echoed into
        /// <c>summary.captures</c> on an otherwise <c>valid: true</c> result, disclosing the secret
        /// store's layout exactly as <see cref="CollectPlaceholders"/>'s scan of string VALUES
        /// exists to prevent. A name that carries a secret reference is dropped from the summary
        /// entirely rather than redacted: this server is not the redaction authority (CLAUDE.md),
        /// and an omitted name costs a reader nothing they cannot get from the document itself.
        /// Substring-tested, not prefix-tested, because a reference can be embedded
        /// (<c>prefix-${secret:…}</c>) as easily as it can lead.
        /// </remarks>
        public void Add(string? name)
        {
            if (string.IsNullOrEmpty(name) || _ordered.Count >= MaxEntriesPerList)
            {
                return;
            }

            if (name.Contains("${", StringComparison.Ordinal))
            {
                return;
            }

            if (_seen.Add(name))
            {
                _ordered.Add(name);
            }
        }

        /// <summary>The names collected so far, in insertion order. Not copied — this set is done with them.</summary>
        public List<string> Ordered => _ordered;
    }
}
