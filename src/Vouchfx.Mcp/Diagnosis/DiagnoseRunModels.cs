using System.Collections.Frozen;

namespace Vouchfx.Mcp.Diagnosis;

/// <summary>
/// The four — and only four — scopes a <see cref="SpecEditProposal"/> may carry (US-S4-03).
/// </summary>
/// <remarks>
/// <b>A closed set, enumerated here rather than spelled inline at each construction site.</b>
/// US-S4-03's acceptance criterion is that "a future addition must deliberately widen this list,
/// never grow it by accident", and US-S4-05 will derive its regression guard from source — both are
/// far easier against one named vocabulary than against string literals scattered through a builder.
/// The scopes bound what a proposal may touch: an ENVIRONMENT declaration, a TIMEOUT/verify mode, a
/// MATCH key, or a CAPTURE path. There is deliberately no scope for an assertion — see
/// <see cref="SpecEditProposalBuilder"/>.
/// </remarks>
public static class SpecEditScopes
{
    /// <summary>An <c>environment</c> declaration: a service image, a dependency version/image, a seed target.</summary>
    public const string Environment = "environment";

    /// <summary>A step's own <c>timeout</c> or <c>verifyMode</c>.</summary>
    public const string Timeouts = "timeouts";

    /// <summary>A step's <c>match</c> criteria — the key or headers it polls on, never the expected VALUE.</summary>
    public const string Match = "match";

    /// <summary>A step's <c>capture</c> extractor expression (the JSONPath/XPath that produced nothing).</summary>
    public const string Capture = "capture";

