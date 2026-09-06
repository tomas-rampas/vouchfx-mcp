using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — get_run_artifacts models (Sprint 3 / US-S3-07; spec §5.12).
//
// Spec §5.12 fixes the shapes:
//
//   interface GetRunArtifactsInput  { runId: string; kind?: "logs" | "reports" | "environment" | "all";
//                                     container?: string; tailLines?: number; /* default 200, max 5000 */ }
//   interface GetRunArtifactsOutput { meta: ToolMeta;
//                                     reports?: { html?: string; junit?: string; };
//                                     logs?: { container: string; lines: string[]; truncated: boolean;
//                                              resourceUri: string; }[];
//                                     environment?: { services: { id: string; image: string; health: string;
//                                                                 ports: object; }[]; dependencies: object[]; }; }
//
// `meta` is NOT a field here: StructuredToolResult.Success stamps it through the one choke point every
// tool uses, and a payload carrying its own top-level `meta` is REJECTED there.
//
// ---------------------------------------------------------------------------------------------
// How this tool reports its upstream gap: a payload-level `partial: true`, and WHY that is the
// right form here when US-S3-06 chose the opposite
// ---------------------------------------------------------------------------------------------
//
// sprint-00-overview.md §3's gated-feature stances give this story stance (b) — "a data-returning
// surface with an upstream-gated portion succeeds with `partial: true` and whatever subset this repo
// can derive, never an error and never a fabricated value". US-S3-06's get_step_timeline sits under the
// same tracker entry (U4/U7) and deliberately does NOT carry such a marker, so the discriminator that
// separates them is recorded here rather than left to look like an inconsistency. It was sharpened on
// 2026-09-06 by a review that pointed out the obvious-looking version of it ("a payload-level marker is
// for a gap that varies per call") is FALSE OF THIS TOOL, and the corrected reasoning is worth more
// than the tidy one:
//
//   * BE PRECISE ABOUT WHAT VARIES, because the U4-gated part of this tool's gap set does NOT.
//     `reports.html`, `reports.junit`, `logs`, `environment.services`, `environment.dependencies` and
//     `environment.resources[].health` are appended on EVERY call that selects their section, so
//     `partial` is measurably true for every input this build can be given — the pinning test
//     (Partial_IsTrueExactlyWhenThereAreGaps, across all four `kind` values) says exactly that and is
//     meant to. What varies per call is the LOCAL gap set: the `awaits: null` entries
//     (`reports.events`, `environment.resources`) appear only for a run whose events file has since
//     been swept or cannot be read, and not for one whose stream is intact.
//   * So the discriminator is not "does the flag flip today" but HOW THE GAP CLOSES. The AC and its
//     Gherkin require the marker ("the result's partial field is true"), and the gaps behind it close
//     INCREMENTALLY as U4 lands section by section: an engine artifacts directory alone retires two
//     entries and leaves the rest, at which point `partial` stays true and means something narrower.
//     Carrying it as a boolean COMPUTED from `Gaps` (see GetRunArtifactsResult.Partial) is what makes
//     that possible without an edit here — a hardcoded `true` would have to be found and changed by
//     whoever lands each slice, and would be silently wrong in between.
//   * US-S3-06's nulls are the opposite shape: STRUCTURALLY permanent for that build rather than
//     incrementally closing. `get_step_timeline`'s `delayMs` has no source on any event type at all, so
//     no partial landing retires it; an explicit null at the field it concerns is the honest form, and
//     a payload-level `partial` beside it would be an invariant dressed up as data.
//
// Both halves are carried here: `partial` says THAT something is missing, and `gaps` says exactly WHAT
// and WHY, per field, with the upstream ask named. A host never has to diff this payload against the
// spec to find out what it did not get.
//
// ---------------------------------------------------------------------------------------------
// The honest inventory: what this build can actually derive, MEASURED against the pinned engine
// ---------------------------------------------------------------------------------------------
//
// This tool invents no source. Everything it can report comes from exactly two places — the run
// registry (US-S3-01) and the run's JSON Lines event stream — and the inventory is short:
//
//   * REPORTS. The registry mints and records `eventsFilePath`, so the event stream itself is a real
//     artefact this server can point a host at, and does (`reports.events`). The engine's own JUnit and
//     HTML reports are NOT derivable: the pinned CLI writes them where its own flags say, this server
//     neither passes those flags nor is told the resulting paths, and an artifacts DIRECTORY the engine
//     owns is precisely what upstream ask U4 covers. `reports.html`/`reports.junit` are therefore
//     OMITTED (spec §5.12 marks both optional) rather than emitted as nulls that would suggest this
//     server looked and found nothing.
//   * LOGS. Nothing. There is no container log access in this build at all — no engine flag exposes it
//     and this server never talks to a container runtime — so `logs` is an EMPTY ARRAY, which AC-002
//     requires be a success rather than an error and which nothing may fabricate lines into.
//   * ENVIRONMENT. Only what an `environment-error` event names. SuiteEventParser recognises four event
//     types (`step-attempt`, `step-completed`, `scenario-completed`, `environment-error`) and exactly
//     one of them carries an environment identifier: `environment-error`'s `resourceName`, beside its
//     `errorKind` and `detail` (EnvironmentErrorSummary). The pinned engine's DISTINCT event-type
//     vocabulary is MEASURED, not inferred, by RealStepAttemptEnvelopeAgainstPinnedCliTests, which
//     pins the whole set from a real run and records the verbatim lines. It is six types, and the two
//     the parser does not handle are `step-started` (a step's kind, verifyMode and timeoutMs) and
//     `scenario-started` (a runId and a scenarioId). There is also one run-level event —
//     `reproducibility-envelope` — and it is the only one that describes the environment at all:
//     measured, it carries `envSchemaVersion`, `secretReferences` and `fixtures`, and a run with a
//     declared redis DEPENDENCY named it nowhere in that envelope. So none of the three carries a
//     service or dependency identifier: there is no `environment-started`, no service inventory, no
//     image, no port map and no health anywhere in the v1 stream.
//     (An earlier version of this paragraph said the vocabulary "adds only step-started". That was an
//     inference from what US-S3-06 needed rather than a reading of a real file, and it was wrong about
//     the set even though its conclusion survived the measurement.)
//
// Two consequences follow, and both are stated rather than smoothed over:
//
//   1. A run that went well reports NO environment identifiers. They appear only where something
//      failed, because a failure event is the only place the stream names a resource. An empty
//      `environment` section is therefore an ordinary, correct answer — not an error, and not evidence
//      that the tool is broken.
//   2. The stream never says whether a named resource is a SERVICE or a DEPENDENCY. The suite language
//      has both (`environment.services` and `environment.dependencies` — see vendored/
//      language-reference.md, where step `target` fields name each), and an `environment-error` event
//      carries a bare Aspire resource name that could be either. So neither spec array claims it: the
//      identifiers land in an additive `resources` array whose entries say `role: "unclassified"`, and
//      `services`/`dependencies` stay empty until U4 gives them a source that can tell them apart.
//      Guessing a classification would be a fabrication of exactly the kind stance (b) forbids.
//
// The one derivation that was AVAILABLE and is refused: reading the suite file's own `environment:`
// block. It would name every declared service and dependency — but the file on disk today is not
// necessarily the file that ran (nothing pins a content hash into the registry), so the answer would be
// an assertion about the run sourced from something that is not the run. GetStepTimelineResult.VerifyMode
// refuses the identical shortcut for the identical reason, and this server never opens a suite it was
// not asked to validate.
//
// Flagged for spec adjudication: §5.12's own AC-002 Gherkin premise ("a run whose REGISTRY ENTRY
// recorded service ids … and dependency ids …") describes a registry field that does not exist —
// RunRegistryEntry stores runId, status, outcome, timestamps, specPaths, eventsFilePath and labels, and
// IRunRegistry's remarks make the absence of environment data a hard boundary rather than a gap. The
// events stream is the only source there is, and it yields the unclassified identifiers above.

