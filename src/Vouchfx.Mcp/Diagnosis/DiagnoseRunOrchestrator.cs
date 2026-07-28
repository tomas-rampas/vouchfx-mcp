using System.Text.Json;

namespace Vouchfx.Mcp.Diagnosis;

/// <summary>
/// Spec C / M2 Healer orchestration for <c>diagnose_run</c>: reuses
/// <see cref="ExplainRunOrchestrator.ExplainAsync"/> for taxonomy-faithful diagnosis, then attaches
/// Fail-only review proposals and EnvironmentError infrastructure guidance. Pure read + parse +
/// template assembly — never re-runs a suite, never writes the caller's suite file, never calls a
/// model API, never emits engine <c>healer-suggestion</c> events.
/// </summary>
/// <remarks>
/// <para>
/// Path resolution, safety, bounded read, and the four-outcome taxonomy are entirely owned by
/// <see cref="ExplainRunOrchestrator"/> — this type only maps that outcome and builds proposals.
/// </para>
/// <para>
/// The response budget reuses <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/> (64&#160;KB
/// wire envelope). When the full diagnose payload exceeds the effective bare budget, proposals and
/// guidance are progressively emptied rather than inventing a second diagnosis path.
/// </para>
/// </remarks>
public sealed class DiagnoseRunOrchestrator
{
    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    private readonly ExplainRunOrchestrator _explainRunOrchestrator;

    public DiagnoseRunOrchestrator(ExplainRunOrchestrator explainRunOrchestrator)
    {
        ArgumentNullException.ThrowIfNull(explainRunOrchestrator);
        _explainRunOrchestrator = explainRunOrchestrator;
    }

    /// <summary>
    /// Resolves, reads, and diagnoses an events file, returning Fail patch proposals when evidence
    /// supports them. See this type's remarks.
    /// </summary>
    /// <param name="eventsPath">
    /// Path to the events file. <see langword="null"/> or whitespace defaults to the last run this
    /// session (same EDGE default as <c>explain_run</c>).
    /// </param>
    public async Task<DiagnoseRunOutcome> DiagnoseAsync(string? eventsPath, CancellationToken cancellationToken)
    {
        var explainOutcome = await _explainRunOrchestrator.ExplainAsync(eventsPath, cancellationToken)
            .ConfigureAwait(false);

        return explainOutcome switch
        {
            ExplainRunOutcome.Diagnosed diagnosed =>
                new DiagnoseRunOutcome.Diagnosed(BuildResult(diagnosed.Diagnosis)),
            ExplainRunOutcome.NoRunToExplain noRun =>
                new DiagnoseRunOutcome.NoRunToExplain(noRun.Message),
            ExplainRunOutcome.InvalidPath invalidPath =>
                new DiagnoseRunOutcome.InvalidPath(invalidPath.Message),
            ExplainRunOutcome.EventsFileNotFound notFound =>
                new DiagnoseRunOutcome.EventsFileNotFound(notFound.Message),
            ExplainRunOutcome.EventsFileUnreadable unreadable =>
                new DiagnoseRunOutcome.EventsFileUnreadable(unreadable.Message),
            ExplainRunOutcome.NoRecognisableEvents noEvents =>
                new DiagnoseRunOutcome.NoRecognisableEvents(noEvents.Message),
            _ =>
                new DiagnoseRunOutcome.NoRecognisableEvents(
                    "diagnose_run could not classify the explain_run outcome."),
        };
    }

    private static DiagnoseRunResult BuildResult(Diagnosis diagnosis)
    {
        var proposals = FailProposalBuilder.BuildProposals(diagnosis);
        var guidance = FailProposalBuilder.BuildEnvironmentGuidance(diagnosis);
        var candidate = new DiagnoseRunResult(diagnosis, proposals, guidance);

        // Measure the bare payload against half of MaxDiagnosisResponseBytes (same wire-envelope
        // doubling as explain_run: StructuredToolResult.Success serialises twice).
        if (SerialisedByteCount(candidate) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return candidate;
        }

        // Drop observation-heavy proposal patches first, then guidance, keeping the diagnosis.
        var withoutPatches = new DiagnoseRunResult(
            diagnosis,
            proposals.Select(p => new FailProposal(
                p.StepId,
                Cap(p.Rationale, 120),
                "# (patch omitted to fit the diagnose_run response budget; see events file)")
            ).ToList(),
            guidance);

        if (SerialisedByteCount(withoutPatches) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return withoutPatches;
        }

        var proposalsOnlyIds = new DiagnoseRunResult(
            diagnosis,
            proposals.Select(p => new FailProposal(
                p.StepId,
                "Fail proposal truncated for response budget; see events file.",
                "# (omitted)")).ToList(),
            // Guidance may be EnvironmentError infrastructure text or Inconclusive non-patch
            // advice — keep the truncation notice verdict-neutral (no hard-coded environmentErrors).
            guidance.Count > 0
                ? ["Guidance truncated for response budget; see events file."]
                : []);

        if (SerialisedByteCount(proposalsOnlyIds) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return proposalsOnlyIds;
        }

        // Last resort: diagnosis alone (already size-capped by explain_run) with empty extras.
        return new DiagnoseRunResult(diagnosis, [], []);
    }

    private static int SerialisedByteCount(DiagnoseRunResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

    private static string Cap(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];
}
