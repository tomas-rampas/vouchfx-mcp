using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// Parses the vouchfx engine's JSON Lines event stream (blueprint §14.4) into a
/// <see cref="SuiteRunSummary"/> — REQ-006's structured, taxonomy-faithful result, and REQ-007's
/// <c>explain_run</c> diagnosis evidence (attempt timelines, observation/diff text).
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
/// structured result. <c>explain_run</c> (REQ-007) reads a file that is, by construction, always
/// already complete — it never runs anything itself, so this "buffered" property is simply a given
/// there, not something it has to reason about.
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
/// single malformed or forward-incompatible line — or a wholly unknown event TYPE (EDGE-004,
/// additive-frozen v1) — must never make an otherwise-good run's result unusable.
/// </para>
/// <para>
/// <b>RETRY attempt counts AND timelines</b> are derived from <c>step-attempt</c> events (attempt is
/// documented as a monotonic, one-based counter — §14.4): <see cref="StepOutcome.AttemptCount"/> is
/// the highest attempt number seen per <c>stepId</c> (not a line count, so a duplicate or
/// out-of-order emission cannot inflate it), and <see cref="SuiteRunSummary.AttemptsByStepId"/>
/// additionally keeps every individual attempt, in file order, for <c>explain_run</c>'s RETRY
/// timeline (REQ-007) — <c>run_suite</c> itself never reads that dictionary at all.
/// </para>
/// <para>
/// <b>US-S3-06's <c>get_step_timeline</c> reads that SAME dictionary</b>, which is its acceptance
/// criterion rather than an implementation convenience: the story requires the timeline to be
/// "extracted from, not duplicated alongside" <c>explain_run.notableSteps[].attempts</c>. So there is
/// one attempt-parsing implementation, here, and the two tools differ only in what they are willing
/// to DROP from it — <c>explain_run</c> shrinks the list under its response-size tiers, and
/// <c>get_step_timeline</c> shrinks per-attempt TEXT instead, keeping the list. Three fields on
/// <see cref="StepAttempt"/> exist for that second consumer (<see cref="StepAttempt.RawOutcome"/>,
/// <see cref="StepAttempt.Error"/>, <see cref="StepAttempt.At"/>); each documents its own reason, and
/// none changes what the first consumer sees.
/// </para>
/// <para>
/// <b>Observation/diff evidence</b> (REQ-007): a <c>step-completed</c> or <c>step-attempt</c> event's
/// own <c>observation</c> field (arbitrary structured JSON — a diff, a matched count, a raw response
/// excerpt) is captured as sanitised raw JSON text, capped at <see cref="MaxObservationCharsAtParse"/>
/// characters — a defensive bound independent of (tighter than) the whole-file
/// <see cref="EventsFileReader.MaxEventsFileBytes"/> cap, so a single pathological observation blob
/// cannot dominate the parsed summary's own memory footprint even before <c>explain_run</c>'s own
/// response-level capping (<c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c>) is applied.
/// </para>
/// <para>
/// <b>Sanitisation:</b> every caller-influenced string that reaches <see cref="StepOutcome"/>,
/// <see cref="StepAttempt"/>, <see cref="EnvironmentErrorSummary"/>, or a narration line — step ids,
/// resource names, error details, observation text — ultimately traces back to the suite YAML the
/// <c>run_suite</c> caller supplied (untrusted input, same threat model as every other caller-supplied
/// value in this codebase) and is therefore run through <see cref="TextSanitiser.SanitiseForDisplay"/>
/// here, at the point it is first extracted.
/// </para>
/// </remarks>
public static class SuiteEventParser
{
    /// <summary>
    /// Maximum characters kept for a single event's <c>observation</c> field, applied at PARSE time
    /// (before sanitisation can expand it further) — see this type's remarks.
    /// </summary>
    private const int MaxObservationCharsAtParse = 10_000;

