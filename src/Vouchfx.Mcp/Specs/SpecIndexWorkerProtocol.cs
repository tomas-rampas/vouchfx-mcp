using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Specs;

// Vouchfx.Mcp.Specs — the spec-index worker's wire contract (Sprint 5 / US-S5-01, B1 fix).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY vouchfx://workspace/specs NEEDS A WORKER AT ALL
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// The first implementation of this resource parsed each suite in-process, on the server's own
// request thread. That was wrong for the exact reason ValidationWorkerClient's remarks already
// record, and the reasoning applies here WITHOUT modification:
//
//   YamlDotNet's Scanner can be driven into an unbounded, ~100%-CPU, UNINTERRUPTIBLE spin by a
//   tiny well-formed input — a scalar `a: b` immediately followed by a MORE-indented `a: b`. It is
//   not a crash and not a slow parse. No Task/CancellationToken timeout can recover from it,
//   because the Scanner's loop has no cooperative cancellation point to observe. Only OS-level
//   process termination can.
//
// And critically: YamlSafetyGuard.CheckNestingDepth ITSELF runs the Scanner, so the guard is not a
// defence against this — the guard IS the spin. A single mis-indented twelve-byte .e2e.yaml sitting
// in a developer's own e2e/ directory therefore wedged this resource permanently, for the life of
// the server process. Reproduced against the Release build: YamlSafetyGuard.Check("a: b\n  a: b\n")
// had not returned after 12 seconds.
//
// So the parse moves across the same process boundary validate_suite already uses. What did NOT
// move is the enumeration and the containment check (Directory.EnumerateFiles, PathSafetyGuard):
// neither hands a byte to YamlDotNet, both need the SERVER's workspace configuration, and running
// them in the parent is what keeps the child ignorant of anything but the paths it was told to read.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY A BATCH WORKER, AND WHY IT STREAMS
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// One spawn per FILE would be correct and unusably slow: a 500-suite workspace would pay 500
// process starts per resource read. One spawn per BUILD is the right unit — but a single batch
// worker that only speaks at the end would lose all 500 results to one hostile file, which is the
// very failure this boundary exists to prevent.
//
// Hence the streaming shape: the worker writes ONE SELF-CONTAINED JSON LINE PER SUITE and flushes
// after each. Every line that reached the parent before a timeout is exactly as valid as it would
// have been had the worker finished. On a timeout the parent keeps those lines, marks the file the
// worker was on as timed out, and — within a total wall-clock budget — RESUMES from the file after
// it in a fresh process. One hostile suite therefore costs one timeout and one degraded entry, not
// the index.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY THE FILE LIST TRAVELS ON STDIN AS JSON
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Stdin for the reasons ValidationWorkerProtocol.InlineYamlArgument records at length (no file
// lifecycle to get wrong across a kill; nothing written to any filesystem; the handle is already
// redirected because a child must never inherit this server's real MCP stdin; no command-line
// length limit and nothing for a process listing to expose).
//
// JSON rather than one-path-per-line because a POSIX file name may legitimately contain a newline
// (`a\nb.e2e.yaml` is a valid Linux filename), and line framing would then silently split one path
// into two — handing the child a path the parent never containment-checked. A JSON array has no
// such failure mode.
//
// The child emits NO PATH of its own, ever: entries are keyed by the INDEX of the input array, and
// the parent attaches the (relative, sanitised) path it already computed. That keeps every path
// rendering decision in the process that owns the workspace, and means a compromised or buggy child
// cannot introduce a path into the response at all.

/// <summary>
/// The wire contract between <see cref="SpecIndexWorkerClient"/> and the hidden
/// <c>--spec-index-worker</c> child-process mode it spawns (see <c>Program.cs</c>).
/// </summary>
public static class SpecIndexWorkerProtocol
{
    /// <summary>
    /// The command-line argument that switches the vouchfx-mcp executable into its hidden, one-shot
    /// spec-index worker mode. Checked in <c>Program.cs</c> alongside
    /// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerProtocol.WorkerModeArgument"/>, before the
    /// ENGINE_PIN load or any MCP host bootstrap — this mode needs neither.
    /// </summary>
    /// <remarks>
    /// Takes NO positional argument: everything the worker needs is the JSON array of absolute paths
    /// on its stdin. That is deliberate — it means there is no argument position a path could be
    /// mistaken for, and therefore no analogue of
    /// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerProtocol.InlineYamlArgument"/>'s in-band
    /// discriminator collision to guard against.
    /// </remarks>
    public const string WorkerModeArgument = "--spec-index-worker";

    /// <summary>
    /// The most paths one worker invocation will accept on stdin. Matches
    /// <see cref="WorkspaceSpecIndexer.MaxSpecsIndexed"/>, because the parent never sends more than
    /// it will publish; enforced on the CHILD side too, so a worker run by hand cannot be pointed at
    /// an unbounded list.
    /// </summary>
    public const int MaxPaths = WorkspaceSpecIndexer.MaxSpecsIndexed;

    /// <summary>
    /// An upper bound on the stdin payload the worker will buffer: <see cref="MaxPaths"/> paths, each
    /// bounded by the platform's own path limit, with JSON quoting and escaping overhead — rounded
    /// generously upward. Bounds what a hostile or buggy parent can make the child allocate, the same
    /// role <c>Program.ReadInlineYaml</c>'s own limit plays.
    /// </summary>
    /// <remarks>
    /// <b>Applied as a CHARACTER count</b> by <c>Program.ReadBoundedStandardInput</c>, which is the
    /// stricter reading for any non-ASCII payload and therefore the safe direction for a memory guard.
    /// The name says "bytes" because that is the budget being reasoned about when sizing it; see that
    /// function's own comment for why the units are stated rather than silently conflated (an earlier
    /// revision had three different names for this one number).
    /// </remarks>
    public const int MaxStandardInputBytes = 4 * 1024 * 1024;
}

/// <summary>
/// One suite's parse result, as the worker writes it — one of these per line on stdout, flushed
/// after each.
/// </summary>
/// <param name="Index">
/// The position of this suite in the input path array. <b>The only identity a worker entry carries</b>
/// — the child never emits a path, so the parent joins on this. See this file's header.
/// </param>
/// <param name="Name">The suite's <c>metadata.name</c>, or <see langword="null"/> when it declares none.</param>
/// <param name="Tags">The suite's <c>metadata.tags</c>, in document order.</param>
/// <param name="StepTypes">
/// The distinct step <c>type</c> values, in first-appearance order — from the SAME
/// <c>SuiteSummaryBuilder</c> walk <c>validate_suite</c>'s own <c>summary.stepTypes</c> comes from,
/// so a suite cannot be described two ways by two surfaces.
/// </param>
/// <param name="Steps">How many entries the document's top-level <c>steps</c> array has.</param>
/// <param name="Readable">
/// <see langword="false"/> when the file was found but could not be read or parsed, in which case
/// <paramref name="ParseError"/> says why in one sentence.
/// </param>
/// <param name="ParseError">One sanitised, capped sentence; <see langword="null"/> when readable.</param>
/// <remarks>
/// <para>
/// Deliberately NOT <see cref="WorkspaceSpecEntry"/> itself: that type carries the <c>path</c> the
/// worker must never produce. Keeping them separate is what makes "the child cannot introduce a path"
/// a property of the types rather than of a code review.
/// </para>
/// <para>
/// <b>The two collections are typed NULLABLE, and that is honesty about deserialisation rather than
/// laxity</b> (a security review's finding). This record is materialised by
/// <see cref="System.Text.Json"/> from a line of text produced by another process, and a
/// syntactically valid <c>{"index":0}</c> that simply omits them leaves both
/// <see langword="null"/> — a non-nullable declaration would not have prevented that, only hidden it
/// until the null reached serialisation of the response. Declaring the truth is what lets
/// <c>SpecIndexWorkerClient.Sanitise</c> be the ONE place that normalises them, on receipt, for every
/// field at once.
/// </para>
/// </remarks>
public sealed record SpecIndexWorkerEntry(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("stepTypes")] IReadOnlyList<string>? StepTypes,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("readable")] bool Readable,
    [property: JsonPropertyName("parseError")] string? ParseError);