/// <summary><c>get_run_artifacts</c>' arguments, as the caller sent them — unvalidated.</summary>
/// <param name="RunId">The run whose artefacts are wanted, as recorded in the run registry.</param>
/// <param name="Kind">
/// Which section(s) to return — one of <see cref="RunArtifactKind"/>'s four literals, or
/// <see langword="null"/> for all three (equivalent to <see cref="RunArtifactKind.All"/>).
/// </param>
/// <param name="Container">
/// Which container's logs to tail. <b>Accepted and validated, and it currently selects nothing</b> —
/// see <see cref="GetRunArtifactsResult.Container"/> for the forward-compatibility reasoning.
/// </param>
/// <param name="TailLines">
/// How many log lines to tail. <b>Accepted and validated, and there are currently no lines to tail</b>
/// — see <see cref="GetRunArtifactsResult.TailLines"/>.
/// </param>
public sealed record GetRunArtifactsRequest(
    string? RunId, string? Kind = null, string? Container = null, int? TailLines = null);

/// <summary>Spec §5.12's <c>kind</c> union, as string constants. Lower-case, matched ordinally.</summary>
public static class RunArtifactKind
{
    /// <summary>Report artefacts only — the events stream this server recorded, and (post-U4) the engine's own reports.</summary>
    public const string Reports = "reports";