    /// <summary>
    /// Maximum characters kept for the shorter, label-shaped free-text fields — a step's own
    /// <c>stepId</c>, and an <c>environment-error</c> event's own
    /// <c>errorKind</c>/<c>resourceName</c>/<c>detail</c> — at PARSE time. Smaller than
    /// <see cref="MaxObservationCharsAtParse"/> because these are meant to be short diagnostic
    /// labels, not structured evidence blobs; still capped independently so a single pathological
    /// event cannot make even ONE field dominate a parsed summary's memory footprint, regardless of
    /// how many such events a caller later chooses to show.
    /// </summary>
    /// <remarks>
    /// A review found <c>stepId</c> was sanitised but NOT length-capped here, unlike every other
    /// caller-influenced field — a single events line with an (e.g.) 200,000-character <c>stepId</c>
    /// would flow uncapped all the way into <c>ExplainRunOrchestrator</c>'s response, defeating its
    /// own 64&#160;KB cap regardless of how carefully that cap's OWN logic was built, since it was
    /// never the source of the oversized content in the first place. <c>stepId</c> is now capped
    /// here for exactly the same reason the other fields already were.
    /// </remarks>
    private const int MaxLabelCharsAtParse = 2_000;

    /// <summary>
    /// Maximum number of lines this method will ever process from one events file — cheap
    /// defence-in-depth against a pathological file of millions of tiny lines (still within the
    /// whole-file <see cref="EventsFileReader.MaxEventsFileBytes"/> byte cap, e.g. one JSON object
    /// per line at a few bytes each) transiently ballooning this parse's own working set. Chosen
    /// generously above any realistic suite's event count — a suite with anywhere near this many
    /// lines would already be an extreme outlier — so this is a genuine backstop, not a bound a
    /// legitimate run is expected to brush up against. Once reached, parsing simply stops and
    /// whatever was already parsed is returned, the same "tolerate and move on" philosophy as a
    /// single malformed line.
    /// </summary>
    private const int MaxLinesProcessed = 2_000_000;

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
        var attemptsByStepId = new Dictionary<string, List<StepAttempt>>(StringComparer.Ordinal);
        RunVerdict? aggregateVerdict = null;

        // A StringReader over the already-materialised string, read one line at a time — deliberately
        // NOT Split('\n'), which would allocate every line's substring into one giant array up front
        // before the loop even starts. Reading lazily bounds this method's OWN peak working set to
        // roughly one line at a time (plus whatever the accumulated summary itself needs), rather
        // than additionally holding the whole file's lines all at once.
        using var reader = new StringReader(eventsFileContent);
        var linesProcessed = 0;
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            if (linesProcessed >= MaxLinesProcessed)
            {
                break;
            }

            linesProcessed++;

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
                    HandleStepAttempt(runEvent, maxAttemptByStepId, attemptsByStepId);
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

        var frozenAttempts = new Dictionary<string, IReadOnlyList<StepAttempt>>(StringComparer.Ordinal);
        foreach (var (stepId, attempts) in attemptsByStepId)
        {
            frozenAttempts[stepId] = attempts;
        }

