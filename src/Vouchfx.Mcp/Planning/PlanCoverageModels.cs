// Vouchfx.Mcp.Planning — PlanCoverageModels (Spec D / M3 Planner, REQ-012).
//
// This server never references any engine assembly (see VouchfxCliSuiteRunner's and
// SuiteEventParser's remarks for the same rule elsewhere in this codebase) — every record below is
// this server's OWN DTO, deserialised from the pinned engine's `vouchfx plan --json` stdout, mirroring
// (never importing) the frozen v1 wire shape documented in the engine repo's
// Vouchfx.Engine.Planning.Report.PlanReportDocument / PlanFinding (planner-coverage-and-gap-report
// REQ-011: golden-file-frozen, additive-only). Property names below are pinned via
// [JsonPropertyName] to that exact camelCase wire shape rather than relying on case-insensitive
// matching alone, mirroring SuiteEventParser.RunEvent's own belt-and-braces convention.

using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Planning;

/// <summary>
/// The <c>plan_coverage</c> tool's successful payload: the pinned engine's schema-versioned
/// coverage-and-gap report document, relayed verbatim (never re-derived or filtered) from
/// <c>vouchfx plan --json</c>.
/// </summary>
/// <param name="SchemaVersion">The plan-report wire-schema version the pinned engine stamped (REQ-011).</param>
/// <param name="EngineVersion">The engine's own version stamp, or <see langword="null"/> when the engine did not supply one.</param>
/// <param name="Thresholds">The EFFECTIVE history-health thresholds used for this analysis (defaults or caller overrides).</param>
/// <param name="Inventory">The REQ-003 inventory section: suites, services, dependencies, step types, and history-shape counts.</param>
/// <param name="Findings">
/// Every coverage gap, vocabulary gap, history-health, and suite-identity-ambiguity finding, in the
/// engine's own deterministic order (EDGE-005). Every REQ-004/REQ-005 gap finding carries the
/// REQ-007 hand-off hints (<see cref="PlanCoverageFinding.SuggestedTypes"/>,
/// <see cref="PlanCoverageFinding.SuggestedStepId"/>) a host feeds into <c>scaffold_suite</c>'s own
/// <c>steps[].type</c>/<c>steps[].id</c> UNCHANGED — no re-derivation needed.
/// </param>
public sealed record PlanCoverageResult(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("engineVersion")] string? EngineVersion,
    [property: JsonPropertyName("thresholds")] PlanCoverageThresholds Thresholds,
    [property: JsonPropertyName("inventory")] PlanCoverageInventory Inventory,
    [property: JsonPropertyName("findings")] IReadOnlyList<PlanCoverageFinding> Findings);

/// <summary>The effective history-health thresholds used for one analysis (REQ-006), echoed on the report.</summary>
/// <param name="StaleDays">A step is stale when last observed more than this many days before the newest analysed event.</param>
/// <param name="FlakyMinRuns">A step is flaky when it shows both Pass and Fail across at least this many distinct runs.</param>
/// <param name="FragileMinEnvErrors">A step is fragile when it shows at least this many EnvironmentError outcomes.</param>
/// <param name="InconclusiveMin">A step is inconclusive-prone when it shows at least this many Inconclusive outcomes.</param>
public sealed record PlanCoverageThresholds(
    [property: JsonPropertyName("staleDays")] int StaleDays,
    [property: JsonPropertyName("flakyMinRuns")] int FlakyMinRuns,
    [property: JsonPropertyName("fragileMinEnvErrors")] int FragileMinEnvErrors,
    [property: JsonPropertyName("inconclusiveMin")] int InconclusiveMin);

