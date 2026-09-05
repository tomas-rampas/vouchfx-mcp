using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — get_run_events models (Sprint 3 / US-S3-05; spec §5.11, §4.5).
//
// Spec §5.11 fixes the success shape:
//
//   interface GetRunEventsOutput { meta: ToolMeta; eventSchemaVersion: string; events: object[];
//                                  nextCursor?: string; }
//
// `meta` is NOT a field here: StructuredToolResult.Success stamps it onto every successful result
// through the one choke point every tool uses (see that type's remarks), so declaring it on the
// payload would be a second copy free to drift — and would in fact be REJECTED, since that helper
// refuses a payload already carrying a top-level `meta`.
//
// ONE field is added to that list: `truncated`. An earlier version of this note argued the opposite
// — that per-event truncation is reported in the event that carries it (`_vfxTruncated`) and nothing
// else was needed. That reasoning covered the wrong failure. `_vfxTruncated` says "this EVENT was too
// big"; it says nothing about the ways the page as a whole can fall short of the stream — the two
// that stop the SCAN early (the 50 MB reader cap, and the two-million-line backstop) and the two that
// drop a single line from an otherwise complete scan (an over-long line on a FILTERED page, which
// cannot be parsed and so cannot be shown to match; and, since US-S3-03, a MID-FILE line that is not
// parseable as a JSON object at all). All of them can end a page with no `nextCursor` — which the
// tool's own contract tells a host to read as "the walk is over". Without `truncated` a host
// concludes it has the whole stream when it demonstrably does not, and the additive field is far
// cheaper than that silence. See GetRunEventsResult.Truncated.

/// <summary>
/// <c>get_run_events</c>'s arguments, as the caller sent them — unvalidated. Every bound and default
/// is applied by <see cref="GetRunEventsOrchestrator"/>, so a test can construct a request that a
/// well-behaved MCP client could never produce and still exercise the guards.
/// </summary>
/// <param name="RunId">The run to read, as recorded in the run registry.</param>
/// <param name="Types">
/// Event types to keep (e.g. <c>step-attempt</c>), or <see langword="null"/>/empty for every type.
/// Matched ordinally against the RAW <c>type</c> the engine wrote — see
/// <see cref="RawEventRelay.RawStringProperty"/> for why filtering compares raw against raw.
/// </param>
/// <param name="StepId">One step's events only, or <see langword="null"/> for every step.</param>
/// <param name="Limit">
/// Maximum events to return. <see langword="null"/> means <see cref="GetRunEventsOrchestrator.DefaultLimit"/>
/// (spec §4.5's 200); above <see cref="GetRunEventsOrchestrator.MaxLimit"/> (2000) is refused, never
/// silently clamped — a caller who asked for 5000 and got 2000 with no signal would reasonably read
/// the short page as "that is all there was".
/// </param>
/// <param name="Cursor">
/// A <c>nextCursor</c> from a previous call, or <see langword="null"/> to start at the first page.
/// Opaque (<see cref="OpaqueCursor"/>).
/// </param>
public sealed record GetRunEventsRequest(
    string? RunId,
    IReadOnlyList<string>? Types = null,
    string? StepId = null,
    int? Limit = null,
    string? Cursor = null);