    /// <summary>Container logs only. Always an empty list in this build; see this file's header.</summary>
    public const string Logs = "logs";

    /// <summary>Environment state only — whatever identifiers the run's events named.</summary>
    public const string Environment = "environment";

    /// <summary>Every section. The default when <c>kind</c> is omitted.</summary>
    public const string All = "all";

    /// <summary>Every accepted value, in spec §5.12's own declaration order.</summary>
    /// <remarks>
    /// The membership test lives at the ONE place that needs it —
    /// <see cref="GetRunArtifactsOrchestrator.ValidateArguments"/>, which matches
    /// case-insensitively and needs the MATCHED element (to echo the canonical spelling) rather than
    /// a bool. An <c>IsKnown</c> helper existed here briefly and was never called: it compared
    /// ordinally, so anything written against it would have disagreed with the only real gate. Deleted
    /// rather than left as a second, subtly different answer to the same question.
    /// </remarks>
    public static readonly IReadOnlyList<string> AllValues = [Logs, Reports, Environment, All];
}

/// <summary>What <see cref="RunEnvironmentResource.Role"/> may say about a named environment resource.</summary>
/// <remarks>
/// One value is reachable in this build, and that is the point: the v1 event stream names a resource
/// without saying which side of the suite's <c>environment</c> block declared it (see this file's
/// header). The two classified values are declared so the U4 landing is additive — a host that switches
/// on this field today needs no change when the classification arrives — and are deliberately never
/// written by anything here.
/// </remarks>
public static class RunEnvironmentResourceRole
{
    /// <summary>A service declared under the suite's <c>environment.services</c>. <b>Never written in this build.</b></summary>
    public const string Service = "service";

    /// <summary>A dependency declared under the suite's <c>environment.dependencies</c>. <b>Never written in this build.</b></summary>
    public const string Dependency = "dependency";

    /// <summary>
    /// The event stream named this resource but did not say which of the two it is — the only value
    /// this build produces, and an honest refusal to guess rather than a placeholder.
    /// </summary>
    public const string Unclassified = "unclassified";
}

/// <summary>Where a reported environment identifier was read from.</summary>
public static class RunEnvironmentResourceSource
{
    /// <summary>An <c>environment-error</c> event in the run's own JSON Lines stream — the only source this build has.</summary>
    public const string EnvironmentErrorEvent = "environment-error";