/// <summary>The REQ-003 inventory section: structural counts and names derived from the declared suites and analysed history.</summary>
/// <param name="Suites">Every discovered suite (path and scenario identity).</param>
/// <param name="Services">The distinct service names declared across every suite's <c>environment.services</c> block.</param>
/// <param name="Dependencies">Every dependency declared across every suite's <c>environment.dependencies</c> block, one entry per (suite, name, type).</param>
/// <param name="StepTypes">The distinct dotted step types declared across every suite.</param>
/// <param name="RunCount">The number of distinct scenario executions (distinct <c>runId</c>s) observed in the analysed history.</param>
/// <param name="FirstEventTs">The earliest event timestamp in the analysed history, or <see langword="null"/> when empty/absent.</param>
/// <param name="LastEventTs">The latest event timestamp in the analysed history — REQ-006's "now" — or <see langword="null"/> when empty/absent.</param>
/// <param name="SkippedEventLines">The count of event-history lines that were not parseable or not recognisable (EDGE-004).</param>
/// <param name="UnmatchedObservations">The count of history observations that could not be attributed to any currently-declared suite (EDGE-007).</param>
/// <param name="UnanalysableSuites">Suites discovered under the analysed path that failed to parse (EDGE-003), each with its captured error.</param>
/// <param name="UnmappableDependencies">Declared dependencies whose kind has no REQ-005 candidate step type (REQ-007) — never a hint-less gap finding.</param>
public sealed record PlanCoverageInventory(
    [property: JsonPropertyName("suites")] IReadOnlyList<PlanCoverageSuiteEntry> Suites,
    [property: JsonPropertyName("services")] IReadOnlyList<string> Services,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<PlanCoverageDependencyEntry> Dependencies,
    [property: JsonPropertyName("stepTypes")] IReadOnlyList<string> StepTypes,
    [property: JsonPropertyName("runCount")] int RunCount,
    [property: JsonPropertyName("firstEventTs")] DateTimeOffset? FirstEventTs,
    [property: JsonPropertyName("lastEventTs")] DateTimeOffset? LastEventTs,
    [property: JsonPropertyName("skippedEventLines")] int SkippedEventLines,
    [property: JsonPropertyName("unmatchedObservations")] int UnmatchedObservations,
    [property: JsonPropertyName("unanalysableSuites")] IReadOnlyList<PlanCoverageUnanalysableSuite> UnanalysableSuites,
    [property: JsonPropertyName("unmappableDependencies")] IReadOnlyList<PlanCoverageUnmappableDependency> UnmappableDependencies);

/// <summary>A single discovered suite's structural identity.</summary>
/// <param name="Path">The suite's path relative to the analysed root, <c>/</c>-separated.</param>
/// <param name="ScenarioId">The suite's derived scenario identity — the sole correlation key against the event history (REQ-008).</param>
/// <param name="Name">The suite's <c>metadata.name</c> verbatim, or <see langword="null"/> when absent.</param>
/// <param name="StepCount">The number of steps declared in this suite.</param>
public sealed record PlanCoverageSuiteEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("scenarioId")] string ScenarioId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("stepCount")] int StepCount);

/// <summary>A single declared dependency's name and kind, scoped to its declaring suite.</summary>
/// <param name="Name">The dependency's logical name (the map key under <c>environment.dependencies</c>).</param>
/// <param name="Type">The dependency's declared <c>type</c> token, e.g. <c>postgres</c>, <c>kafka</c>.</param>
/// <param name="Suite">The relative path of the suite that declares this dependency — never <see langword="null"/>.</param>
public sealed record PlanCoverageDependencyEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("suite")] string Suite);

/// <summary>A discovered suite that failed to parse (EDGE-003).</summary>
/// <param name="Path">The suite's path relative to the analysed root, <c>/</c>-separated.</param>
/// <param name="Error">The captured parse / AST-build error message.</param>
public sealed record PlanCoverageUnanalysableSuite(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("error")] string Error);

/// <summary>A declared dependency whose kind has no REQ-005 candidate step type (REQ-007).</summary>
/// <param name="Name">The dependency's logical name.</param>
/// <param name="Type">The dependency's declared <c>type</c> token.</param>
/// <param name="Reason">A structural, human-readable explanation of why the kind is unmappable.</param>
/// <param name="Suite">The relative path of the suite that declares this dependency — never <see langword="null"/>.</param>
public sealed record PlanCoverageUnmappableDependency(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("suite")] string Suite);

