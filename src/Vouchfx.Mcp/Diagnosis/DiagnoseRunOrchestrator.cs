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

        // US-S4-03's SECOND list (plan D2's superset). The two builders are disjoint by
        // construction — Fail steps reach only the first, EnvironmentError/Inconclusive material only
        // the second — so neither's behaviour changes by the other's existence.
        var specEditProposals = SpecEditProposalBuilder.BuildProposals(diagnosis);
        var candidate = new DiagnoseRunResult(diagnosis, proposals, guidance, specEditProposals);

        // Measure the bare payload (diagnosis + proposals + guidance combined) against
        // ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes — half of MaxDiagnosisResponseBytes.
        // This is a CONSERVATIVE GATE, not an exact model of the wire envelope: it was originally
        // justified as "StructuredToolResult.Success serialises twice, so halving covers the
        // doubling", but that claim is false and was corrected by US-S1-02's measurement on
        // explain_run — the real envelope-to-bare multiplier is 2.213, not 2, because the duplicated
        // text Content block is a JSON-escaped STRING (every quote and backslash re-escaped) rather
        // than a second verbatim copy. See ExplainRunOrchestrator.MaxDiagnosisResponseBytes' remarks
        // for that measurement, and
        // DiagnoseRunOrchestratorTests.DiagnoseAsync_MaximalStageZeroInput_FitsTheDiagnoseBudgetButItsEnvelopeExceedsTheCap
        // for THIS type's own measured baseline (candidate 32,235 B, envelope 67,057 B, multiplier
        // 2.080 on that input) — halving here still lets a real envelope exceed the public 64 KB cap;
        // it narrows the miss, it does not close it. Sprint 4 owns the actual fix.
        if (SerialisedByteCount(candidate) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return candidate;
        }

        // Drop observation-heavy proposal patches first, then guidance, keeping the diagnosis.
        //
        // US-S4-03 STOPGAP, deliberately minimal: stages 2 and 3 below pass specEditProposals
        // through UNSHRUNK, and only stage 4 empties them (alongside proposals and guidance, never
        // one without the others). Eliding suggestedEdit bodies at stage 2 and rationales at stage 3
        // — so the new list sheds detail at the SAME boundaries as the existing one — is US-S4-04's
        // own acceptance criterion, and re-engineering these stages here would collide with it. What
        // this stopgap guarantees is the invariant that must not go red in the meantime: the FINAL
        // stage's measured size still fits the budget, because it carries no proposals of either
        // kind.
        var withoutPatches = new DiagnoseRunResult(
            diagnosis,
            proposals.Select(p => new FailProposal(
                p.StepId,
                Cap(p.Rationale, 120),
                "# (patch omitted to fit the diagnose_run response budget; see events file)")
            ).ToList(),
            guidance,
            specEditProposals);

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
                : [],
            specEditProposals);

        if (SerialisedByteCount(proposalsOnlyIds) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return proposalsOnlyIds;
        }

        // Last resort: diagnosis alone with empty extras — BOTH proposal lists and the guidance,
        // together. Never one populated while the others are dropped: a response carrying spec-edit
        // proposals but no Fail proposals would read as "there were no failing steps", which at this
        // stage is a statement about the BUDGET, not the run.
        //
        // HONEST BOUND, not a measured one (a review corrected an earlier comment that claimed this
        // stage's "measured size" fits — nothing measures it). What holds: the diagnosis is already
        // within EffectiveDiagnosisBudgetBytes (32,768) because ExplainRunOrchestrator tiered it
        // there, and this shape adds a FIXED 77-byte wrapper — `{"diagnosis":` (13) plus
        // `,"proposals":[]` (15), `,"environmentGuidance":[]` (25), `,"specEditProposals":[]` (23)
        // and the closing brace (1). So the result is bounded by 32,845 B, which EXCEEDS the budget
        // by up to 77 B at the margin: a diagnosis that exactly fills its own budget produces a
        // final-stage result 77 B over this one.
        //
        // That margin is REACHABLE, and an earlier version of this comment wrongly said otherwise by
        // claiming such a diagnosis would already have become the emergency shape. It would not:
        // BuildDiagnosis RETURNS the first tier measuring <= 32,768 B, and only falls through to the
        // emergency shape when even the floor tier EXCEEDS that. So any of the three tiers can land
        // in the 77-byte window [32,692..32,768] and overflow the wrapper here — which is precisely
        // why US-S4-04, which owns the ladder, must MEASURE this stage like the three above it and
        // fall back to the emergency diagnosis shape when it does not fit.
        return new DiagnoseRunResult(diagnosis, [], [], []);
    }

    private static int SerialisedByteCount(DiagnoseRunResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

    private static string Cap(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];
}
