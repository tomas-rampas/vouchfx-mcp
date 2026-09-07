using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Specs;

// Vouchfx.Mcp.Specs — vouchfx://workspace/specs models (Sprint 5 / US-S5-01 AC-005).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS RESOURCE IS FOR, AND THEREFORE WHAT IS IN IT
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Spec §6's purpose for it is stated in US-S5-01's own In-scope line: "an index of existing specs
// under specsDir, so hosts avoid duplicating scenarios". That single sentence fixes the shape. A
// host about to author a suite asks "does one of these already cover the flow I was asked about?",
// and the fields that answer it are the suite's NAME, its TAGS and the STEP TYPES it uses — enough
// to recognise overlap, and cheap enough to derive for every file in a directory. Anything more
// (assertions, environments, full summaries) would make this a bulk validate_suite, which is a tool
// call the host can already make once it has picked a candidate.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// "NO WORKSPACE CONFIGURED" IS A REPORTED STATE, NOT AN ERROR — sprint-00-overview.md §3 stance (b)
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// A server launched without --workspace has no specsDir at all (Workspace.cs: null is the
// full-fidelity legacy mode, not a degraded one). Two answers were available and the third is what
// ships:
//
//   * Throw. Rejected: reading a resource is how a host DISCOVERS what a server can do, and a
//     protocol error is a poor way to say "you did not pass a flag". It also makes the resource
//     unreadable rather than empty, which a host cannot distinguish from a broken server.
//   * Return an empty list. Rejected outright: indistinguishable from a workspace whose specsDir is
//     genuinely empty, which is a completely different situation with a completely different next
//     action.
//   * Return the empty list WITH `workspaceConfigured: false` and a `reason` saying which flag would
//     populate it. Ships. It is stance (b) applied to a resource: succeed with the derivable subset
//     and name the gap, never error and never fabricate.
//
// The same discriminator carries the other two "empty for a reason" cases — a specsDir that does not
// exist, and one that cannot be read — so `specs: []` is never ambiguous.

/// <summary>Why an index came back empty or short, when it did.</summary>
/// <remarks>
/// String constants rather than an enum for the reason every wire vocabulary in this server is
/// (see <c>RunArtifactKind</c>, <c>RunEnvironmentResourceRole</c>): the value crosses to a host as
/// JSON, and a stable literal is what a host branches on.
/// </remarks>
/// <remarks>
/// <b>There is deliberately no <c>"ok"</c> literal.</b> One existed briefly and was never emitted —
/// the complete-and-unremarkable case is signalled by <see cref="WorkspaceSpecIndex.Reason"/> being
/// ABSENT from the JSON entirely, which is what makes <c>specs: []</c> unambiguous (see this file's
/// header). A constant nothing writes is a second, contradictory answer to the same question waiting
/// for someone to use it.
/// </remarks>
public static class WorkspaceSpecIndexReasons
{
    /// <summary>This server was launched without <c>--workspace</c>, so there is no <c>specsDir</c> to index.</summary>
    public const string NoWorkspaceConfigured = "no-workspace-configured";

    /// <summary>A workspace is configured, but its <c>specsDir</c> does not exist on disk.</summary>
    public const string SpecsDirMissing = "specs-dir-missing";

    /// <summary>The <c>specsDir</c> exists but could not be enumerated (permissions, an I/O fault).</summary>
    public const string SpecsDirUnreadable = "specs-dir-unreadable";

    /// <summary>
    /// The directory holds more than <see cref="WorkspaceSpecIndexer.MaxSpecsIndexed"/> suites and the
    /// walk stopped there. Accompanies <see cref="WorkspaceSpecIndex.Truncated"/>, which is the field
    /// a host branches on; this says WHY it is set.
    /// </summary>
    public const string SpecLimitReached = "spec-limit-reached";

    /// <summary>
    /// The suites were FOUND but none could be examined, because the child process that parses them
    /// never ran or never produced output — an ENVIRONMENTAL failure of this machine, saying nothing
    /// about any suite.
    /// </summary>
    /// <remarks>
    /// <b>This exists so a broken worker is never reported as a workspace full of broken suites</b>
    /// (a review's finding: the first version collapsed "the worker could not start" into the same
    /// per-file "could not be parsed" text a genuine parse failure produced, so a machine that could
    /// not spawn a process published every suite in the directory as unparseable). It mirrors
    /// <c>ValidationWorkerClient</c>'s own split between <c>validation-worker-failed</c> and
    /// <c>validation-timeout</c>: the entries still appear, with a reason that blames no file, and
    /// THIS says what actually happened.
    /// </remarks>
    public const string SpecWorkerUnavailable = "spec-worker-unavailable";
}