    /// <summary>
    /// An <c>environment-error</c> event that named NO resource. The entry still carries its
    /// <c>errorKind</c>, <c>detail</c> and <c>occurrences</c>; its <see cref="RunEnvironmentResource.Id"/>
    /// is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>A distinct source value rather than a made-up id</b> (a gatekeeper review's minor finding).
    /// <see cref="SuiteEventParser.UnnamedResourceSentinel"/> — the literal string <c>(unknown)</c> —
    /// is what the shared parser substitutes for an absent <c>resourceName</c>, and relaying it as an
    /// <c>id</c> made a sentence this server invented indistinguishable, on the wire, from an Aspire
    /// resource the engine really named. Every unnamed event folds into ONE such entry per run, so the
    /// occurrence count still says how many times the run failed without naming anything.
    /// </remarks>
    public const string UnnamedEnvironmentErrorEvent = "environment-error-unnamed";
}

/// <summary>
/// One environment resource this run's event stream named, with everything the stream said about it and
/// nothing it did not.
/// </summary>
/// <param name="Id">
/// The resource name the engine reported (an Aspire resource name), already sanitised and capped by
/// <see cref="SuiteEventParser"/> at parse time and capped again here for the response — or
/// <see langword="null"/> when the event named no resource at all, in which case
/// <see cref="Source"/> says <see cref="RunEnvironmentResourceSource.UnnamedEnvironmentErrorEvent"/>.
/// <b>This field is never anything but an engine-reported name</b>: the parser's own
/// <see cref="SuiteEventParser.UnnamedResourceSentinel"/> placeholder is recognised and turned into
/// that null rather than passed through as an identity nothing reported.
/// </param>
/// <param name="Role">
/// One of <see cref="RunEnvironmentResourceRole"/>'s literals — <c>unclassified</c> for everything this
/// build produces, because the event says which resource failed and never which kind of resource it is.
/// </param>
/// <param name="Health">
/// The resource's live health. <b>Always <see langword="null"/>, and explicitly present rather than
/// omitted</b>: spec §5.12 types it on a service entry, US-S3-07's AC-002 names it ("with no health
/// field populated"), and live health needs a probe against a running environment that this server has
/// no channel to make — upstream ask U4. A null here means "not observed", never "unhealthy".
/// </param>
/// <param name="ErrorKind">
/// The <c>OrchestrationErrorKind</c> the engine reported for the FIRST event naming this resource (e.g.
/// <c>ImagePull</c>, <c>Provision</c>, <c>HealthGate</c>). <b>Additive</b> — spec §5.12 does not list it
/// — and kept because a bare identifier with no reason is barely worth returning.
/// </param>
/// <param name="Detail">
/// The engine's own one-line detail for that first event, sanitised and capped, or
/// <see langword="null"/> when it reported none. <b>Engine-redacted already</b>: this server never
/// re-redacts and never resolves a <c>${secret:…}</c>.
/// </param>
/// <param name="Occurrences">
/// How many <c>environment-error</c> events in this run named this resource. A derived count, not an
/// estimate: the parse walks the whole (bounded) stream, and a resource that failed repeatedly is a
/// materially different situation from one that failed once.
/// </param>
/// <param name="Source">
/// One of <see cref="RunEnvironmentResourceSource"/>'s literals, so a host can tell a derived
/// identifier from a (future) declared one without inferring it from which array carried it — and,
/// today, a named failure from an unnamed one without having to test <see cref="Id"/> for null.
/// </param>
public sealed record RunEnvironmentResource(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("health")] string? Health,
    [property: JsonPropertyName("errorKind")] string? ErrorKind,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail,
    [property: JsonPropertyName("occurrences")] int Occurrences,
    [property: JsonPropertyName("source")] string Source);