        return new SuiteRunSummary(aggregateVerdict, steps, environmentErrors, frozenAttempts);
    }

    private static void HandleStepAttempt(
        RunEvent runEvent,
        Dictionary<string, int> maxAttemptByStepId,
        Dictionary<string, List<StepAttempt>> attemptsByStepId)
    {
        if (runEvent.StepId is not { } rawStepId || runEvent.Attempt is not { } attempt)
        {
            return;
        }

        // Capped ONCE, here, and used as the key for BOTH dictionaries below — the IDENTICAL value
        // BuildStepOutcome's own lookup uses (see that method), so an attempt count is never
        // recorded under one key and looked up under another (a review fix — the count dictionary
        // was previously keyed by the RAW, uncapped id, retaining an unbounded-length string as a
        // dictionary KEY even though StepOutcome.StepId itself was already capped: exactly the
        // memory bloat MaxLabelCharsAtParse exists to prevent. Collapsing two distinct raw ids that
        // share a >cap-length prefix to the same capped value is an ALREADY-ACCEPTED consequence of
        // capping — attemptsByStepId already keys this way — so keying maxAttemptByStepId
        // differently would only add inconsistency without avoiding that collision anyway).
        var stepId = SanitiseAndCapLabel(rawStepId);

        if (!maxAttemptByStepId.TryGetValue(stepId, out var currentMax) || attempt > currentMax)
        {
            maxAttemptByStepId[stepId] = attempt;
        }

        // A step-attempt event's own resolved outcome is wire-named "outcome" (StepAttemptEvent.Outcome,
        // §14.4) — DELIBERATELY a different JSON property from step-completed/scenario-completed's
        // "verdict" (StepCompletedEvent.Verdict / ScenarioCompletedEvent.Verdict). Using Verdict here
        // would silently read the wrong field and always come back null for a genuine attempt event.
        var outcome = RunVerdictExtensions.ParseWireToken(runEvent.Outcome)?.ToString();
        var observation = ExtractObservation(runEvent.Observation);

        // US-S3-06: the RAW token, kept beside the parsed one so "no outcome reported" and "an outcome
        // token this build does not recognise" stay distinguishable — see StepAttempt.RawOutcome for
        // why collapsing both to null was not good enough for get_step_timeline. Capped and sanitised
        // like every other caller-influenced label; an empty or whitespace-only token is normalised to
        // null so it cannot masquerade as a token that was actually present.
        var rawOutcome = string.IsNullOrWhiteSpace(runEvent.Outcome)
            ? null
            : SanitiseAndCapLabel(runEvent.Outcome);

        // `ts` is MEASURED PRESENT on every event the pinned engine writes (see StepAttempt.At and
        // RealStepAttemptEnvelopeAgainstPinnedCliTests); `error` is measured ABSENT and read anyway
        // because the v1 event contract is additive-frozen. `ts` is probed before `at` because it is
        // the spelling the engine actually uses; whichever is present wins.
        var error = AsString(runEvent.Error) is { } errorText && !string.IsNullOrWhiteSpace(errorText)
            ? SanitiseAndCapLabel(errorText)
            : null;
        var rawAt = AsString(runEvent.Ts) is { } ts && !string.IsNullOrWhiteSpace(ts) ? ts : AsString(runEvent.At);
        var at = string.IsNullOrWhiteSpace(rawAt) ? null : SanitiseAndCapLabel(rawAt);

        if (!attemptsByStepId.TryGetValue(stepId, out var list))
        {
            list = [];
            attemptsByStepId[stepId] = list;
        }

        list.Add(new StepAttempt(attempt, runEvent.TMs ?? 0, outcome, observation, rawOutcome, error, at));
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

        // The SAME capped value HandleStepAttempt records the count under — see that method's
        // remarks. Looking this up under the raw id (as an earlier version did) would silently
        // return "not found" (defaulting to 1) whenever the raw id exceeded the cap, since the
        // count would actually be stored under the CAPPED key instead.
        var stepId = SanitiseAndCapLabel(rawStepId);
        var attemptCount = maxAttemptByStepId.TryGetValue(stepId, out var max) ? max : 1;
        var observation = ExtractObservation(runEvent.Observation);

        return new StepOutcome(stepId, verdict.Value.ToString(), runEvent.DurationMs ?? 0, attemptCount, observation);
    }

    private static EnvironmentErrorSummary BuildEnvironmentErrorSummary(RunEvent runEvent)
    {
        var errorKind = runEvent.ErrorKind is { } kind ? SanitiseAndCapLabel(kind) : "Unknown";
        var resourceName = runEvent.ResourceName is { } name ? SanitiseAndCapLabel(name) : "(unknown)";
        var detail = runEvent.Detail is { } detailText ? SanitiseAndCapLabel(detailText) : null;

        return new EnvironmentErrorSummary(errorKind, resourceName, detail);
    }

    /// <summary>
    /// The string value of an opportunistically-probed property, or <see langword="null"/> when the
    /// event carried it as anything other than a JSON string.
    /// </summary>
    /// <remarks>
    /// <b>The reason those properties are typed <see cref="JsonElement"/> rather than
    /// <see cref="string"/></b> (a gatekeeper review's MAJOR finding). Binding <c>ts</c> to a
    /// <c>string?</c> makes <c>"ts": 1757110872382</c> — an epoch-milliseconds spelling a future or
    /// third-party writer could legitimately choose, the v1 contract being additive-frozen — throw
    /// <see cref="JsonException"/> out of the whole-line <c>Deserialize</c> call, and this parser's
    /// tolerance is PER LINE: the line is then skipped entirely, so one wrong-shaped field on a
    /// <c>step-completed</c> event silently costs that step its verdict, and the run its aggregate
    /// one. Probing the element's <see cref="JsonElement.ValueKind"/> instead confines a wrong shape
    /// to the ONE field that carried it — the field reads as absent, which is exactly what it is —
    /// while every other field on the line survives. <see cref="RunEvent.Observation"/> was already
    /// typed this way for the same tolerance reason; this is that precedent applied to the three
    /// fields US-S3-06 added.
    /// </remarks>
    private static string? AsString(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Caps to <see cref="MaxLabelCharsAtParse"/> BEFORE sanitising, mirroring <see cref="ExtractObservation"/>'s cap-then-sanitise ordering.</summary>
    private static string SanitiseAndCapLabel(string rawText)
    {
        var capped = rawText.Length > MaxLabelCharsAtParse ? rawText[..MaxLabelCharsAtParse] : rawText;
        return TextSanitiser.SanitiseForDisplay(capped);
    }

    /// <summary>
    /// Renders an event's <c>observation</c> field (arbitrary structured JSON) as sanitised, capped
    /// raw JSON text — see this type's remarks on <see cref="MaxObservationCharsAtParse"/>.
    /// </summary>
    private static string? ExtractObservation(JsonElement? observation)
    {
        if (observation is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var raw = value.GetRawText();
        var capped = raw.Length > MaxObservationCharsAtParse ? raw[..MaxObservationCharsAtParse] : raw;
        return TextSanitiser.SanitiseForDisplay(capped);
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

        /// <summary>Elapsed wall-clock time for a <c>step-attempt</c> event, in milliseconds (§14.4).</summary>
        [JsonPropertyName("tMs")]
        public long? TMs { get; init; }

        /// <summary>
        /// The raw wire token — <c>"PASS"</c>/<c>"FAIL"</c>/<c>"ENV_ERROR"</c>/<c>"INCONCLUSIVE"</c> —
        /// parsed via <see cref="RunVerdictExtensions.ParseWireToken"/>. Carried by
        /// <c>step-completed</c> and <c>scenario-completed</c> events ONLY — a <c>step-attempt</c>
        /// event's own per-attempt outcome is the wire-DISTINCT <see cref="Outcome"/> field below.
        /// </summary>
        [JsonPropertyName("verdict")]
        public string? Verdict { get; init; }

        /// <summary>
        /// A <c>step-attempt</c> event's own resolved outcome for THAT attempt (<c>StepAttemptEvent.Outcome</c>,
        /// §14.4) — <see langword="null"/> for a mid-RETRY poll with no outcome yet. Wire-DISTINCT
        /// from <see cref="Verdict"/> above; the two are never the same JSON property.
        /// </summary>
        [JsonPropertyName("outcome")]
        public string? Outcome { get; init; }

        [JsonPropertyName("durationMs")]
        public long? DurationMs { get; init; }

        [JsonPropertyName("errorKind")]
        public string? ErrorKind { get; init; }

        [JsonPropertyName("resourceName")]
        public string? ResourceName { get; init; }

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }

        /// <summary>The observation/diff evidence carried by a <c>step-attempt</c> or <c>step-completed</c> event (§14.4).</summary>
        [JsonPropertyName("observation")]
        public JsonElement? Observation { get; init; }

        /// <summary>
        /// A <c>step-attempt</c> event's own per-attempt error text, if the stream carries one —
        /// spec §5.10's <c>Attempt.error?</c>. See <see cref="StepAttempt.Error"/>: measured ABSENT
        /// at the pinned engine, probed because the event contract is additive-frozen.
        /// </summary>
        /// <remarks><see cref="JsonElement"/>, not <see cref="string"/> — see <see cref="AsString"/>.</remarks>
        [JsonPropertyName("error")]
        public JsonElement? Error { get; init; }

        /// <summary>
        /// An event's own absolute timestamp, first spelling — and the one the pinned engine
        /// actually writes, on EVERY event (measured; see <see cref="StepAttempt.At"/>).
        /// </summary>
        /// <remarks><see cref="JsonElement"/>, not <see cref="string"/> — see <see cref="AsString"/>.</remarks>
        [JsonPropertyName("ts")]
        public JsonElement? Ts { get; init; }

        /// <summary>An event's own absolute timestamp, second spelling — spec §5.10's own field name. See <see cref="Ts"/>.</summary>
        /// <remarks><see cref="JsonElement"/>, not <see cref="string"/> — see <see cref="AsString"/>.</remarks>
        [JsonPropertyName("at")]
        public JsonElement? At { get; init; }
    }
}
