using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

// Vouchfx.Mcp.Validation.Semantics — the shared plumbing every rule in this directory uses
// (Sprint 2 / US-S2-03).
//
// Three concerns live here, each one a place a rule could otherwise get the seam's contract subtly
// wrong on its own:
//
//   1. ADDRESSING. A finding names its subject twice — once for the host (Diagnostic.Path, a
//      JSONPath, per that record's own field documentation) and once for line resolution
//      (a JSON Pointer, which is what YamlLineResolver walks and what the schema channel's
//      InstancePath already speaks). SuitePath builds both from one call so the two can never
//      describe different nodes.
//
//   2. LOCATION. A DiagnosticLocation needs a file, a line and a column, and its Column is not
//      nullable — so a rule that resolved only a line could not build one at all.
//      SemanticFinding.Create does the whole thing, or leaves Location null when the document did
//      not parse to a mapping (an inline fragment, an aliased root) rather than inventing a
//      position.
//
//   3. IDENTIFIER HYGIENE. SemanticAnalysisContext.Facts deliberately retains names literally
//      spelled `${secret:vault/prod-db-password}`, which makes "interpolate the name into the
//      message" the natural first draft AND an instant call failure at the Analyse choke point.
//      SemanticFinding.Identifier is the one sanctioned way to put a document-derived name into
//      prose: bounded, control-character-escaped, and withheld entirely when it carries a
//      reference.

