namespace Vouchfx.Mcp.Diagnosis;

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
public sealed record DiagnoseRunResult(
    Diagnosis Diagnosis,
    IReadOnlyList<FailProposal> Proposals,
    IReadOnlyList<string> EnvironmentGuidance);

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