/// <summary>Spec §5.11's success payload, minus the <c>meta</c> the result envelope stamps on.</summary>
/// <param name="EventSchemaVersion">
/// The version of the event contract these events were read under — see
/// <see cref="GetRunEventsOrchestrator.ResolveEventSchemaVersion"/> for where the value comes from
/// and why it is not simply hardcoded.
/// </param>
/// <param name="Events">
/// The page's events, in file order, each the sanitised and bounded relay of the engine's own JSON
/// object (<see cref="RawEventRelay"/>). <b>Wire vocabulary</b>: a verdict field reads
/// <c>PASS</c>/<c>FAIL</c>/<c>ENV_ERROR</c>/<c>INCONCLUSIVE</c> exactly as the engine emitted it,
/// never this server's <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> response
/// strings (sprint-00-overview.md §5).
/// </param>
/// <param name="NextCursor">
/// The token to pass as <c>cursor</c> for the next page, or <see langword="null"/> — omitted from the
/// JSON entirely — when this page is the last one. Present ONLY when a further matching event
/// genuinely exists: <see cref="GetRunEventsOrchestrator"/> looks one match ahead before minting it,
/// so a host that follows <c>nextCursor</c> never receives an empty page as its stopping condition.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when what this page reports is not everything the run's stream held, for
/// any of the four reasons that can happen: the file exceeded
/// <see cref="EventsFileReader.MaxEventsFileBytes"/> and was read only up to that cap, the scan hit
/// <see cref="GetRunEventsOrchestrator.MaxLinesProcessed"/>, a line past
/// <see cref="RawEventRelay.MaxEventLineChars"/> was passed over with its match status unknown on a
/// <c>types</c>/<c>stepId</c> filtered page (see <see cref="GetRunEventsOrchestrator.BuildPage"/>: it
/// is never parsed, so it cannot be asserted to match, and it cannot be admitted as the label-less
/// marker either), or a MID-FILE line was not parseable as a JSON object at all. The first two are
/// the SCAN stopping short; the last two are individual lines dropped from an otherwise complete
/// scan. One flag covers all four because a host's question is the same in every case — "is this the
/// whole answer?" — and the honest reply is no.
/// <para>
/// <b>The fourth reason is deliberately MID-FILE only</b> (a peer review's carry-in from US-S3-05,
/// closed in US-S3-03). A bounded read routinely ends on a partial line the byte cap cut through, and
/// that is already what the first reason reports; flagging it twice would add nothing. An unparseable
/// line with more lines AFTER it is a different fact — a hole in the middle of a scan that otherwise
/// completed, previously invisible because no cursor was owed and the page looked whole. It is
/// reachable in production through US-S3-02's multi-suite merge, whose failed-copy path terminates
/// the partial part with a newline and then appends the next suite's events behind it.
/// </para>
/// <para>
/// <b>Read it together with <see cref="NextCursor"/>, never instead of it.</b> The two answer
/// different questions: <c>nextCursor</c> says "more MATCHING events remain within what was read",
/// and <c>truncated</c> says "what was read is not all there was". Their dangerous combination is
/// <c>truncated: true</c> with no cursor — the page walk has ended, but at this server's bound rather
/// than at the end of the run, and a host that treated the absent cursor alone as completion would
/// silently believe it holds a whole timeline.
/// </para>
/// </param>
public sealed record GetRunEventsResult(
    [property: JsonPropertyName("eventSchemaVersion")] string EventSchemaVersion,
    [property: JsonPropertyName("events")] IReadOnlyList<JsonElement> Events,
    [property: JsonPropertyName("nextCursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCursor,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>
/// What one <c>get_run_events</c> call produced — a closed union (the private constructor confines
/// derivation to the cases nested here), mirroring <see cref="RunSuiteOutcome"/> and
/// <c>ExplainRunOutcome</c> so the tool's switch maps each case to exactly one <c>VFX-E-</c> code and
/// the compiler enumerates the cases when one is added.
/// </summary>
public abstract record GetRunEventsOutcome
{
    private GetRunEventsOutcome()
    {
    }

    /// <summary>The page was produced.</summary>
    public sealed record Paged(GetRunEventsResult Result) : GetRunEventsOutcome;

    /// <summary>An argument was missing, out of range, or over a bound — <c>VFX-E-1006</c>.</summary>
    public sealed record InvalidArgument(string Message) : GetRunEventsOutcome;

    /// <summary>The <c>cursor</c> could not be verified — <c>VFX-E-1506</c>.</summary>
    public sealed record InvalidCursor(string Message) : GetRunEventsOutcome;

    /// <summary>No run with that id is in the registry — <c>VFX-E-1505</c>.</summary>
    public sealed record RunNotFound(string Message) : GetRunEventsOutcome;

    /// <summary>The run's events path is a UNC location or escapes the workspace — <c>VFX-E-1001</c>.</summary>
    public sealed record InvalidPath(string Message) : GetRunEventsOutcome;

    /// <summary>The run exists but its events file is gone — <c>VFX-E-1004</c>.</summary>
    public sealed record EventsFileNotFound(string Message) : GetRunEventsOutcome;

    /// <summary>The events file exists but could not be read — <c>VFX-E-1005</c>.</summary>
    public sealed record EventsFileUnreadable(string Message) : GetRunEventsOutcome;
}
