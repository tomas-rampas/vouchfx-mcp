using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp — run registry entry model (Sprint 3 / US-S3-01; spec §5.7-§5.8, plan §2.7 invariant 4).
//
// One entry per run_suite call. This is the ONLY thing the registry stores, and the list of fields
// below is deliberately exhaustive: paths, timestamps, status/outcome, and free-form labels. No
// environment value, no log line, no events-file CONTENT, no suite YAML ever lands here — see
// IRunRegistry's remarks for why that is a hard boundary rather than a current-scope accident.

/// <summary>
/// The <c>status</c> vocabulary of spec §5.8's <c>RunSummary</c>, as string constants — the exact
/// tokens a future <c>get_run_status</c>/<c>list_runs</c> response carries, so the registry stores
/// the response vocabulary directly and nothing has to translate on the way out.
/// </summary>
/// <remarks>
/// <para>
/// <b>All five are declared; THREE are reachable as of US-S3-03.</b> <see cref="Running"/> is
/// written at run start and <see cref="Completed"/> at run completion. <see cref="Cancelled"/> was
/// declared-but-unwritten until US-S3-03's <c>cancel_run</c> gave it the only thing that could
/// distinguish it from an ordinary completion — a deliberate lifecycle ACTION by a host — and is now
/// written by <c>RunSuiteOrchestrator.TerminalStatusFor</c>, at exactly one site.
/// <para>
/// The two that remain unreachable, and why. <see cref="Queued"/> needs non-blocking
/// <c>wait: false</c>, which US-S3-02 landed as an ACCEPTED-BUT-REFUSED input (upstream ask U4,
/// <c>VFX-E-1504</c>), so nothing writes it and nothing will until that ask lands.
/// <see cref="Timeout"/> is a deliberate NON-write rather than a gap: a run whose own
/// <c>timeoutSeconds</c> budget expires — like one the MCP caller cancels — still completes with an
/// <c>Inconclusive</c> OUTCOME under <see cref="Completed"/> (EDGE-002's taxonomy: the run finished,
/// it just reached no definitive verdict), which is what every existing result asserts and what no
/// acceptance criterion in this sprint asked to change. Splitting it out later is additive; writing
/// a status this file does not name would not be, which is why all five are declared here.
/// </para>
/// </para>
/// <para>
/// Lower-case, matching spec §5.8's literal union exactly. Contrast <see cref="RunRegistryEntry.Outcome"/>,
/// which carries the PascalCase <see cref="RunVerdict"/> names this server already puts on the wire.
/// The two vocabularies genuinely differ in the spec and are deliberately not harmonised here.
/// </para>
/// </remarks>
public static class RunRegistryStatus
{
    /// <summary>Accepted but not yet started — unreachable until US-S3-02's <c>wait: false</c>.</summary>
    public const string Queued = "queued";

    /// <summary>The run is in flight. Written at run start; <see cref="RunRegistryEntry.Outcome"/> is <see langword="null"/>.</summary>
    public const string Running = "running";

    /// <summary>The run finished and reached one of the four <see cref="RunVerdict"/> outcomes.</summary>
    public const string Completed = "completed";

    /// <summary>
    /// The run was stopped by a <c>cancel_run</c> call (US-S3-03). Its
    /// <see cref="RunRegistryEntry.Outcome"/> is whatever the run genuinely reached — for a run
    /// cancelled before any suite failed, <c>Inconclusive</c> — and is never rewritten by the
    /// cancellation itself; see <c>RunSuiteOrchestrator.TerminalStatusFor</c>.
    /// </summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The run exceeded its budget and was abandoned as a distinct status. <b>Deliberately never
    /// written</b> — a timed-out run reports <see cref="Completed"/> with an <c>Inconclusive</c>
    /// outcome, as it always has; see this type's remarks.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>Every status this server may write, in spec §5.8's own declaration order.</summary>
    public static readonly IReadOnlyList<string> All = [Queued, Running, Completed, Cancelled, Timeout];

