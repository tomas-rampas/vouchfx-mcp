namespace Vouchfx.Mcp.Diagnosis;

/// <summary>One RETRY attempt within a step's timeline, as REQ-007's diagnosis presents it.</summary>
/// <param name="Attempt">The one-based attempt counter.</param>
/// <param name="TMs">Elapsed wall-clock time for this attempt, in milliseconds.</param>
/// <param name="Outcome">This attempt's own resolved outcome, when the engine reported one; <see langword="null"/> for a mid-RETRY poll with no outcome yet.</param>
/// <param name="Observation">This attempt's own observation/diff evidence, capped for the response budget (see <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/>); <see langword="null"/> when none was carried or this tier omits it.</param>
public sealed record StepAttemptDiagnosis(int Attempt, long TMs, string? Outcome, string? Observation);

/// <summary>
/// One step the diagnosis calls out by name — every step whose OWN verdict is not <c>Pass</c>
/// (REQ-007: "for Fail, name the failing step(s) and the mismatch"; "for Inconclusive, the
/// timed-out step and its RETRY timeline").
/// </summary>
/// <param name="StepId">The step's own identifier.</param>
/// <param name="Verdict">One of <c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> — never <c>Pass</c> (a passing step is never "notable").</param>
/// <param name="DurationMs">Total wall-clock duration of all attempts combined, in milliseconds.</param>
/// <param name="AttemptCount">How many attempts this step made in total (may exceed <see cref="Attempts"/>'s own count — see <see cref="OmittedAttemptCount"/>).</param>
/// <param name="Observation">The step's own final observation/diff evidence, capped for the response budget; <see langword="null"/> when none was carried or this tier omits it.</param>
/// <param name="Attempts">The RETRY attempt timeline, in order, capped for the response budget.</param>
/// <param name="OmittedAttemptCount">How many earlier attempts were left out of <see cref="Attempts"/> to fit the response budget; <c>0</c> when none were.</param>
public sealed record StepDiagnosis(
    string StepId,
    string Verdict,
    long DurationMs,
    int AttemptCount,
    string? Observation,
    IReadOnlyList<StepAttemptDiagnosis> Attempts,
    int OmittedAttemptCount = 0);

/// <summary>One <c>environment-error</c> event, as REQ-007's diagnosis presents it — always distinct from a step <c>Fail</c>.</summary>
/// <param name="ErrorKind">The <c>OrchestrationErrorKind</c> name the engine reported (e.g. <c>"ImagePull"</c>, <c>"Provision"</c>).</param>
/// <param name="ResourceName">The Aspire resource name the failure concerns.</param>
/// <param name="Detail">A trimmed summary of the underlying failure; <see langword="null"/> when the engine reported none.</param>
public sealed record EnvironmentErrorDiagnosis(string ErrorKind, string ResourceName, string? Detail);

/// <summary>
/// REQ-007's <c>explain_run</c> result: a taxonomy-faithful diagnosis of a (possibly historical)
/// suite run, built PURELY from its events file — no suite is ever re-run to produce this.
/// </summary>
/// <param name="Verdict">One of <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> — <see cref="RunVerdict"/>'s own names.</param>
/// <param name="CategoryMeaning">
/// A short, fixed explanation of what <see cref="Verdict"/>'s CATEGORY means (§12.1) — e.g. that an
/// <c>EnvironmentError</c> is an infrastructure problem and explicitly NOT a test defect. Always
/// present, regardless of verdict.
/// </param>
/// <param name="Summary">A short, readable, one-paragraph summary of the whole diagnosis.</param>
/// <param name="TotalStepCount">How many steps the events file recorded a final outcome for.</param>
/// <param name="PassedStepCount">How many of those steps passed.</param>
/// <param name="NotableSteps">
/// Every step whose own verdict is not <c>Pass</c>, with full evidence (subject to the response
/// budget) — empty for an all-passing run.
/// </param>
/// <param name="OmittedNotableStepCount">
/// How many additional non-passing steps exist beyond <see cref="NotableSteps"/>, omitted to fit the
/// response budget; <c>0</c> when none were.
/// </param>
/// <param name="EnvironmentErrors">
/// Every <c>environment-error</c> event the run recorded, capped for the response budget.
/// </param>
/// <param name="OmittedEnvironmentErrorCount">
/// How many additional <c>environment-error</c> events exist beyond <see cref="EnvironmentErrors"/>,
/// omitted to fit the response budget; <c>0</c> when none were.
/// </param>
/// <param name="EventsFilePath">The events file this diagnosis was built from.</param>
/// <param name="EventsTruncated">
/// <see langword="true"/> when the events file itself exceeded
/// <see cref="Run.EventsFileReader.MaxEventsFileBytes"/> and was only read up to that many bytes — the
/// diagnosis may be based on incomplete source data.
/// </param>
/// <param name="ResponseTruncated">
/// <see langword="true"/> when embedding full evidence for every notable step would have exceeded
/// <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/>, so observations/attempt timelines/
/// step counts were progressively trimmed — the full detail still exists in
/// <see cref="EventsFilePath"/>.
/// </param>
public sealed record Diagnosis(
    string Verdict,
    string CategoryMeaning,
    string Summary,
    int TotalStepCount,
    int PassedStepCount,
    IReadOnlyList<StepDiagnosis> NotableSteps,
    int OmittedNotableStepCount,
    IReadOnlyList<EnvironmentErrorDiagnosis> EnvironmentErrors,
    int OmittedEnvironmentErrorCount,
    string EventsFilePath,
    bool EventsTruncated,
    bool ResponseTruncated);

/// <summary>
/// The outcome of <see cref="ExplainRunOrchestrator.ExplainAsync"/> — a closed discriminated union (a
/// private constructor confines derivation to the cases nested here), mirroring
/// <see cref="Run.RunSuiteOutcome"/>'s own shape: every branch a caller must handle is visible at the
/// type level, not inferred from a message string.
/// </summary>
public abstract record ExplainRunOutcome
{
    private ExplainRunOutcome()
    {
    }

    /// <summary>The events file was read and understood; see <see cref="Diagnosis"/> for the result.</summary>
    public sealed record Diagnosed(Diagnosis Diagnosis) : ExplainRunOutcome;

    /// <summary><c>eventsPath</c> was omitted and no run has completed this session yet.</summary>
    public sealed record NoRunToExplain(string Message) : ExplainRunOutcome;

    /// <summary>The resolved path is a UNC/network location (forced-authentication risk) — rejected before any filesystem call is made against it.</summary>
    public sealed record InvalidPath(string Message) : ExplainRunOutcome;

    /// <summary>The resolved path does not exist.</summary>
    public sealed record EventsFileNotFound(string Message) : ExplainRunOutcome;

    /// <summary>The resolved path exists but could not be read (permissions, a locked file, or another I/O failure).</summary>
    public sealed record EventsFileUnreadable(string Message) : ExplainRunOutcome;

    /// <summary>The file was read successfully but contained no recognisable vouchfx event — empty, or entirely unparseable/garbage content.</summary>
    public sealed record NoRecognisableEvents(string Message) : ExplainRunOutcome;
}
