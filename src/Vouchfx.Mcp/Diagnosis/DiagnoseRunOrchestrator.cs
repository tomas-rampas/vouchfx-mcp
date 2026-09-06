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

        // STAGE 2 — bodies elided, identities kept. The two proposal lists shed the SAME KIND of
        // detail at the SAME boundary: a FailProposal loses its patch, a SpecEditProposal loses its
        // suggestedEdit, and both keep a (capped) rationale so a reader still knows which step the
        // advice was about and roughly why. US-S4-03 shipped this stage passing specEditProposals
        // through untouched as an explicit stopgap; US-S4-04 formalises it, which is what makes the
        // ladder symmetric rather than "the old list shrinks and the new one does not".
        var bodiesElided = new DiagnoseRunResult(
            diagnosis,
            proposals.Select(p => new FailProposal(
                p.StepId,
                Cap(p.Rationale, MaxElidedRationaleChars),
                "# (patch omitted to fit the diagnose_run response budget; see events file)")
            ).ToList(),
            guidance,
            specEditProposals.Select(p => new SpecEditProposal(
                p.StepId,
                p.Scope,
                Cap(p.Rationale, MaxElidedRationaleChars),
                "# (suggested edit omitted to fit the diagnose_run response budget; see events file)")
            ).ToList());

        if (SerialisedByteCount(bodiesElided) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return bodiesElided;
        }

        // STAGE 3 — rationales elided too; only identities (and, for a spec edit, its SCOPE) survive.
        // The scope is kept deliberately: it is four characters of closed vocabulary, and it is the
        // one field a host can still act on — "there was a capture problem on this step" remains
        // useful when the text explaining it does not fit.
        var identitiesOnly = new DiagnoseRunResult(
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
            // DEDUPLICATED by (stepId, scope) at this stage, and only at this stage. Once the
            // rationale is a fixed notice and the body is "# (omitted)", two proposals sharing an id
            // and a scope are BYTE-IDENTICAL records — and the environment-scoped ones all share a
            // null id, so a run with several environment errors emits several copies of exactly the
            // same object at the moment bytes are scarcest. Nothing is lost: what distinguished them
            // was the text this stage has already elided. Stages 1 and 2 keep every entry, because
            // there the rationales still differ.
            specEditProposals
                .Select(p => new SpecEditProposal(
                    p.StepId,
                    p.Scope,
                    "Spec-edit proposal truncated for response budget; see events file.",
                    "# (omitted)"))
                .DistinctBy(p => (p.StepId, p.Scope))
                .ToList());

        if (SerialisedByteCount(identitiesOnly) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return identitiesOnly;
        }

        // STAGE 4 — diagnosis alone with empty extras: BOTH proposal lists and the guidance,
        // together. Never one populated while the others are dropped: a response carrying spec-edit
        // proposals but no Fail proposals would read as "there were no failing steps", which at this
        // stage is a statement about the BUDGET, not the run. This is also the stage that removes the
        // raw step IDS — SpecEditProposal.StepId is deliberately uncapped (it is the host's
        // correlation key), so ten proposals can carry ~20 KB that no amount of BODY elision above
        // touches. Emptying the lists is what bounds that, which is why the emptying is a stage
        // rather than a detail.
        var withoutProposals = new DiagnoseRunResult(diagnosis, [], [], []);

        // ...and it is MEASURED, like the three stages above it (US-S4-04's own acceptance
        // criterion; US-S4-03 left this bounded by arithmetic alone). The arithmetic said: a
        // diagnosis is <= EffectiveDiagnosisBudgetBytes (32,768) because ExplainRunOrchestrator
        // tiered it there, and this shape adds a fixed 77-byte wrapper — `{"diagnosis":` (13),
        // `,"proposals":[]` (15), `,"environmentGuidance":[]` (25), `,"specEditProposals":[]` (23),
        // `}` (1) — so the result can reach 32,845 B and exceed the budget by up to 77 B. That
        // window, [32,692..32,768] on the diagnosis, is genuinely REACHABLE: BuildDiagnosis returns
        // the first tier measuring <= 32,768 and only falls to its emergency shape when even the
        // floor tier exceeds that, so a tier landing inside the window is returned intact and
        // overflows here.
        if (SerialisedByteCount(withoutProposals) <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes)
        {
            return withoutProposals;
        }

        // The genuine floor: explain_run's OWN last-resort shape, which carries no per-item
        // collection at all (~2 KB measured) plus the same 77-byte wrapper. Reusing it rather than
        // inventing a fifth shape here matters for the reason the whole sprint keeps repeating:
        // there must not be two different "we could not fit this" answers for a host to tell apart.
        return new DiagnoseRunResult(
            ExplainRunOrchestrator.BuildEmergencyMinimalDiagnosis(diagnosis), [], [], []);
    }

    /// <summary>
    /// Characters kept of a proposal's rationale at stage 2 — the same bound for both proposal
    /// kinds, so neither list looks more important than the other after eliding.
    /// </summary>
    private const int MaxElidedRationaleChars = 120;

    private static int SerialisedByteCount(DiagnoseRunResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

    private static string Cap(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];
}