/// <summary>One <c>.e2e.yaml</c> file found under the workspace's <c>specsDir</c>.</summary>
/// <param name="Path">
/// The file's path RELATIVE to <see cref="WorkspaceSpecIndex.SpecsDir"/>, with <c>/</c> separators
/// on every platform.
/// <para>
/// <b>Relative, and that is a disclosure decision as much as a usability one.</b> A host composes an
/// absolute path by joining it onto the <c>specsDir</c> this same payload carries, so nothing is
/// lost; but every entry in a possibly-long list then stops repeating the machine's directory
/// layout. Forward slashes on every platform because this is a wire identifier a host may echo into
/// a tool argument, and both separators resolve on Windows while only <c>/</c> resolves on Unix.
/// </para>
/// </param>
/// <param name="Name">
/// The suite's own <c>metadata.name</c>, sanitised and capped — or <see langword="null"/> when the
/// document declares none. Null is an ordinary state: <c>metadata</c> is optional in the schema.
/// </param>
/// <param name="Tags">
/// The suite's <c>metadata.tags</c>, in document order, each sanitised and capped. Empty when the
/// document declares none.
/// </param>
/// <param name="StepTypes">
/// The distinct <c>type</c> values the document's steps declare, in first-appearance order — taken
/// from the SAME <c>SuiteSummaryBuilder</c> walk <c>validate_suite</c>'s own <c>summary.stepTypes</c>
/// comes from, so the two can never disagree about what a step type is.
/// </param>
/// <param name="Steps">How many entries the document's top-level <c>steps</c> array has.</param>
/// <param name="Readable">
/// <see langword="false"/> when this file was found but could not be read or parsed — in which case
/// every field above except <see cref="Path"/> is null/empty and <see cref="ParseError"/> says why.
/// <b>The entry is still listed</b>: a host asking "what suites exist here" is better served by
/// "this one, and it is broken" than by silence, and a suite that fails to parse is exactly the one
/// worth knowing about before authoring a second covering the same flow.
/// </param>
/// <param name="ParseError">
/// One sanitised, capped sentence saying why <see cref="Readable"/> is false; omitted otherwise.
/// <b>Never the full diagnostic set</b> — that is <c>validate_suite</c>'s job, and this resource
/// deliberately does not become a bulk validator.
/// </param>
public sealed record WorkspaceSpecEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("stepTypes")] IReadOnlyList<string> StepTypes,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("readable")] bool Readable,
    [property: JsonPropertyName("parseError")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParseError);

/// <summary>The body <c>vouchfx://workspace/specs</c> serves.</summary>
/// <param name="WorkspaceConfigured">
/// Whether this server was launched with <c>--workspace</c>. <see langword="false"/> means
/// <see cref="Specs"/> is empty for a REASON that has nothing to do with what is on disk — see this
/// file's header.
/// </param>
/// <param name="SpecsDir">
/// The absolute <c>specsDir</c> that was indexed, sanitised and capped for display, or
/// <see langword="null"/> when no workspace is configured. Sanitised via the same
/// <c>PathSafetyGuard.CapAndSanitisePathForDisplay</c> <c>get_run_artifacts</c> applies to its own
/// path field, and carries that field's identical caveat: for a directory whose name contains
/// non-ASCII characters the string is not openable verbatim, because each becomes a literal
/// <c>\uXXXX</c> escape. It is for display and for composing the relative paths below, not for
/// blind concatenation into a filesystem call.
/// </param>
/// <param name="Specs">
/// Every <c>.e2e.yaml</c> found under <see cref="SpecsDir"/>, ordered ordinally by
/// <see cref="WorkspaceSpecEntry.Path"/> — <b>the relative, forward-slashed wire value itself</b>,
/// not the absolute filesystem path it was derived from. That distinction is load-bearing rather than
/// pedantic: on Windows <c>'\'</c> (0x5C) and <c>'/'</c> (0x2F) fall on opposite sides of every path
/// character in an ordinal comparison, so sorting the absolute form and publishing the relative one
/// produced a list whose published order was not the sorted order. Sorting the wire key is what makes
/// two reads of an unchanged directory produce byte-identical bodies on every platform, so a host's
/// cache is never invalidated by the filesystem's own enumeration order.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when what came back is not every suite under <see cref="SpecsDir"/> — the
/// SAME meaning <c>get_run_events.truncated</c>, <c>get_step_timeline.truncated</c> and
/// <c>list_runs.truncated</c> carry, deliberately named the same so a host learns one rule. Set when
/// the walk stopped at <see cref="WorkspaceSpecIndexer.MaxSpecsIndexed"/>.
/// <para>
/// <b>It does NOT cover a suite that failed to parse.</b> Such a suite is still LISTED, with
/// <see cref="WorkspaceSpecEntry.Readable"/> false and its own reason — the list is complete, one of
/// its entries is just thin. Folding that into this flag would tell a host that suites are missing
/// when none is.
/// </para>
/// </param>
/// <param name="Reason">
/// One of <see cref="WorkspaceSpecIndexReasons"/>' literals, or <b>omitted from the JSON entirely</b>
/// when the index is an ordinary complete answer — see that type's own remarks for why there is no
/// "ok" value. It explains an EMPTY or SHORT list; it never contradicts a populated one.
/// </param>
/// <param name="Detail">
/// One sanitised sentence expanding <see cref="Reason"/> into something actionable (which flag,
/// which directory), or omitted alongside it.
/// </param>
public sealed record WorkspaceSpecIndex(
    [property: JsonPropertyName("workspaceConfigured")] bool WorkspaceConfigured,
    [property: JsonPropertyName("specsDir")] string? SpecsDir,
    [property: JsonPropertyName("specs")] IReadOnlyList<WorkspaceSpecEntry> Specs,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail);
