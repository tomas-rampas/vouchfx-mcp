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
/// <param name="Truncated">
/// <see langword="true"/> when at least one of the lists above dropped a name it would otherwise
/// have carried because that list had already reached
/// <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> — i.e. this digest is known to be
/// incomplete. Never set by the <c>${…}</c> hygiene filter, which is a deliberate omission rather
/// than a shortfall.
/// </param>
/// <remarks>
/// <para>
/// <b>This record is the CALLER-FACING digest, and it is lossy by design.</b> Two filters shape it:
/// the <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> cap, and the <c>${…}</c> name exclusion
/// below. <b>Nothing may use it to decide set membership</b> — "this placeholder names no capture",
/// "this target names no declared service" — because both filters produce exactly the false negative
/// such a decision would turn into a wrong finding on a valid suite. Code that needs to answer a
/// "X is not declared" question reads <see cref="SuiteFacts"/> (which the semantic seam hands every
/// rule) or walks <see cref="Semantics.SemanticAnalysisContext.Document"/> itself.
/// </para>
/// <para>
/// <b>No field ever carries a <c>${secret:…}</c> reference — values AND names alike.</b> The
/// obvious half is <see cref="Placeholders"/>, which is scanned out of string VALUES. The
/// non-obvious half is that a secret reference can also appear as a NAME: nothing stops an author
/// writing a capture variable, service, dependency, or step type whose identifier is literally
/// <c>${secret:vault/prod-db-password}</c>, and those four lists are built from identifiers taken
/// verbatim out of the document. Echoing one back would publish the caller's secret STORE LAYOUT
/// (source and path) in a tool result — the same disclosure the placeholder scan exists to prevent,
/// arriving through a different door, and on a <c>valid: true</c> result at that. The rule is
/// therefore applied in ONE place for ALL five lists (<c>SuiteSummaryBuilder.NameCollector.Add</c>
/// drops any name containing <c>${</c> from the WIRE list — never from the fact set, which is not
/// published), rather than per collection site where a sixth list added later could forget it.
/// </para>
/// <para>
/// <b>Every list is capped at <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> (1 000) entries,
/// and <see cref="Truncated"/> says when that cap actually bit.</b> A summary is a digest for
/// orientation, not an inventory to compute against: a document with more than a thousand distinct
/// step types, service names, capture variables, or placeholder tokens is already past the point
/// where reading a flat list helps. The flag exists so a reader never has to INFER incompleteness
/// from a list length of exactly 1 000 — a heuristic that is both wrong on a suite with exactly
/// 1 000 distinct names and unavailable to anything reading a single list. See
/// <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> for why the cap exists at all.
/// </para>
/// <para>
/// <b><see cref="Truncated"/> is deliberately one flag for the whole record, not one per list.</b>
/// It answers the only question a consumer can act on — "may I treat this digest as complete?" —
/// and a per-list flag would multiply the wire shape by five to refine an answer that is "no" for
/// every use either way. It is also deliberately NOT raised by the <c>${…}</c> exclusion: that
/// filter is a permanent property of every summary (a name carrying a secret reference is never
/// published, at any size), so a flag that flipped for it would report "incomplete" on a healthy
/// suite and train readers to ignore it.
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
/// <c>SemanticSeamTests</c>) compares the seven fields itself. Left as-is rather than given a custom
/// <c>Equals</c>: a hand-written one on a seven-member record is more surface to keep correct than
/// the one call site it would serve.
/// </para>
/// </remarks>
public sealed record SuiteSummary(
    int Steps,
    IReadOnlyList<string> StepTypes,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Captures,
    IReadOnlyList<string> Placeholders,
    bool Truncated);