/// <summary>Spec §5.12's <c>environment</c> section.</summary>
/// <param name="Services">
/// Spec §5.12's <c>services</c>. <b>Always empty in this build</b> — nothing in the v1 event stream
/// declares a service, and its typed fields (<c>image</c>, <c>health</c>, <c>ports</c>) have no source
/// at all pre-U4. Typed as the same entry shape <see cref="Resources"/> carries rather than as a
/// separate never-constructed record: post-U4 a classified entry is one that says
/// <see cref="RunEnvironmentResourceRole.Service"/> and carries the fields this build cannot fill, so
/// one widening type keeps that landing additive.
/// </param>
/// <param name="Dependencies">
/// Spec §5.12's <c>dependencies</c> (typed there as a bare <c>object[]</c>). <b>Always empty in this
/// build</b>, for the same reason <see cref="Services"/> is.
/// </param>
/// <param name="Resources">
/// <b>Additive, and the only array this build populates.</b> Every environment resource the run's
/// events named, deduplicated by id in first-appearance order. It exists precisely so that neither spec
/// array has to claim a classification the stream does not carry — see this file's header.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when what came back is not everything the run's stream held about its
/// environment — <b>the same meaning <c>get_run_events.truncated</c> and
/// <c>get_step_timeline.truncated</c> carry</b>, deliberately named the same so a host learns one rule.
/// Two reasons set it: the events file exceeded <see cref="EventsFileReader.MaxEventsFileBytes"/> and
/// was read only up to that cap, or more distinct resources were named than
/// <see cref="GetRunArtifactsOrchestrator.MaxEnvironmentResources"/> allows
/// (<see cref="OmittedResourceCount"/> says how many).
/// </param>
/// <param name="OmittedResourceCount">
/// How many distinct resources the response bound dropped from the end of <see cref="Resources"/>;
/// <c>0</c> for every ordinary run. Non-zero implies <see cref="Truncated"/>, but not the reverse — a
/// byte-capped events file truncates without dropping anything this server ever saw.
/// </param>
public sealed record RunEnvironmentArtifacts(
    [property: JsonPropertyName("services")] IReadOnlyList<RunEnvironmentResource> Services,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<RunEnvironmentResource> Dependencies,
    [property: JsonPropertyName("resources")] IReadOnlyList<RunEnvironmentResource> Resources,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("omittedResourceCount")] int OmittedResourceCount);

/// <summary>The run's JSON Lines event stream, as an artefact a host can go and read.</summary>
/// <param name="Path">
/// The absolute path the run registry recorded for this run, sanitised and capped for display. It is a
/// FILESYSTEM PATH, not a resource URI — see <see cref="ResourceUri"/>.
/// <para>
/// <b>Strictly NARROWER than what <c>get_run_status</c> already discloses for the same run</b>, and an
/// earlier version of this sentence claimed it was "the same value" (a security review's finding). It
/// is not: <c>get_run_status</c> serialises the <see cref="RunRegistryEntry"/> itself, so its
/// <c>eventsFilePath</c> is the registry's RAW string, while this one goes through
/// <see cref="Validation.PathSafetyGuard.CapAndSanitisePathForDisplay"/> — identical for an
/// ASCII path, and for a path containing any character outside <c>0x20</c>-<c>0x7E</c> a strict subset
/// of it (each such character becomes a literal <c>\uXXXX</c> escape, and the result is capped at 1,000
/// characters). So the disclosure claim holds in the direction that matters — a caller who may call
/// this tool may already call <c>get_run_status</c> and learn at least as much.
/// </para>
/// <para>
/// <b>The host-usability consequence, stated rather than left to be discovered:</b> for a run whose
/// events path contains non-ASCII characters — an accented directory name, a CJK one — the string
/// returned here is NOT openable verbatim. A host that wants to open the file should use
/// <c>get_run_status</c>'s raw <c>eventsFilePath</c>; this field is for display, and
/// <see cref="Available"/> is the fact it exists to carry.
/// </para>
/// </param>
/// <param name="Available">
/// Whether that file exists right now. <see langword="false"/> is an ordinary, reportable state rather
/// than an error: a run's metadata outlives its event stream whenever the output directory is cleaned,
/// and an artefacts INVENTORY that refused to answer in that case would be less useful than one that
/// says "this is where it was, and it is gone".
/// </param>
/// <param name="ResourceUri">
/// Spec §5.12 describes the report fields as resource URIs. <b>Always <see langword="null"/>: this
/// server advertises no run-artefact resource family</b> (the only resources it serves today are the
/// vendored documents and the <c>vouchfx-docs:///errors/{code}</c> catalogue). The path above is what
/// exists; a <c>vouchfx://</c> URI is a later sprint's work, and inventing one that resolves to nothing
/// would be worse than a null.
/// </param>
public sealed record RunEventsArtifact(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("resourceUri")] string? ResourceUri);