/// <summary>
/// One node in the suite, addressed both ways at once: as the JSONPath a
/// <see cref="Diagnostic.Path"/> carries, and as the JSON Pointer
/// <see cref="YamlLineResolver"/> resolves to a source mark.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two spellings rather than one.</b> <see cref="Diagnostic.Path"/>'s own documentation
/// fixes its vocabulary as a JSONPath (<c>$.steps[2].match.key</c>), and that is what a host reads.
/// <see cref="YamlLineResolver"/>, the schema channel's <c>InstancePath</c>, and the engine's own
/// <c>DocumentValidator</c> all speak RFC 6901 pointers (<c>/steps/2/match/key</c>). Deriving one
/// from the other at each call site is how the two drift; deriving both from one builder is how
/// they cannot.
/// </para>
/// <para>
/// <b>Immutable and structural.</b> Each step returns a new value, so a rule can hold a step's path
/// and hang several field paths off it without any of them mutating the others.
/// </para>
/// </remarks>
internal readonly record struct SuitePath(string JsonPath, string Pointer)
{
    /// <summary>The document root — the base every other path is built from.</summary>
    public static SuitePath Root => new("$", string.Empty);

    /// <summary>The <c>steps</c> array's <paramref name="index"/>-th element.</summary>
    public static SuitePath Step(int index) => Root.Property("steps").Index(index);

    /// <summary>This node's <paramref name="index"/>-th array element.</summary>
    public SuitePath Index(int index) =>
        new($"{JsonPath}[{index}]", $"{Pointer}/{index}");

    /// <summary>This node's <paramref name="name"/> property.</summary>
    /// <remarks>
    /// <para>
    /// <b>Callers must not pass a name carrying a <c>${…}</c> reference.</b> The result becomes
    /// <see cref="Diagnostic.Path"/>, which the <see cref="SemanticAnalyser"/> choke point checks —
    /// so splicing a fact-set name in here fails the whole call exactly as splicing it into a
    /// message would. A rule addressing a secret-named entry stops at its CONTAINER (the capture
    /// map, say) instead; <see cref="UnusedCaptureRule"/> is the worked example.
    /// </para>
    /// <para>
    /// <b>The JSONPath half is SANITISED and CAPPED; the pointer half is deliberately neither.</b>
    /// <paramref name="name"/> is an author-chosen mapping key taken verbatim out of an untrusted
    /// document, and the JSONPath is the half that ships — <see cref="Diagnostic.Path"/> reaches a
    /// host and may reach a terminal. It therefore goes through
    /// <see cref="VfxCode.SanitiseForEcho"/> (cap, then
    /// <see cref="TextSanitiser.SanitiseForDisplay"/>), exactly as the SCHEMA channel already treats
    /// its own <c>instancePath</c> and for the same stated reason: raw ASCII control bytes fail
    /// earlier as a YAML parse error, but a bidi override (U+202E) or a zero-width joiner reaches
    /// here intact, and <see cref="TextSanitiser"/>'s contract is that no such value leaves this
    /// server unrendered. The POINTER is what
    /// <see cref="YamlLineResolver.ResolveMark"/> walks to find the node's source line: escaping it
    /// would make every non-ASCII key unresolvable and silently drop the location from findings on
    /// perfectly good suites, so it keeps the raw name and never leaves the process.
    /// </para>
    /// <para>
    /// <b>Ordering dependency with the <see cref="SemanticAnalyser"/> choke point, stated so nobody
    /// "tightens" the sanitiser and breaks the guard.</b> That guard tests
    /// <see cref="Diagnostic.Path"/> for the literal <c>${</c>. Both <c>$</c> (0x24) and <c>{</c>
    /// (0x7B) are printable ASCII, so <see cref="TextSanitiser.SanitiseForDisplay"/> passes them
    /// through unchanged and a reference spliced into a path is still visible to the guard AFTER
    /// this call. A sanitiser that escaped either character would silently disarm the check.
    /// </para>
    /// </remarks>
    public SuitePath Property(string name)
    {
        // Rendered ONCE and then tested, so the bare/bracket decision is made about the text that
        // actually ships: an escape sequence introduces a backslash, which is not a bare-name
        // character, so a bidi-carrying key correctly takes the quoted form.
        var rendered = VfxCode.SanitiseForEcho(name);

        return new(
            IsBarePropertyName(rendered) ? $"{JsonPath}.{rendered}" : $"{JsonPath}['{rendered}']",
            $"{Pointer}/{EncodePointerSegment(name)}");
    }

    /// <summary>
    /// Whether <paramref name="name"/> can follow a dot in a JSONPath, or needs bracket-quoting.
    /// </summary>
    /// <remarks>
    /// RFC 9535's shorthand member name is <c>[A-Za-z_]</c> followed by <c>[A-Za-z0-9_]</c> — no
    /// hyphen. A hyphenated key (<c>orders-db</c>) therefore takes the bracket form, which every
    /// JSONPath implementation accepts, rather than a dotted form some of them would parse as a
    /// subtraction.
    /// </remarks>
    private static bool IsBarePropertyName(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Encodes one JSON Pointer segment per RFC 6901 — <c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>.
    /// The exact inverse of <see cref="YamlLineResolver"/>'s own decoder, and <b>order matters</b>:
    /// <c>~</c> first, or the tilde introduced by encoding a slash gets encoded again.
    /// </summary>
    private static string EncodePointerSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal)
               .Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>
/// The cursor a WHOLE-DOCUMENT walk carries instead of a <see cref="SuitePath"/>: a pushed/popped
/// stack of segments that becomes a real path only when a finding actually fires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not speculative.</b> <see cref="SecretLiteralRule"/> is the one rule that visits
/// every node in the document rather than a handful of named fields, and its first revision built a
/// <see cref="SuitePath"/> — two fresh interpolated strings, each a copy of the whole prefix — at
/// EVERY node on the way down. That is quadratic in depth × prefix length and it is paid in full on
/// a document with no findings at all: a 4.3&#160;MB finding-free suite took <b>37.8 seconds</b>
/// against the validation worker's 10-second wall clock, so it surfaced as VFX-E-1150 (a killed
/// worker) rather than as a slow rule. Carrying the ancestry as segments and materialising once,
/// in the finding arm, took the same fixture to <b>4.1 seconds</b> — that figure is the WHOLE call
/// (process start, YAML parse, summary and all ten rules), so the walk's own share of it is smaller
/// still.
/// </para>
/// <para>
/// <b>Push/pop discipline.</b> <see cref="TryPushProperty"/> returns whether it pushed, and a caller
/// pops only when it did: a property name carrying a <c>${…}</c> reference is deliberately NOT
/// pushed, so its subtree is addressed at the PARENT instead of smuggling the reference out through
/// <see cref="Diagnostic.Path"/> (which the <see cref="SemanticAnalyser"/> choke point fails the call
/// for). That is the same rule <see cref="SuitePath.Property"/>'s own remarks state; this type is
/// where a walking rule obeys it without having to remember to.
/// </para>
/// </remarks>
internal sealed class SuitePathBuilder
{
    /// <summary>One step of ancestry: a named property, or an array index when <c>Name</c> is null.</summary>
    private readonly record struct Segment(string? Name, int Index);

    private readonly List<Segment> _segments = [];

    /// <summary>Descends into the <paramref name="index"/>-th element of the current node.</summary>
    public void PushIndex(int index) => _segments.Add(new Segment(null, index));

    /// <summary>
    /// Descends into the <paramref name="name"/> property, unless that name carries a <c>${…}</c>
    /// reference.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the segment was pushed and the caller must
    /// <see cref="Pop"/> it; <see langword="false"/> when the name was skipped for hygiene and the
    /// subtree is addressed at the parent.
    /// </returns>
    public bool TryPushProperty(string name)
    {
        if (name.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        _segments.Add(new Segment(name, 0));
        return true;
    }

    /// <summary>Ascends one segment.</summary>
    public void Pop() => _segments.RemoveAt(_segments.Count - 1);

    /// <summary>
    /// Materialises the current position as a <see cref="SuitePath"/>. <b>The only allocation site
    /// on the walk, and it belongs inside a finding arm</b> — see this type's remarks.
    /// </summary>
    public SuitePath Build()
    {
        var path = SuitePath.Root;
        foreach (var segment in _segments)
        {
            path = segment.Name is { } name ? path.Property(name) : path.Index(segment.Index);
        }

        return path;
    }
}

/// <summary>
/// Builds the <see cref="Diagnostic"/> a rule returns — the one place a semantic finding's code,
/// path, location and docs URL are assembled.
/// </summary>
internal static class SemanticFinding
{
    /// <summary>Severity for a finding this server is certain about (today: only VFX-D-1207).</summary>
    public const string Error = "error";

    /// <summary>Severity for authoring advice — the default for this channel. See the catalogue's
    /// note on why five spec-unannotated codes ship at this level.</summary>
    public const string Warning = "warning";

    /// <summary>Severity for a finding that changes nothing about how the suite runs.</summary>
    public const string Info = "info";

    /// <summary>
    /// The stand-in a message uses instead of an identifier it must not reproduce — see
    /// <see cref="Identifier"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately describes the WITHHOLDING rather than naming the reference form: the phrase
    /// reaches a host, and spelling the form out here would put the very token this server refuses
    /// to echo into every such message. The finding's <see cref="Diagnostic.Path"/> still says
    /// exactly where to look, which is what the reader actually needs.
    /// </para>
    /// <para>
    /// <b>Quoted, like every real name <see cref="Identifier"/> renders.</b> The two spellings appear
    /// in the same sentence position in the same messages, and an unquoted stand-in read as prose the
    /// rule had written rather than as the slot where a name would have been.
    /// </para>
    /// </remarks>
    public const string WithheldIdentifier = "'(a name this server does not echo)'";

    /// <summary>
    /// Builds a finding for <paramref name="code"/>, resolving <paramref name="path"/> to a source
    /// location against the document the seam already parsed.
    /// </summary>
    /// <param name="context">The rule's context — the source of both the file identity and the YAML marks.</param>
    /// <param name="code">One of <see cref="VfxCodeCatalogue"/>'s <c>VFX-D-</c> constants.</param>
    /// <param name="severity">One of <see cref="Error"/>, <see cref="Warning"/>, <see cref="Info"/>.</param>
    /// <param name="message">Prose composed from bounded identifiers only — never raw document text.</param>
    /// <param name="path">
    /// The node the finding is about, or <see langword="null"/> when the finding is about the
    /// document as a whole and no node addresses it (an ABSENT <c>metadata</c> block, say).
    /// </param>
    /// <param name="fix">A candidate fix, for the one code whose remedy is a single literal line.</param>
    public static Diagnostic Create(
        SemanticAnalysisContext context,
        string code,
        string severity,
        string message,
        SuitePath? path = null,
        DiagnosticFix? fix = null) =>
        VfxCodeCatalogue.CreateDiagnostic(
            code,
            severity,
            message,
            LocationFor(context, path),
            path?.JsonPath,
            fix);

    /// <summary>
    /// Resolves <paramref name="path"/> to a <see cref="DiagnosticLocation"/>, or
    /// <see langword="null"/> when there is nothing honest to report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three ways this returns <see langword="null"/>, all of them "no position is KNOWN" rather
    /// than "no position exists": the finding names no node; the document did not parse to a YAML
    /// mapping (so the seam holds no representation model to walk); or the pointer addresses
    /// nothing in that model. Inventing line 1 for any of them would be a wrong answer wearing a
    /// precise shape — a host would jump the author's editor to the wrong place.
    /// </para>
    /// <para>
    /// <b><see cref="DiagnosticLocation.File"/> is the CALLER's own suite identity</b> — a path they
    /// supplied, or the inline marker — echoed back sanitised, never anything derived from document
    /// content. That is exactly why the <see cref="SemanticAnalyser"/> choke point excludes this one
    /// field from its <c>${…}</c> check: a workspace directory containing those characters must not
    /// crash every finding on every suite under it.
    /// </para>
    /// </remarks>
    private static DiagnosticLocation? LocationFor(SemanticAnalysisContext context, SuitePath? path)
    {
        if (path is null)
        {
            return null;
        }

        if (YamlLineResolver.ResolveMark(context.YamlRoot, path.Value.Pointer) is not { } mark)
        {
            return null;
        }

        var (line, column) = mark;

        // YamlDotNet marks are longs and DiagnosticLocation's are ints. A suite big enough to
        // overflow an int line number is orders of magnitude past YamlSafetyGuard's size cap, so the
        // clamp is unreachable rather than lossy — it exists so the conversion cannot be the thing
        // that throws inside a rule (ISemanticRule: "a rule must not throw").
        return new DiagnosticLocation(
            context.SourceName,
            (int)Math.Clamp(line, 1, int.MaxValue),
            (int)Math.Clamp(column, 1, int.MaxValue),
            EndLine: null,
            EndColumn: null);
    }

    /// <summary>
    /// Renders a document-derived name for inclusion in a message: bounded, control-character
    /// escaped, and <b>withheld entirely when it carries a <c>${…}</c> reference</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one sanctioned way to name a thing in semantic prose.</b>
    /// <see cref="SemanticAnalysisContext.Facts"/> retains names the published
    /// <see cref="SuiteSummary"/> filters out — including identifiers literally spelled
    /// <c>${secret:vault/prod-db-password}</c> — because retaining them is what makes "is this
    /// capture declared?" answerable at all. The cost is that interpolating one into a message
    /// publishes the caller's secret STORE LAYOUT on an otherwise clean result, which the
    /// <see cref="SemanticAnalyser"/> choke point turns into a failed call. This method is the
    /// pre-emption: the finding is still reported, and the reader is still told where to look via
    /// <see cref="Diagnostic.Path"/> — only the name itself is dropped.
    /// </para>
    /// <para>
    /// The reference test comes FIRST and is decisive: <see cref="VfxCode.SanitiseForEcho"/> caps
    /// and escapes, but <c>$</c> and <c>{</c> are both printable ASCII, so it would pass a reference
    /// straight through. Substring-tested, not prefix-tested, because a reference can be embedded
    /// (<c>prefix-${secret:…}</c>) as easily as it can lead — the same predicate
    /// <c>SuiteSummaryBuilder.NameCollector.Add</c> and the choke point itself both use.
    /// </para>
    /// </remarks>
    public static string Identifier(string? name) =>
        name is not null && name.Contains("${", StringComparison.Ordinal)
            ? WithheldIdentifier
            : $"'{VfxCode.SanitiseForEcho(name)}'";
}

/// <summary>
/// The two document shapes nearly every rule starts from, read once and correctly rather than
/// re-derived per rule.
/// </summary>
/// <remarks>
/// <b>Tolerant by construction, because a rule must not throw.</b> A document whose <c>steps</c> is
/// a scalar, or whose entries are strings, is a SCHEMA violation the schema pass reports; these
/// helpers simply yield nothing for it. A rule that instead assumed the shape would turn a
/// malformed suite into <c>VFX-E-1901</c> (a crashed worker) and lose every finding the other nine
/// rules produced — the failure mode <c>ISemanticRule</c>'s contract exists to prevent.
/// </remarks>
internal static class SuiteDocument
{
    /// <summary>
    /// Every OBJECT element of the document's top-level <c>steps</c> array, with its index, in
    /// document order.
    /// </summary>
    /// <remarks>
    /// <b>The index is the document's, not the sequence's</b> — a non-object element is skipped but
    /// still counted, so a reported <c>$.steps[3]</c> is the fourth entry in the author's file even
    /// when the second one is malformed. Getting that wrong would point every subsequent finding at
    /// the wrong step.
    /// </remarks>
    public static IEnumerable<(int Index, JsonElement Step)> Steps(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("steps", out var steps) ||
            steps.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.Object)
            {
                yield return (index, step);
            }

            index++;
        }
    }

    /// <summary>
    /// <paramref name="element"/>'s <paramref name="name"/> property as a non-empty string, or
    /// <see langword="null"/> when it is absent, empty, or not a string.
    /// </summary>
    public static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
