using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Everything <c>validate_suite</c> v2 learned about one suite (Sprint 2 / US-S2-02): the schema
/// pass's verdict, the semantic pass's findings, and the document's own summary.
/// </summary>
/// <param name="Valid"><see langword="true"/> only when <paramref name="Errors"/> is empty.</param>
/// <param name="Errors">
/// The SCHEMA channel, unchanged from v1: every problem the <see cref="SuiteValidator"/> pipeline
/// found, plus the pipeline's own failures (see <see cref="SuiteValidationError"/>).
/// </param>
/// <param name="SemanticDiagnostics">
/// The SEMANTIC channel: findings from the rules pass. US-S2-03 adds the NEW semantic rules at 1202
/// through 1211 in the VFX-D-12xx range (written without their prefix here for the reason
/// <c>Validation/Semantics/SemanticAnalysis.cs</c>'s header records) and migrates the existing
/// unknown-step-type finding — which already ships on VFX-D-1201, emitted today by the SCHEMA pass's
/// cross-check
/// (<c>SuiteValidator.AppendUnknownStepTypeErrors</c>) — into this channel, per the sprint spec.
/// That reuse is deliberate: 1201 keeps its code and its catalogue page, and only changes which
/// channel carries it. Empty at <see cref="ValidationLevel.Schema"/>, and — until the rules land —
/// empty at every level.
/// </param>
/// <param name="Summary">
/// The parsed document's own digest, or <see langword="null"/> when no document was ever built (a
/// missing file, a rejected path, a YAML-bomb guard rejection, a parse failure, a killed worker).
/// </param>
/// <param name="Level">
/// The level the call actually ran at — the caller's <c>level</c> argument, or
/// <see cref="ValidationLevels.Default"/> when they named none.
/// </param>
/// <remarks>
/// <para>
/// <b>The two channels never merge, and that is the point of the shape.</b> A schema violation is a
/// statement about the document's CONFORMANCE, checked by the engine's own composed schema and
/// therefore identical to what <c>vouchfx validate</c> would say; a semantic finding is this
/// server's own advice about a document the schema already accepts. Folding them into one array
/// would make an agent unable to tell "the engine will reject this" from "this looks wrong to us",
/// and would put this server's opinions inside a channel whose whole value is that it does not have
/// any. Two arrays, always both present.
/// </para>
/// <para>
/// <b>This is also the worker's wire type</b> (see <see cref="ValidationWorkerProtocol"/>): the
/// semantic pass consumes the parsed document, which only exists inside the isolated worker
/// process, so both the semantic findings and the summary are produced there and travel back
/// together with the schema verdict. That is why this record — not a tool-layer-only shape — is what
/// crosses the boundary.
/// </para>
/// <para>
/// <b><see cref="Level"/> is echoed because <see cref="Valid"/> alone is ambiguous.</b> At
/// <see cref="ValidationLevel.Semantic"/> the schema pass does not run, so — <b>for a document that
/// parses</b> — <see cref="Errors"/> is empty for the trivial reason that nothing looked, and
/// <see cref="Valid"/>, which reports exactly "the schema channel is empty", therefore reads
/// <see langword="true"/> on no evidence about schema conformance at all. (The scope matters: a
/// guard rejection or a parse failure populates <see cref="Errors"/> at EVERY level, including this
/// one — those run before either pass and are not gated by <see cref="Level"/> at all. The
/// ambiguity is confined to documents that got as far as being parsed.) A consumer holding only
/// <c>{valid: true}</c> cannot tell that apart from a suite the engine would accept. Carrying the
/// effective level beside the verdict is
/// what makes the distinction machine-readable, and it costs one token on the wire. The narrower
/// alternatives (suppressing <see cref="Valid"/>, or making it nullable) would both reshape a field
/// <c>run_suite</c> and every existing caller already read.
/// </para>
/// <para>
/// <b>Deliberately NOT a widening of <see cref="ValidateSuiteResult"/>.</b> That record is
/// <c>run_suite</c>'s EDGE-003 pre-flight envelope as well as validate_suite v1's payload
/// (<c>RunSuiteInvalidPayload</c>), and adding fields to it would reshape <c>run_suite</c>'s wire as
/// a side effect of a <c>validate_suite</c> story. <see cref="AsValidationResult"/> is the one-way
/// narrowing that keeps <c>run_suite</c> and <see cref="Tools.ValidationOutcomeRenderer"/> reading
/// exactly the shape they always have.
/// </para>
/// </remarks>
public sealed record SuiteAnalysis(
    bool Valid,
    IReadOnlyList<SuiteValidationError> Errors,
    IReadOnlyList<Diagnostic> SemanticDiagnostics,
    SuiteSummary? Summary,
    ValidationLevel Level)
{
    /// <summary>
    /// Narrows this analysis to the v1 <see cref="ValidateSuiteResult"/> shape — for
    /// <c>run_suite</c>'s pre-flight envelope and the diagnostic/error split in
    /// <see cref="Tools.ValidationOutcomeRenderer"/>, neither of which has any use for the two new
    /// channels.
    /// </summary>
    /// <remarks>
    /// A METHOD rather than a property, deliberately: a get-only property on a record is serialised,
    /// so a <c>Validation</c> property here would silently add a duplicate <c>{valid, errors}</c>
    /// object to every <c>validate_suite</c> result and to the worker's wire.
    /// </remarks>
    public ValidateSuiteResult AsValidationResult() => new(Valid, Errors);

    /// <summary>
    /// Wraps a schema-pass-only <paramref name="validation"/> as an analysis with an empty semantic
    /// channel — the shape every pre-parse outcome has (a fast reject, a guard rejection, a worker
    /// failure), where no document was built and so neither a semantic pass nor a summary was
    /// possible.
    /// </summary>
    /// <remarks>
    /// <paramref name="level"/> is still reported even though no pass ran: it says what the call
    /// ASKED for, which is what a consumer needs to interpret the outcome — and every one of these
    /// outcomes carries a <see cref="SuiteValidationError"/> saying why nothing could run, so there
    /// is no risk of it being read as "this level's pass produced a clean verdict".
    /// </remarks>
    public static SuiteAnalysis FromValidation(ValidateSuiteResult validation, ValidationLevel level)
    {
        ArgumentNullException.ThrowIfNull(validation);

        return new SuiteAnalysis(validation.Valid, validation.Errors, [], null, level);
    }
}