    /// <summary>
    /// Whether <paramref name="status"/> means "this run is over" — the three statuses that stamp
    /// <see cref="RunRegistryEntry.FinishedAtUtc"/> and make an entry eligible to be
    /// <c>explain_run</c>'s default. Compared ordinally: these are protocol tokens, not display text.
    /// </summary>
    public static bool IsTerminal(string status) =>
        string.Equals(status, Completed, StringComparison.Ordinal)
        || string.Equals(status, Cancelled, StringComparison.Ordinal)
        || string.Equals(status, Timeout, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="status"/> is one of <see cref="All"/> — the fail-closed check every write point makes.</summary>
    public static bool IsKnown(string status) => All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// One run's persisted metadata — the registry's whole record of a <c>run_suite</c> call, and
/// (field for field) the source US-S3-03's <c>get_run_status</c>/<c>list_runs</c> will project
/// spec §5.8's <c>RunSummary</c> from.
/// </summary>
/// <param name="RunId">
/// This run's identifier: <c>run-</c> followed by 32 lowercase hex characters (a
/// <see cref="Guid"/>'s <c>N</c> format). <b>Server-minted, pre-U4.</b> The engine does not yet emit
/// a stable run id of its own — that is upstream work item U4 — so until it does, this server mints
/// the id and owns it. When U4 lands, the engine's id becomes authoritative and this shape becomes a
/// fallback for engines that predate it; nothing outside the registry parses the id's INTERNAL
/// structure, precisely so that swap costs nothing. The <c>run-</c> prefix is what keeps a run id
/// distinguishable at a glance from the events-file GUID it used to be confused with, and — because
/// the file-backed registry names a directory after it — what guarantees a run id can never collide
/// with a non-run directory name a future layout adds beside it.
/// </param>
/// <param name="Status">One of <see cref="RunRegistryStatus"/>'s tokens. Never free text.</param>
/// <param name="Outcome">
/// The run's overall verdict as one of <see cref="RunVerdict"/>'s four PascalCase names
/// (<c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c>) — <b>the MCP response
/// vocabulary, never the engine's own wire tokens</b> (<c>PASS</c>, <c>ENV_ERROR</c>, …). The
/// registry is the response's source, so storing a wire token here would leak the engine's
/// serialisation into this server's contract; <see cref="IRunRegistry.RecordStatusTransition"/>
/// rejects anything that is not one of the four names. <see langword="null"/> while the run is still
/// <see cref="RunRegistryStatus.Running"/>, matching spec §5.8's optional <c>outcome?</c>.
/// </param>
/// <param name="StartedAtUtc">When the run started, in UTC. Strictly increasing within one registry instance — see <see cref="IRunRegistry.ListRuns"/>.</param>
/// <param name="FinishedAtUtc">When the run reached a terminal status; <see langword="null"/> while it is still running.</param>
/// <param name="SpecPaths">
/// The suite path(s) this run covered, in the order they ran. <b>Array-shaped from day one</b>, a
/// story before it was needed — US-S3-02 then landed <c>paths: string[]</c> with glob expansion and
/// populated it with every suite of a multi-suite run, at no format cost, which is exactly what a
/// scalar field here would have made impossible without a breaking change or a second parallel field.
/// </param>
/// <param name="EventsFilePath">
/// Where this run's JSON Lines event stream lives — the file <c>explain_run</c>/<c>diagnose_run</c>
/// read. Minted by the registry itself (see <see cref="IRunRegistry.StartRun"/>), not by the caller,
/// so the registry is the single authority on where a run's artefacts live.
/// </param>
/// <param name="Labels">
/// Free-form host-supplied labels (spec §5.7's <c>labels?: Record&lt;string,string&gt;</c>), bounded
/// and validated at the tool boundary (<c>RunSuiteOrchestrator.ValidateLabels</c>) and stored
/// verbatim — never sanitised, because a label is matched by a host rather than displayed. Populated
/// since US-S3-02; empty when the caller sent none.
/// <para>
/// <b>This is the ONLY place a run's labels live in this build.</b> Spec §5.7 also describes labels
/// appearing in the JSON Lines run envelope. Every EVENT in that stream is authored by the engine —
/// this server never composes one, and the pinned CLI has no labels flag through which to ask it to
/// — so that half awaits upstream work rather than being simulated here. (It does APPEND
/// engine-produced bytes to that file when a multi-suite run merges its per-suite parts; copying a
/// stream the engine wrote is not the same as authoring an event in it, and the distinction is the
/// whole reason the labels half cannot simply be filled in locally.)
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <b>Every property carries an explicit <see cref="JsonPropertyName"/>, deliberately.</b> The names
/// are spec §5.8's own (<c>startedAt</c>, not <c>startedAtUtc</c>), so the persisted document and
/// the eventual wire response agree without a translation layer — and per-property attributes are
/// the only naming mechanism that travels reliably when a type is serialised through someone else's
/// <c>JsonSerializerOptions</c> (the same reasoning <c>Contracts/ToolMeta</c> and
/// <c>Contracts/VfxError</c> already record: a naming POLICY on an options instance does not travel
/// with the type; an attribute does).
/// </para>
/// <para>
/// <b>Immutable, so a reader can never observe a half-updated entry.</b> A status transition
/// produces a NEW entry with <c>with</c>; the registry then swaps it in as a whole. That is what
/// lets <see cref="IRunRegistry.ListRuns"/> hand snapshots out without copying or locking.
/// </para>
/// </remarks>
public sealed record RunRegistryEntry(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAtUtc,
    [property: JsonPropertyName("specPaths")] IReadOnlyList<string> SpecPaths,
    [property: JsonPropertyName("eventsFilePath")] string EventsFilePath,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string> Labels);
