using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// Parses the vouchfx engine's JSON Lines event stream (blueprint §14.4) into a
/// <see cref="SuiteRunSummary"/> — REQ-006's structured, taxonomy-faithful result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffered, not tailable — the critical finding this design is built around.</b> The engine
/// writes its ENTIRE event stream to the <c>--events</c> file exactly once, after the whole scenario
/// loop completes (<c>ScenarioRunner.RunSuiteAsync</c>'s <c>FileReportWriter.WriteFileReports</c>
/// call happens once, at the end) — confirmed both from source and empirically (a real run's events
/// file did not exist until seconds before the process itself exited). This method is therefore
/// always called AFTER <see cref="ISuiteRunner.RunAsync"/> returns
/// <see cref="RunTermination.CompletedNormally"/>, over the file's complete, final content — never
/// incrementally while a run is in flight. <see cref="VouchfxCliSuiteRunner"/>'s relayed stdout/stderr
/// lines are the only genuinely live signal this server can offer; this parse instead produces a
/// rich, ACCURATE post-hoc narration (via <paramref name="onNarration"/> below) plus the final
/// structured result.
/// </para>
/// <para>
/// <b>Own, independent DTOs, not the engine's typed records.</b> This server never references any
/// engine assembly (see <see cref="VouchfxCliSuiteRunner"/>'s remarks) — <see cref="RunEvent"/> is a
/// single flat record covering every field this parser needs across every event type
/// (<c>scenario-completed</c>, <c>step-attempt</c>, <c>step-completed</c>, <c>environment-error</c>),
/// deserialised with default <see cref="System.Text.Json"/> behaviour, which already ignores unknown
/// JSON properties — satisfying the §14 "renderers tolerate unknown fields" contract for free, with
/// no <c>[JsonExtensionData]</c> needed. A line that fails to parse as JSON at all, or parses but
/// carries no <c>type</c> this method recognises, is skipped rather than treated as an error: a
/// single malformed or forward-incompatible line must never make an otherwise-good run's result
/// unusable.
/// </para>
/// <para>
/// <b>RETRY attempt counts</b> are derived by tracking the highest <c>attempt</c> number seen per
/// <c>stepId</c> across <c>step-attempt</c> events (attempt is documented as a monotonic, one-based
/// counter — §14.4), not by counting lines, so a duplicate or out-of-order emission cannot inflate
/// the count.
/// </para>
/// <para>
/// <b>Sanitisation:</b> every caller-influenced string that reaches <see cref="StepOutcome"/>,
/// <see cref="EnvironmentErrorSummary"/>, or a narration line — step ids, resource names, error
/// details — is a step id or engine-produced diagnostic that ultimately traces back to the suite
/// YAML the <c>run_suite</c> caller supplied (untrusted input, same threat model as every other
/// caller-supplied value in this codebase) and is therefore run through
/// <see cref="TextSanitiser.SanitiseForDisplay"/> here, at the point it is first extracted.
/// </para>
/// </remarks>
public static class SuiteEventParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses <paramref name="eventsFileContent"/> (the complete, final content of a run's
    /// <c>--events</c> file — see this type's remarks on why it is never a partial read) into a
    /// <see cref="SuiteRunSummary"/>.
    /// </summary>
    /// <param name="eventsFileContent">
    /// The events file's raw text. An empty or whitespace-only string (e.g. the CLI crashed before
    /// producing any output at all) yields a summary with no aggregate verdict and no steps — never
    /// throws.
    /// </param>
    /// <param name="onNarration">
    /// Invoked once per recognised event, in file order, with a short human-readable description
    /// (e.g. <c>"step check-health PASS (142ms)"</c>) — <see cref="RunSuiteOrchestrator"/> relays
    /// these as progress notifications. This is a NARRATION of an already-completed run, not a live
    /// feed (see this type's remarks) — callers must not present it as real-time progress.
    /// </param>
    public static SuiteRunSummary Parse(string eventsFileContent, Action<string>? onNarration = null)
    {
        ArgumentNullException.ThrowIfNull(eventsFileContent);

        var steps = new List<StepOutcome>();
        var environmentErrors = new List<EnvironmentErrorSummary>();
        var maxAttemptByStepId = new Dictionary<string, int>(StringComparer.Ordinal);
        RunVerdict? aggregateVerdict = null;

        foreach (var rawLine in eventsFileContent.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            RunEvent? runEvent;
            try
            {
                runEvent = JsonSerializer.Deserialize<RunEvent>(line, Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (runEvent is null)
            {
                continue;
            }

            switch (runEvent.Type)
            {
                case "step-attempt":
                    HandleStepAttempt(runEvent, maxAttemptByStepId);
                    if (onNarration is not null && runEvent.StepId is { } attemptStepId)
                    {
                        onNarration($"step {TextSanitiser.SanitiseForDisplay(attemptStepId)} attempt {runEvent.Attempt ?? 1}");
                    }

                    break;

                case "step-completed":
                    var stepOutcome = BuildStepOutcome(runEvent, maxAttemptByStepId);
                    if (stepOutcome is not null)
                    {
                        steps.Add(stepOutcome);
                        onNarration?.Invoke(
                            $"step {stepOutcome.StepId} {stepOutcome.Verdict} ({stepOutcome.DurationMs}ms)");
                    }

                    break;

                case "scenario-completed":
                    if (RunVerdictExtensions.ParseWireToken(runEvent.Verdict) is { } scenarioVerdict)
                    {
                        aggregateVerdict = aggregateVerdict is { } current
                            ? RunVerdictExtensions.Elevate(current, scenarioVerdict)
                            : scenarioVerdict;

                        var scenarioLabel = runEvent.ScenarioId is { } scenarioId
                            ? TextSanitiser.SanitiseForDisplay(scenarioId)
                            : "(scenario)";
                        onNarration?.Invoke($"scenario {scenarioLabel} {scenarioVerdict}");
                    }

                    break;

                case "environment-error":
                    var environmentError = BuildEnvironmentErrorSummary(runEvent);
                    environmentErrors.Add(environmentError);
                    onNarration?.Invoke(
                        $"environment error: {environmentError.ErrorKind} on '{environmentError.ResourceName}'");
                    break;
            }
        }

        return new SuiteRunSummary(aggregateVerdict, steps, environmentErrors);
    }

    private static void HandleStepAttempt(RunEvent runEvent, Dictionary<string, int> maxAttemptByStepId)
    {
        if (runEvent.StepId is not { } stepId || runEvent.Attempt is not { } attempt)
        {
            return;
        }

        if (!maxAttemptByStepId.TryGetValue(stepId, out var currentMax) || attempt > currentMax)
        {
            maxAttemptByStepId[stepId] = attempt;
        }
    }

    private static StepOutcome? BuildStepOutcome(RunEvent runEvent, Dictionary<string, int> maxAttemptByStepId)
    {
        if (runEvent.StepId is not { } rawStepId)
        {
            return null;
        }

        var verdict = RunVerdictExtensions.ParseWireToken(runEvent.Verdict);
        if (verdict is null)
        {
            return null;
        }

        var stepId = TextSanitiser.SanitiseForDisplay(rawStepId);
        var attemptCount = maxAttemptByStepId.TryGetValue(rawStepId, out var max) ? max : 1;

        return new StepOutcome(stepId, verdict.Value.ToString(), runEvent.DurationMs ?? 0, attemptCount);
    }

    private static EnvironmentErrorSummary BuildEnvironmentErrorSummary(RunEvent runEvent)
    {
        var errorKind = runEvent.ErrorKind is { } kind ? TextSanitiser.SanitiseForDisplay(kind) : "Unknown";
        var resourceName = runEvent.ResourceName is { } name ? TextSanitiser.SanitiseForDisplay(name) : "(unknown)";
        var detail = runEvent.Detail is { } detailText ? TextSanitiser.SanitiseForDisplay(detailText) : null;

        return new EnvironmentErrorSummary(errorKind, resourceName, detail);
    }

    /// <summary>
    /// A single flat DTO covering every field this parser needs, across every event type the vouchfx
    /// event-stream contract defines that this server cares about (§14.4). See this type's remarks
    /// for why one flat, lenient shape — not one record per engine event type — is the deliberate
    /// design.
    /// </summary>
    private sealed record RunEvent
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("stepId")]
        public string? StepId { get; init; }

        [JsonPropertyName("scenarioId")]
        public string? ScenarioId { get; init; }

        [JsonPropertyName("attempt")]
        public int? Attempt { get; init; }

        /// <summary>The raw wire token — <c>"PASS"</c>/<c>"FAIL"</c>/<c>"ENV_ERROR"</c>/<c>"INCONCLUSIVE"</c> — parsed via <see cref="RunVerdictExtensions.ParseWireToken"/>.</summary>
        [JsonPropertyName("verdict")]
        public string? Verdict { get; init; }

        [JsonPropertyName("durationMs")]
        public long? DurationMs { get; init; }

        [JsonPropertyName("errorKind")]
        public string? ErrorKind { get; init; }

        [JsonPropertyName("resourceName")]
        public string? ResourceName { get; init; }

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }
    }
}