/// <summary>
/// Which suite <c>validate_suite</c> was asked about: a file on disk (<c>path</c>) or YAML text the
/// caller supplied directly (<c>yaml</c>, US-S2-02). Exactly one, never both — the tool boundary
/// enforces that before one of these is ever constructed (see <c>Tools/ValidateSuiteInput</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Both sources cross the same process-isolation boundary.</b> Inline YAML is untrusted content
/// in exactly the way a file's content is — more so, since it never had to be written to disk first
/// — so it gets no shortcut past <see cref="ValidationWorkerClient"/>, the wall clock, or the
/// whole-tree kill. The only difference between the two is how the bytes reach the worker.
/// </para>
/// <para>
/// <b>Never logged, never echoed.</b> <see cref="InlineYaml"/> holds caller-supplied suite text
/// which may contain <c>${secret:…}</c> references; this server relays neither the text nor those
/// references anywhere (CLAUDE.md's secret-hygiene invariant). The one place it is written is the
/// worker's stdin. <see cref="ToString"/> is overridden to make that invariant STRUCTURAL rather
/// than a rule to remember: a record's generated <c>ToString</c> prints every property, so the
/// default would have spilled the whole suite body into any interpolated string, log line, or test
/// assertion message that happened to mention a source.
/// </para>
/// </remarks>
public sealed record SuiteSource
{
    private SuiteSource(string? path, string? inlineYaml)
    {
        Path = path;
        InlineYaml = inlineYaml;
    }

    /// <summary>The suite file's path, or <see langword="null"/> when this is an inline source.</summary>
    public string? Path { get; }

    /// <summary>The caller-supplied suite text, or <see langword="null"/> when this is a file source.</summary>
    public string? InlineYaml { get; }

    /// <summary>Whether this source carries YAML text rather than naming a file.</summary>
    public bool IsInline => InlineYaml is not null;

    /// <summary>A suite named by its path on disk.</summary>
    public static SuiteSource FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return new SuiteSource(path, inlineYaml: null);
    }

    /// <summary>A suite supplied as YAML text.</summary>
    public static SuiteSource FromInlineYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        return new SuiteSource(path: null, yaml);
    }

    /// <summary>
    /// The literal <c>"inline"</c> for an inline source, or the path for a file source — never the
    /// suite text.
    /// </summary>
    /// <remarks>
    /// Overriding this is the enforcement of the "never logged, never echoed" rule in this type's
    /// own remarks: the record's generated <c>ToString</c> would have printed
    /// <c>InlineYaml = &lt;the caller's whole suite&gt;</c>, secret references and all, the first
    /// time anyone interpolated a source into a message. Making the safe rendering the DEFAULT one
    /// means no future call site has to know the rule exists.
    /// </remarks>
    public override string ToString() => IsInline ? "inline" : Path!;
}