/// <summary>
/// The COMPLETE, unfiltered name sets one suite document declares — the internal half of
/// <see cref="SuiteSummaryBuilder"/>'s single walk, handed to every semantic rule via
/// <see cref="Semantics.SemanticAnalysisContext.Facts"/> and <b>never serialised anywhere</b>.
/// </summary>
/// <param name="StepTypes">Every distinct <c>type</c> a step declares.</param>
/// <param name="Services">Every logical name under <c>environment.services</c>.</param>
/// <param name="Dependencies">Every logical name under <c>environment.dependencies</c>.</param>
/// <param name="Captures">Every distinct capture variable name any step's <c>capture</c> map declares.</param>
/// <param name="Placeholders">Every distinct <c>{name}</c> interpolation token used in a string value.</param>
/// <param name="Variables">
/// Every name declared in the document's root <c>variables</c> block.
/// <para>
/// <b>Why this sixth set exists here and has no counterpart in <see cref="SuiteSummary"/>.</b> The
/// composed schema (<c>vendored/composed-schema.v1.json</c>) makes root <c>variables</c> a
/// first-class, name-keyed declaration surface — constants pre-loaded into the shared variable
/// context before the first step runs, carrying the same reserved-prefix rule a <c>capture</c> name
/// does. A rule deciding "this placeholder names nothing" must therefore test against
/// <c>Captures ∪ Variables</c>: a <c>{region}</c> token that resolves to a declared variable is
/// correct, and reporting it because only captures were consulted would be a wrong VFX-D finding on
/// a valid suite. The wire <see cref="SuiteSummary"/> omits it because the sprint spec fixes that
/// object's shape at six fields, and widening a published result shape is a separate decision from
/// giving rules the facts they need. That divergence is the point of this type existing: the wire
/// shape is a contract, the fact set is whatever the rules must know.
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <b>Uncapped and unfiltered, on purpose — this is the SET-MEMBERSHIP authority.</b>
/// <see cref="SuiteSummary"/> is a digest: it drops any name containing <c>${</c> and stops at
/// <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> entries. Deciding "X is not declared" from
/// a set with either property is how a rule emits a false finding on a valid suite — a capture
/// literally named <c>${secret:…}</c> would look undeclared, and so would every name past the
/// thousandth on a large suite. These sets have neither property.
/// </para>
/// <para>
/// <b>Never crosses the worker's process boundary, and must not start.</b> It is built inside the
/// isolated validation worker, consumed by the semantic pass in that same process, and discarded;
/// only <see cref="SuiteAnalysis"/> — schema errors, semantic findings, and the digest — is
/// serialised back. Two reasons, both load-bearing: the <c>${…}</c> names it deliberately retains
/// are exactly what the summary's hygiene filter exists to keep out of a tool result, and its size
/// is bounded only by the document (up to <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>), which
/// the wire is not.
/// </para>
/// <para>
/// Exposed as <see cref="IReadOnlySet{T}"/> rather than a list because every consumer asks
/// <c>Contains</c>, and an <c>IReadOnlyList.Contains</c> over a large suite inside a rule loop is
/// the quadratic shape this seam's whole design (see
/// <c>Validation/Semantics/SemanticAnalysis.cs</c>'s header) exists to avoid. Ordinal comparison,
/// matching the wire lists: a suite name is an identifier the engine matches byte-for-byte.
/// </para>
/// </remarks>
public sealed record SuiteFacts(
    IReadOnlySet<string> StepTypes,
    IReadOnlySet<string> Services,
    IReadOnlySet<string> Dependencies,
    IReadOnlySet<string> Captures,
    IReadOnlySet<string> Placeholders,
    IReadOnlySet<string> Variables)
{
    /// <summary>The facts of a document with nothing in it — for a caller that has no document.</summary>
    public static SuiteFacts Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Both products of <see cref="SuiteSummaryBuilder.Build"/>'s single walk: the caller-facing
/// <see cref="SuiteSummary"/> and the internal <see cref="SuiteFacts"/>.
/// </summary>
/// <remarks>
/// Returned as one value rather than exposed as two entry points precisely so no caller can walk
/// the document twice to get both — the measured hazard this whole pipeline is shaped around (see
/// <see cref="SuiteValidator.AnalyseYaml"/>'s note on the 31.9-second re-parse). The two are derived
/// from the same names in the same pass; they differ only in what each one is allowed to drop.
/// </remarks>
public sealed record SuiteDigest(SuiteSummary Summary, SuiteFacts Facts);

/// <summary>
/// Derives a <see cref="SuiteDigest"/> — the caller-facing <see cref="SuiteSummary"/> and the
/// internal <see cref="SuiteFacts"/> together — from the JSON projection of a suite document,
/// <b>the one <see cref="SuiteValidator"/> already built for schema evaluation</b>, never a second
/// parse of the same YAML.
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE walk produces BOTH.</b> Every name enters through
/// <see cref="NameCollector.Add(string?)"/>, which records it in the complete fact set and then, if
/// it survives the hygiene filter and the cap, in the published list. There is no second pass and
/// no second traversal to add: a "facts" walk beside the summary walk would double the cost of the
/// exact operation the note below says must not be repeated.
/// </para>
/// <para>
/// <b>Single-parse discipline is a measured constraint, not a preference.</b> See
/// <see cref="SuiteValidator.ValidateYaml"/>'s own note: an earlier revision re-parsed per finding
/// and was measured at 31.9 seconds on a 2 000-error suite against the validation worker's
/// 10-second budget. This type therefore takes a <see cref="JsonElement"/> — a view into a document
/// that already exists — and can only ever be called where one is in hand.
/// </para>
/// <para>
/// <b>Every field is derived from what the composed schema actually defines</b> (see
/// <c>vendored/composed-schema.v1.json</c>): <c>environment.services</c>,
/// <c>environment.dependencies</c>, and the root <c>variables</c> block are objects KEYED by
/// logical name, so their names are property names, not values; a step's <c>capture</c> is likewise
/// a map of variable name to extractor expression, so the names are its keys and the JSONPath/XPath
/// expressions are deliberately not reported.
/// </para>
/// </remarks>
public static class SuiteSummaryBuilder
{
    /// <summary>
    /// The most entries any one <see cref="SuiteSummary"/> list carries — <b>a bound on what is
    /// PUBLISHED, not on what is collected</b> (<see cref="SuiteFacts"/> is uncapped by design).
    /// A summary is a digest, not a second copy of the suite: a 5&#160;MB document
    /// (<see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>) full of distinct placeholder tokens could
    /// otherwise produce a list with hundreds of thousands of entries — crossing the worker pipe and
    /// landing in a tool result, bounded only by the worker's 50&#160;MB output cap, which would
    /// report the whole call as a worker failure rather than degrading gracefully. Truncating here
    /// keeps a pathological document's summary useless-but-harmless instead of turning it into an
    /// error, and <see cref="SuiteSummary.Truncated"/> says when it did.
    /// </summary>
    /// <remarks>
    /// The cap no longer bounds the worker's own memory, and that change is deliberate: the fact set
    /// retains every distinct name, so a pathological document's names are held in full for the
    /// duration of one worker process. That is bounded by the document itself (5&#160;MB of input
    /// cannot yield more than 5&#160;MB of substrings), lives in a short-lived isolated process under
    /// a 10-second wall clock, and is the price of rules that can answer "is this name declared?"
    /// correctly. What must stay bounded is the WIRE, and that is what this constant does.
    /// </remarks>
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

    /// <summary>
    /// Builds the digest — published summary and internal fact set — for <paramref name="root"/>,
    /// the suite document's JSON projection.
    /// </summary>
    /// <remarks>
    /// Never throws for a document of an unexpected shape: a suite whose <c>steps</c> is a scalar,
    /// or whose <c>environment</c> is a string, is a SCHEMA violation the schema pass reports — this
    /// type's job is to describe what is there, so anything that is not the shape it expects simply
    /// contributes nothing rather than becoming a second, differently-worded complaint.
    /// </remarks>
    public static SuiteDigest Build(JsonElement root)
    {
        var stepCount = 0;
        var stepTypes = new NameCollector();
        var captures = new NameCollector();
        var services = new NameCollector();
        var dependencies = new NameCollector();
        var placeholders = new NameCollector();

        // Facts-only: the root `variables` block is a rule input, not a summary field — see
        // SuiteFacts.Variables for why the two shapes diverge here.
        var variables = new NameCollector(onWire: false);

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

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("environment", out var environment))
        {
            CollectMapKeys(environment, "services", services);
            CollectMapKeys(environment, "dependencies", dependencies);
        }

        CollectMapKeys(root, "variables", variables);

        CollectPlaceholders(root, placeholders);

        var summary = new SuiteSummary(
            stepCount,
            stepTypes.Published,
            services.Published,
            dependencies.Published,
            captures.Published,
            placeholders.Published,
            // Only the five PUBLISHED lists can truncate — `variables` never reaches the wire, so
            // its (absent) list cannot make this digest incomplete.
            stepTypes.Truncated
                || services.Truncated
                || dependencies.Truncated
                || captures.Truncated
                || placeholders.Truncated);

        var facts = new SuiteFacts(
            stepTypes.Facts,
            services.Facts,
            dependencies.Facts,
            captures.Facts,
            placeholders.Facts,
            variables.Facts);

        return new SuiteDigest(summary, facts);
    }

    /// <summary>
    /// Records the property names of <paramref name="owner"/>'s <paramref name="mapName"/> object —
    /// the logical names the schema keys <c>environment.services</c>,
    /// <c>environment.dependencies</c>, and the root <c>variables</c> block by.
    /// </summary>
    private static void CollectMapKeys(JsonElement owner, string mapName, NameCollector sink)
    {
        if (owner.ValueKind == JsonValueKind.Object &&
            owner.TryGetProperty(mapName, out var map) &&
            map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                sink.Add(entry.Name);
            }
        }
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
    private static void CollectPlaceholders(JsonElement element, NameCollector sink)
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

    private static void CollectPlaceholdersFromText(string? text, NameCollector sink)
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
    /// One name list, collected ONCE for both of this builder's outputs: the complete
    /// <see cref="SuiteFacts"/> set, and the distinct, insertion-ordered, capped,
    /// <c>${…}</c>-filtered <see cref="SuiteSummary"/> list. Written once rather than six times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fact set is the one both sides dedupe against</b>, which is what keeps the two
    /// products consistent for free: the published list is by construction a subset of the facts, so
    /// there is no second <c>seen</c> set to fall out of step with it, and a name rejected as a
    /// duplicate can never be miscounted as a truncation.
    /// </para>
    /// <para>
    /// The cap is applied on ADD rather than on materialisation so the published list is never
    /// larger than the wire allows even transiently. It bounds the LIST only; see
    /// <see cref="MaxEntriesPerList"/> for why the fact set deliberately keeps growing past it.
    /// </para>
    /// <para>
    /// The secret-reference exclusion lives here for the same reason it always did: it is a property
    /// of EVERY published list, not of the five sites that happen to feed them today. See
    /// <see cref="Add"/>.
    /// </para>
    /// </remarks>
    private sealed class NameCollector
    {
        private readonly List<string>? _published;
        private readonly HashSet<string> _facts = new(StringComparer.Ordinal);
        private bool _truncated;

        /// <param name="onWire">
        /// <see langword="false"/> for a set that exists only as a rule input and has no
        /// <see cref="SuiteSummary"/> field to fill — currently just the root <c>variables</c> block.
        /// Such a collector builds no list at all rather than building one nobody reads.
        /// </param>
        public NameCollector(bool onWire = true) => _published = onWire ? [] : null;

        /// <summary>
        /// Records <paramref name="name"/> in the fact set unless it is empty, and additionally in
        /// the published list unless it contains a <c>${…}</c> reference or the list is already at
        /// <see cref="MaxEntriesPerList"/>.
        /// </summary>
        /// <remarks>
        /// <para>
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
        /// </para>
        /// <para>
        /// <b>The filter applies to the published list ONLY.</b> The fact set keeps such a name,
        /// because it is a name the document really declares and a rule asking "is this capture
        /// declared?" must answer yes for it. That set never leaves the worker process (see
        /// <see cref="SuiteFacts"/>), so retaining it discloses nothing.
        /// </para>
        /// </remarks>
        public void Add(string? name)
        {
            if (string.IsNullOrEmpty(name) || !_facts.Add(name))
            {
                return;
            }

            if (_published is null || name.Contains("${", StringComparison.Ordinal))
            {
                return;
            }

            // Reached only for a name that is new AND publishable — so the flag means "a name a
            // reader would otherwise have seen was dropped", never "a duplicate arrived late".
            if (_published.Count >= MaxEntriesPerList)
            {
                _truncated = true;
                return;
            }

            _published.Add(name);
        }

        /// <summary>
        /// The published names, in insertion order. Not copied — this collector is done with them.
        /// </summary>
        /// <remarks>
        /// Throws rather than returning an empty list when this collector has no wire field: an
        /// empty list here would become a <see cref="SuiteSummary"/> field silently reporting that
        /// the document declares nothing of that kind, which is a wrong answer dressed as a valid
        /// one. The condition is unreachable today (only <c>variables</c> is facts-only, and it
        /// fills no summary field) and exists so that stays true.
        /// </remarks>
        public List<string> Published => _published
            ?? throw new InvalidOperationException(
                "This collector is facts-only and has no published list; see SuiteFacts.Variables.");

        /// <summary>Every distinct name seen, unfiltered and uncapped.</summary>
        public HashSet<string> Facts => _facts;

        /// <summary>Whether the cap dropped a name <see cref="Published"/> would otherwise carry.</summary>
        public bool Truncated => _truncated;
    }
}