    /// <summary>Every scope above — the set US-S4-03's enumeration test pins.</summary>
    public static FrozenSet<string> All { get; } =
        new[] { Environment, Timeouts, Match, Capture }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// US-S4-03's second proposal kind: a scoped, review-only suite edit for an
/// <c>EnvironmentError</c>/<c>Inconclusive</c> outcome — plan D2's Healer SUPERSET, never a
/// replacement for <see cref="FailProposal"/>.
/// </summary>
/// <param name="StepId">
/// The step this edit concerns, or <see langword="null"/> when it concerns an ENVIRONMENT-ERROR
/// RECORD rather than a step.
/// <para>
/// <b>Nullable, which the acceptance criterion's field list does not say — an interpretation, stated
/// rather than smoothed over.</b> The AC lists <c>stepId</c> on every proposal, but it ALSO routes
/// <c>pull</c>/<c>unhealthy</c>/<c>seed</c> to the <c>environment</c> scope, and US-S4-01 assigns
/// those three kinds ONLY to environment-error records — which carry a resource name and no step id
/// at all. Every environment-scoped proposal is therefore necessarily step-less. Reporting the
/// resource name in this field would be a category error (it is not a step), and inventing a
/// placeholder id would be worse, so the field is null and the resource is named in
/// <see cref="Rationale"/> and <see cref="SuggestedEdit"/> instead.
/// </para>
/// <para>
/// <b>Carried RAW — deliberately NOT capped, unlike every identifier spliced into
/// <see cref="SuggestedEdit"/>.</b> This is a CORRELATION key: a host joins it against
/// <c>explain_run</c>'s <c>notableSteps[].stepId</c> and <see cref="FailProposal.StepId"/>, both of
/// which carry <c>SuiteEventParser</c>'s uncapped value, so truncating it here would break the join
/// that makes a proposal locatable at all. The cost is a real residual — ten proposals can carry
/// ~20&#160;KB of step ids — which <see cref="SpecEditProposalBuilder"/>'s remarks quantify and
/// which US-S4-04's shrink ladder, not a second cap, is the place to absorb.
/// </para>
/// </param>
/// <param name="Scope">One of <see cref="SpecEditScopes"/>' four values — never anything else.</param>
/// <param name="Rationale">Short text grounded in the classified <see cref="VerdictReason"/>'s own evidence.</param>
/// <param name="SuggestedEdit">
/// A YAML FRAGMENT a human may adapt — explicitly NOT a unified diff against a real file, because
/// this server was never given one to diff against (the same review-only framing
/// <see cref="FailProposalBuilder"/>'s patch comment already uses). Never applied by this server;
/// <c>diagnose_run</c> is <c>ReadOnly</c> and opens no suite file for writing.
/// </param>
public sealed record SpecEditProposal(string? StepId, string Scope, string Rationale, string SuggestedEdit);

/// <summary>
/// One review-only Fail patch proposal from <c>diagnose_run</c> (Spec C / M2 Healer) — never
/// auto-applied, never produced for EnvironmentError or Pass, and never a suite-rewrite for
/// Inconclusive.
/// </summary>
/// <param name="StepId">The failing step's identifier.</param>
/// <param name="Rationale">Short text grounded in the step's event observation/diff evidence.</param>
/// <param name="Patch">
/// Unified-diff style review comment block (or YAML fragment placeholders) for that step — evidence-
/// based only when no suite path is supplied (v1).
/// </param>
public sealed record FailProposal(string StepId, string Rationale, string Patch);

/// <summary>
/// Spec C / REQ-002's <c>diagnose_run</c> result: the same taxonomy-faithful diagnosis as
/// <c>explain_run</c>, plus Fail-only review proposals and EnvironmentError infrastructure guidance.
/// </summary>
/// <param name="Diagnosis">
/// The full <see cref="Diagnosis"/> payload (verdict, notable steps, environment errors, paths,
/// truncation flags) — identical taxonomy rules to <c>explain_run</c>.
/// </param>
/// <param name="Proposals">
/// Review-only patches for step-level <c>Fail</c> outcomes that carried usable observation evidence.
/// Empty for Pass, pure EnvironmentError, and Inconclusive (no suite-rewrite patches).
/// </param>
/// <param name="EnvironmentGuidance">
/// Infrastructure-oriented checklist when environment-error evidence is present (or overall verdict
/// is <c>EnvironmentError</c>). Empty when there is no environment-error signal. Never accompanies
/// YAML rewrite proposals for those env failures.
/// </param>
/// <param name="SpecEditProposals">
/// US-S4-03's scoped spec-edit proposals for <c>EnvironmentError</c>/<c>Inconclusive</c> material —
/// a SECOND, distinct list, never a replacement for <paramref name="Proposals"/> (plan D2:
/// superset). Empty for Pass, and empty for every <c>Fail</c> step: a Fail step's only proposal is
/// its existing review comment, which US-S4-01's rule table makes structural rather than
/// conventional (see <see cref="SpecEditProposalBuilder"/>).
/// </param>
public sealed record DiagnoseRunResult(
    Diagnosis Diagnosis,
    IReadOnlyList<FailProposal> Proposals,
    IReadOnlyList<string> EnvironmentGuidance,
    IReadOnlyList<SpecEditProposal> SpecEditProposals);

/// <summary>
/// Outcome of <see cref="DiagnoseRunOrchestrator.DiagnoseAsync"/> — same error taxonomy as
/// <see cref="ExplainRunOutcome"/>, with a diagnosed result that also carries proposals.
/// </summary>
public abstract record DiagnoseRunOutcome
{
    private DiagnoseRunOutcome()
    {
    }

    /// <summary>Events file was read and diagnosed; see <see cref="DiagnoseRunResult"/>.</summary>
    public sealed record Diagnosed(DiagnoseRunResult Result) : DiagnoseRunOutcome;

    /// <summary><c>eventsPath</c> was omitted and no run has completed this session yet.</summary>
    public sealed record NoRunToExplain(string Message) : DiagnoseRunOutcome;

    /// <summary>The resolved path is a UNC/network location.</summary>
    public sealed record InvalidPath(string Message) : DiagnoseRunOutcome;

    /// <summary>The resolved path does not exist.</summary>
    public sealed record EventsFileNotFound(string Message) : DiagnoseRunOutcome;

    /// <summary>The resolved path exists but could not be read.</summary>
    public sealed record EventsFileUnreadable(string Message) : DiagnoseRunOutcome;

    /// <summary>The file was read but contained no recognisable vouchfx events.</summary>
    public sealed record NoRecognisableEvents(string Message) : DiagnoseRunOutcome;
}