/// <summary>
/// A single coverage gap, vocabulary gap, history-health, or suite-identity-ambiguity finding
/// (frozen v1 shape — see this file's header remarks).
/// </summary>
/// <param name="Kind">The finding-kind discriminator (e.g. <c>dependency-not-asserted</c>, <c>step-flaky</c>).</param>
/// <param name="Suite">The relative path of the suite this finding concerns, or <see langword="null"/> when not suite-scoped.</param>
/// <param name="StepId">The step id this finding concerns, or <see langword="null"/> when not step-scoped.</param>
/// <param name="Target">The service or dependency NAME this finding concerns, or <see langword="null"/>.</param>
/// <param name="TargetKind"><c>"service"</c> or <c>"dependency"</c> when <see cref="Target"/> is set; otherwise <see langword="null"/>.</param>
/// <param name="SuggestedTypes">
/// REQ-007 hand-off hint: one or more dotted step types, present in the pinned engine's current
/// registration, that would close this gap. Feeds <c>scaffold_suite</c>'s own <c>steps[].type</c>
/// UNCHANGED (take <c>SuggestedTypes[0]</c> — the first, ordinal-sorted candidate). Empty for
/// finding kinds that are not gaps.
/// </param>
/// <param name="SuggestedStepId">
/// REQ-007 hand-off hint: a deterministic, YAML-legal suggested step id. Feeds <c>scaffold_suite</c>'s
/// own <c>steps[].id</c> UNCHANGED. <see langword="null"/> for finding kinds that are not gaps.
/// </param>
/// <param name="Ambiguous">
/// REQ-008 / EDGE-002 marker: <see langword="true"/> when this finding's suite identity could not be
/// resolved unambiguously against the event history.
/// </param>
/// <param name="AmbiguityReason"><c>"scenario-id-collision"</c> or <c>"history-references-missing-file"</c> when <see cref="Ambiguous"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
/// <param name="History">REQ-006 evidence for a history-health finding. <see langword="null"/> for gap and ambiguity finding kinds.</param>
/// <param name="Detail">
/// A short, human-readable explanation composed only from structural facts (suite paths, step ids,
/// step types, service/dependency names, verdict counts, timestamps, threshold numbers) — never
/// from an observation payload, a captured value, or an environment value (EDGE-006).
/// </param>
/// <param name="RelatedSuites">
/// The relative paths of every OTHER suite this finding's ambiguity concerns. Empty for every other
/// finding kind.
/// </param>
public sealed record PlanCoverageFinding(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("suite")] string? Suite,
    [property: JsonPropertyName("stepId")] string? StepId,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("targetKind")] string? TargetKind,
    [property: JsonPropertyName("suggestedTypes")] IReadOnlyList<string> SuggestedTypes,
    [property: JsonPropertyName("suggestedStepId")] string? SuggestedStepId,
    [property: JsonPropertyName("ambiguous")] bool Ambiguous,
    [property: JsonPropertyName("ambiguityReason")] string? AmbiguityReason,
    [property: JsonPropertyName("history")] PlanCoverageStepHistory? History,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("relatedSuites")] IReadOnlyList<string> RelatedSuites);

/// <summary>REQ-006 evidence attached to a history-health finding: the raw <c>step-completed</c> tallies that justified the classification.</summary>
/// <param name="PassCount">The number of <c>Pass</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="FailCount">The number of <c>Fail</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="EnvErrorCount">The number of <c>EnvironmentError</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="InconclusiveCount">The number of <c>Inconclusive</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="DistinctRuns">The number of distinct <c>runId</c>s this step was observed under.</param>
/// <param name="LastObserved">The timestamp of the most recent <c>step-completed</c> event for this step.</param>
/// <param name="AgeDays">The whole number of days between <see cref="LastObserved"/> and the analysed history's newest event, or <see langword="null"/>.</param>
public sealed record PlanCoverageStepHistory(
    [property: JsonPropertyName("passCount")] int PassCount,
    [property: JsonPropertyName("failCount")] int FailCount,
    [property: JsonPropertyName("envErrorCount")] int EnvErrorCount,
    [property: JsonPropertyName("inconclusiveCount")] int InconclusiveCount,
    [property: JsonPropertyName("distinctRuns")] int DistinctRuns,
    [property: JsonPropertyName("lastObserved")] DateTimeOffset? LastObserved,
    [property: JsonPropertyName("ageDays")] int? AgeDays);

/// <summary>Neutral outcome of <see cref="PlanCoverageOrchestrator.PlanAsync"/>.</summary>
public abstract record PlanCoverageOutcome
{
    private PlanCoverageOutcome()
    {
    }

    /// <summary>The pinned engine produced a coverage-and-gap report (successfully — regardless of how many gaps it contains; gaps are data, never an error).</summary>
    public sealed record Completed(PlanCoverageResult Result) : PlanCoverageOutcome;

    /// <summary>
    /// Caller args failed local validation before any CLI spawn, OR the pinned CLI itself rejected
    /// the suite path / thresholds as a usage error (<c>vouchfx plan</c> exit code 2 — a bad/missing
    /// suite path, an empty suite folder, or an out-of-range threshold).
    /// </summary>
    public sealed record InvalidArgument(string Message) : PlanCoverageOutcome;

    /// <summary>Pinned CLI missing, mismatched, unparseable, or not launchable (or does not implement the M3 Planner's <c>plan</c> subcommand).</summary>
    public sealed record CliUnavailable(string Message) : PlanCoverageOutcome;

    /// <summary>
    /// The CLI ran but the analysis could not be completed — incomplete catalogue metadata
    /// (<c>vouchfx plan</c> exit code 3), an unrecognised non-zero exit, empty stdout on a
    /// nominally successful run, or stdout that could not be parsed as the plan report document.
    /// </summary>
    public sealed record PlanFailed(string Message) : PlanCoverageOutcome;
}