/// <summary>Spec §5.12's <c>reports</c> section.</summary>
/// <param name="Html">
/// Spec §5.12's HTML report URI. <b>Omitted from the JSON, always, in this build</b> — the engine owns
/// where it writes its reports and this server is neither told nor asks. Upstream ask U4. Omitted
/// rather than null because §5.12 marks it optional and a null would read as "this server looked".
/// </param>
/// <param name="Junit">Spec §5.12's JUnit report URI. Omitted for the reason <see cref="Html"/> is.</param>
/// <param name="Events">
/// <b>Additive, and the only report artefact this build has.</b> The run's own JSON Lines event stream,
/// which the registry minted the path for — the file <c>explain_run</c>, <c>diagnose_run</c>,
/// <c>get_run_events</c> and <c>get_step_timeline</c> all read.
/// </param>
public sealed record RunReportArtifacts(
    [property: JsonPropertyName("html")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Html,
    [property: JsonPropertyName("junit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Junit,
    [property: JsonPropertyName("events")] RunEventsArtifact Events);

/// <summary>
/// Spec §5.12's <c>logs</c> entry shape. <b>Never constructed in this build</b> — declared so the empty
/// array has the element type U4 will fill, and so that landing adds values rather than a shape.
/// </summary>
/// <param name="Container">Which container the lines came from.</param>
/// <param name="Lines">The tailed lines, engine-redacted.</param>
/// <param name="Truncated">Whether the tail is shorter than the log.</param>
/// <param name="ResourceUri">A resource URI for the full log.</param>
public sealed record RunLogArtifact(
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("resourceUri")] string? ResourceUri);

/// <summary>One thing this result does NOT carry, named at the field it concerns, with the reason.</summary>
/// <param name="Field">
/// The dotted path of the missing field within this payload (e.g. <c>reports.junit</c>,
/// <c>environment.services</c>), so a host can match a gap to the field it was looking for rather than
/// to prose.
/// </param>
/// <param name="Reason">One sentence saying why this build cannot produce it.</param>
/// <param name="Awaits">
/// The upstream ask that would close the gap (<c>U4</c>), or <see langword="null"/> when the gap is a
/// local condition instead — a swept events file is not waiting on anything upstream.
/// </param>
public sealed record RunArtifactGap(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("awaits")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Awaits);

/// <summary>Spec §5.12's <c>GetRunArtifactsOutput</c>, minus the <c>meta</c> the result envelope stamps on.</summary>
/// <param name="RunId">The run this answer is about, echoed back — the registry's own recorded id, never the caller's string.</param>
/// <param name="Kind">
/// Which section(s) this result carries: the caller's <c>kind</c> normalised, or
/// <see cref="RunArtifactKind.All"/> when they omitted it. <b>Additive</b>, and kept because the
/// sections are OMITTED rather than emptied when they are not selected — without this a host cannot
/// distinguish "you did not ask for logs" from "there are no logs".
/// </param>
/// <param name="Partial">
/// <see langword="true"/> when this result is not everything spec §5.12's shape describes — computed as
/// "<see cref="Gaps"/> is non-empty", never hardcoded. See this file's header for why this tool carries
/// a payload-level marker where <c>get_step_timeline</c> deliberately does not.
/// </param>
/// <param name="Reports">
/// Spec §5.12's <c>reports</c>, present when <see cref="Kind"/> selected it and omitted otherwise.
/// </param>
/// <param name="Logs">
/// Spec §5.12's <c>logs</c>, present when <see cref="Kind"/> selected it and omitted otherwise.
/// <b>Always EMPTY when present</b>, which AC-002 requires be a success rather than an error: this
/// build has no container log access, and an empty array with a matching <see cref="Gaps"/> entry is
/// the honest shape. It is never fabricated into.
/// </param>
/// <param name="Environment">
/// Spec §5.12's <c>environment</c>, present when <see cref="Kind"/> selected it and omitted otherwise.
/// </param>
/// <param name="Container">
/// The <c>container</c> argument echoed back, sanitised and capped, or <see langword="null"/> when the
/// caller sent none. <b>It selected nothing.</b> The argument is accepted and validated today purely so
/// this tool's contract does not change when U4 lands — the same forward-compatibility posture
/// <see cref="TailLines"/> takes — and echoing it is what lets a host confirm the server understood the
/// request rather than having silently dropped a field.
/// </param>
/// <param name="TailLines">
/// The EFFECTIVE tail length: the caller's value, or <see cref="GetRunArtifactsOrchestrator.DefaultTailLines"/>
/// when they sent none. <b>Nothing is tailed with it in this build</b> — there are no log lines to tail
/// — but it is validated against
/// <see cref="GetRunArtifactsOrchestrator.MaxTailLines"/> and refused when out of range rather than
/// silently ignored, so the bound a host codes against today is the bound that will apply once U4 makes
/// it functional (US-S3-07 AC-003).
/// </param>
/// <param name="Gaps">
/// Every field this result could not populate, with its reason and (where one applies) the upstream ask
/// that would close it. <b>Additive, and the substance behind <see cref="Partial"/></b>: the boolean
/// says something is missing, this says what and why. Empty only if a future build can populate
/// everything §5.12 describes, in which case <see cref="Partial"/> is false.
/// </param>
public sealed record GetRunArtifactsResult(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("reports")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RunReportArtifacts? Reports,
    [property: JsonPropertyName("logs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RunLogArtifact>? Logs,
    [property: JsonPropertyName("environment")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RunEnvironmentArtifacts? Environment,
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("tailLines")] int TailLines,
    [property: JsonPropertyName("gaps")] IReadOnlyList<RunArtifactGap> Gaps);

/// <summary>
/// What one <c>get_run_artifacts</c> call produced — a closed union (the private constructor confines
/// derivation to the cases nested here), mirroring <see cref="GetStepTimelineOutcome"/> so the tool's
/// switch maps each case to exactly one <c>VFX-E-</c> code and the compiler enumerates the cases when
/// one is added.
/// </summary>
/// <remarks>
/// <b>Deliberately shorter than its siblings' unions, and the absences are the design.</b> There is no
/// <c>EventsFileNotFound</c> and no <c>EventsFileUnreadable</c> case here, although
/// <c>get_step_timeline</c> and <c>get_run_events</c> both have them over the same file. For those two
/// the file IS the answer, so an unreadable one leaves nothing to return; for an artefacts INVENTORY it
/// is one input of three, and stance (b) says a gap in derivable data comes back as a result with
/// <c>partial: true</c> rather than as an error. So a swept or unreadable stream is reported —
/// <c>reports.events.available: false</c> plus a <see cref="RunArtifactGap"/> naming it — and the other
/// sections still answer. What remains an error here is what is genuinely the caller's or the
/// environment's fault: a bad argument, an unknown run, or a path that fails the workspace containment
/// rule.
/// </remarks>
public abstract record GetRunArtifactsOutcome
{
    private GetRunArtifactsOutcome()
    {
    }

    /// <summary>The inventory was built — always a success, however little was derivable.</summary>
    public sealed record Found(GetRunArtifactsResult Result) : GetRunArtifactsOutcome;

    /// <summary>An argument was missing, blank, out of range, or not one of the accepted values — <c>VFX-E-1006</c>.</summary>
    public sealed record InvalidArgument(string Message) : GetRunArtifactsOutcome;

    /// <summary>No run with that id is in the registry — <c>VFX-E-1505</c>.</summary>
    public sealed record RunNotFound(string Message) : GetRunArtifactsOutcome;

    /// <summary>The run's recorded events path is a UNC location or escapes the workspace — <c>VFX-E-1001</c>.</summary>
    public sealed record InvalidPath(string Message) : GetRunArtifactsOutcome;
}
