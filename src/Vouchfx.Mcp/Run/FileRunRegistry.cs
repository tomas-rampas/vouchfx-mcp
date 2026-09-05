using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// The <see cref="IRunRegistry"/> used when the host launched this server with
/// <c>--workspace &lt;path&gt;</c>: one directory per run under the workspace's
/// <see cref="Workspace.OutputDir"/>, holding that run's metadata document and its events stream —
/// so a run recorded by one server process is still discoverable by the next one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage layout.</b> <c>&lt;outputDir&gt;/&lt;runId&gt;/run.json</c> for the metadata and
/// <c>&lt;outputDir&gt;/&lt;runId&gt;/events.jsonl</c> for the JSON Lines event stream.
/// <see cref="Workspace.OutputDir"/> is already <c>&lt;root&gt;/.vouchfx/runs</c>, so a run's
/// artefacts land at <c>&lt;root&gt;/.vouchfx/runs/&lt;runId&gt;/…</c> — the extra <c>runs/</c>
/// segment the story sketch showed would only have stuttered. The metadata lives INSIDE the run's
/// own directory rather than in a shared <c>registry/</c> sibling for two reasons: a host (or a
/// future retention sweep) can retire a run completely by removing one directory, with no chance of
/// orphaning metadata from artefacts; and there is no special directory name a run id could ever
/// shadow, since every run id is <c>run-</c> plus 32 hex characters
/// (<see cref="RunRegistryCore.IsWellFormedRunId"/>).
/// </para>
/// <para>
/// <b>Format choice: one file per run, NOT a shared JSON Lines log — and this is the decision
/// US-S3-04's cross-process lock inherits.</b> A single append-only log would have been less code,
/// but it makes every writer contend on ONE file: two server processes against the same workspace
/// would interleave partial lines into each other's appends (a write larger than the platform's
/// atomic-append guarantee is not atomic, and nothing here can assume one), and every reader would
/// have to replay the whole log to fold out each run's current state. One file per run has the
/// opposite profile: a write touches exactly one path that only its own run's writer ever names, so
/// two processes working on DIFFERENT runs never contend at all, and a reader needs no fold — the
/// file IS the current state. The consequence for US-S3-04 is the useful part: its <c>.lock</c> only
/// has to serialise the "a run is in progress in this workspace" CLAIM, and does not have to be
/// held across registry writes. Had this been a shared log, every single registry write would have
/// needed that lock, turning a coarse run-level mutex into a hot path on every status transition.
/// The cost paid for that is inode-per-run and an O(n) directory scan in
/// <see cref="ListRuns"/> (bounded by <see cref="MaxRunsScanned"/>), which is the right trade at the
/// scale a local developer workspace reaches.
/// </para>
/// <para>
/// <b>Crash safety: a torn write cannot exist, and an unreadable entry cannot poison its
/// neighbours.</b> Three layers, in order of what they defend against:
/// <list type="number">
/// <item><description>
/// <b>Atomic publish.</b> A write serialises the whole document to a sibling temp file
/// (<c>run.json.tmp-&lt;guid&gt;</c>) and then <see cref="File.Move(string, string, bool)"/>s it
/// over the real name. That replace is a single filesystem metadata operation, so a concurrent
/// reader observes either the complete previous document or the complete new one — never a
/// half-written prefix. Serialising directly onto <c>run.json</c> would have opened exactly that
/// window on every status transition.
/// </description></item>
/// <item><description>
/// <b>Crash residue is inert.</b> A process killed mid-write leaves an orphan
/// <c>run.json.tmp-*</c>; readers only ever open the exact file name <see cref="EntryFileName"/>,
/// so residue is invisible to them rather than something they must recognise and skip. The temp
/// name carries a GUID so two concurrent writers to the same entry cannot collide on it either.
/// </description></item>
/// <item><description>
/// <b>Per-entry fault isolation.</b> <see cref="ListRuns"/> reads each entry independently and skips
/// any that is missing, oversized (<see cref="MaxEntryFileBytes"/>), unparseable, of an unknown
/// <see cref="CurrentFormatVersion"/>, or internally inconsistent — returning every OTHER entry
/// normally. One damaged or hand-edited file therefore costs exactly one run's record, never the
/// registry. That is the property the acceptance criterion asks for: a torn or partial last write
/// must not poison reads of earlier entries. <b>"Internally inconsistent" includes a FORGED entry</b>
/// — see <see cref="ReadEntry"/>, which rejects any document whose <c>eventsFilePath</c> is not
/// exactly the path this registry would have minted for that run id, and any terminal entry carrying
/// no outcome. Those checks are DEFENCE IN DEPTH rather than the load-bearing anchor they once were:
/// <c>explain_run</c>'s containment exemptions have been retired, so containment now refuses a
/// foreign path on its own merits (see <c>ExplainRunOrchestrator.ExplainAsync</c>).
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Durability: what the atomic publish DOES and does not buy.</b> The rename in layer 1 defends
/// against exactly two observers — a CONCURRENT READER in this or another process, and a PROCESS
/// CRASH mid-write — for both of which the entry is all-or-nothing. It says nothing about POWER
/// LOSS: nothing here calls <c>fsync</c> on the file or on the containing directory, so a host that
/// loses power moments after a write can come back with the rename or the file's contents not yet
/// durable. That is a deliberate non-goal, not an oversight. Syncing on every status transition
/// would cost a device round trip per write to protect run BOOKKEEPING (the events file itself is
/// the engine's to flush), and layer 3 already degrades the worst case correctly: a half-durable
/// entry parses as garbage and is treated as an ABSENT run, which is a state every caller handles.
/// </para>
/// <para>
/// <b>Crash safety stops at the entry, not the run: a hard kill leaves a permanently
/// <see cref="RunRegistryStatus.Running"/> entry on disk, and nothing reaps it.</b> The completing
/// transition is written by <c>RunSuiteOrchestrator</c> after the run returns, so a server killed
/// with <c>SIGKILL</c>/<c>TerminateProcess</c> mid-run leaves that entry saying <c>running</c>
/// forever. There is no reaper, no lease, and no startup reconciliation pass in US-S3-01.
/// The consequence is bounded rather than silent:
/// <see cref="RunRegistryExtensions.MostRecentFinishedRun"/> filters non-terminal entries out, so
/// <c>explain_run</c> never defaults to such a phantom; it would, however, be visible to a future
/// <c>list_runs</c>. Reconciling (or expiring) an orphaned <c>running</c> entry is US-S3-03's
/// <c>get_run_status</c>/<c>list_runs</c> and US-S3-04's cross-process lock territory — those two
/// stories own the "is that run still alive?" question, and answering it here without their lock
/// would be a guess.
/// </para>
/// <para>
/// <b>Thread safety.</b> Writes are serialised on <see cref="_gate"/>, which is what makes a status
/// transition's read-modify-write of an entry safe against a concurrent transition. Reads take NO
/// lock and deliberately go to disk rather than to any in-memory mirror: layer 1 above already makes
/// a concurrently-published entry all-or-nothing, and a mirror would be a second source of truth
/// that could disagree with the files a restarted process reads. <b>The rename alone is not what
/// makes that lock-free read safe — the READER's share mode is.</b> On Windows a file opened without
/// <see cref="FileShare.Delete"/> cannot be renamed over while the handle is open, so a reader using
/// the default share mode would make a concurrent <see cref="File.Move(string, string, bool)"/> fail
/// with a sharing violation: <c>explain_run</c> reading an entry while <c>run_suite</c> published one
/// would have thrown out of <see cref="Persist"/>. <see cref="ReadEntry"/> therefore opens with
/// <c>FileShare.ReadWrite | FileShare.Delete</c>, which is what lets the publish proceed underneath
/// it. Atomicity also rests on the temp file and the final file being SIBLINGS in one directory,
/// hence on one volume: a cross-volume <see cref="File.Move(string, string, bool)"/> degrades to
/// copy-then-delete (<c>MOVEFILE_COPY_ALLOWED</c>), which is not atomic at all. The lock is per
/// PROCESS and makes no claim about a second server process against the same workspace — that is
/// US-S3-04's job, and the layout above is what keeps its scope small.
/// </para>
/// <para>
/// <b>Directory and file PERMISSIONS are the platform's defaults, on the record.</b> Nothing here
/// sets an ACL, a mode, or a <c>umask</c>: a run directory and its <c>run.json</c> inherit whatever
/// the workspace's own <c>.vouchfx</c> ancestry grants. For a local, single-user developer tool that
/// is the right default — the caller could read and write these files directly anyway — and
/// tightening them would fight the host's own conventions on a directory the host chose. It is
/// stated rather than assumed because the entries are metadata a second local account could read on
/// a shared machine; they carry no secret (see <see cref="IRunRegistry"/>'s metadata-only invariant),
/// which is what makes the default acceptable rather than merely convenient.
/// </para>
/// <para>
/// <b>This type is one of the very few in <c>src/</c> permitted to mutate the filesystem</b>
/// (<c>ReadOnlySourceGuardTests</c>'s fail-closed allow-list names it explicitly). The read-only
/// invariant is about never writing, modifying, or deleting a SUITE file or anything else the caller
/// named; everything written here is under the workspace's own output directory, is named by this
/// server, and is created by this server. No caller-supplied path ever reaches a write here — the
/// only caller-influenced value in a path is the run id, and that is minted here and shape-checked
/// on the way back in.
/// </para>
/// </remarks>
public sealed class FileRunRegistry : IRunRegistry
{
    /// <summary>The metadata document's file name inside a run's own directory.</summary>
    public const string EntryFileName = "run.json";

    /// <summary>The events stream's file name inside a run's own directory.</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>
    /// The on-disk format version written into every document and required, exactly, on read.
    /// </summary>
    /// <remarks>
    /// Required EXACTLY rather than "at most": a future version 2 that renames or re-means a field
    /// must not be silently misread by a server that only knows version 1. Skipping the entry
    /// instead degrades to "that run is not in the registry", which is a state every caller already
    /// handles, whereas misreading it would surface a wrong status or outcome as if it were fact.
    /// </remarks>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// The largest a single entry document may be before <see cref="ListRuns"/> skips it, rather
    /// than reading it into memory.
    /// </summary>
    /// <remarks>
    /// An entry is a few hundred bytes of metadata; 64 KB is orders of magnitude of headroom even
    /// once US-S3-02 starts recording labels and multiple spec paths. The cap exists because the
    /// registry directory is on a filesystem this server does not exclusively own — a bounded read
    /// at every boundary is this codebase's standing rule (see <c>EventsFileReader</c>,
    /// <c>BoundedStreamReader</c>), and an unbounded one here would let anything that could drop a
    /// large file into the output directory exhaust this process's memory through <c>explain_run</c>.
    /// </remarks>
    public const int MaxEntryFileBytes = 64 * 1024;

    /// <summary>
    /// The largest number of RUN directories — entries whose name is a well-formed run id —
    /// <see cref="ListRuns"/> will examine in one call.
    /// </summary>
    /// <remarks>
    /// Bounds the work an <c>explain_run</c> call can be made to do against a workspace holding a
    /// pathological number of run directories. Enumeration order is the filesystem's, so the cap is
    /// a denial-of-service bound rather than a "newest N runs" selection — reaching it at all means
    /// the workspace needs a retention sweep, which US-S3-03's <c>list_runs</c> paging is the natural
    /// place to introduce. Paired with <see cref="MaxDirectoriesExamined"/>, which bounds the OTHER
    /// half of the same walk.
    /// </remarks>
    public const int MaxRunsScanned = 10_000;

    /// <summary>
    /// The largest number of directory entries of ANY name <see cref="ListRuns"/> will look at,
    /// applied BEFORE the well-formed-run-id filter.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxRunsScanned"/> alone bounds only how many RUNS are examined, so a directory
    /// holding a million FOREIGN entries — an unrelated tool's scratch space, or a deliberate flood —
    /// would still be enumerated in full on every read, because none of those entries ever counts
    /// against that cap. This bounds the enumeration itself. It is deliberately an order of magnitude
    /// larger than <see cref="MaxRunsScanned"/>: the output directory is the server's own, so foreign
    /// content is the anomaly, and the looser bound means a workspace mixing a legitimate 10,000 runs
    /// with ordinary neighbours still reaches its run cap on merit rather than being cut off early by
    /// this one.
    /// </remarks>
    public const int MaxDirectoriesExamined = 100_000;

    private static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Indented purely so a developer inspecting .vouchfx/runs by hand can read it; these
        // documents are small and never on a hot path, so the extra bytes cost nothing measurable.
        WriteIndented = true,
    };

    private readonly string _outputDirectory;
    private readonly object _gate = new();
    private DateTimeOffset? _lastStartedAtUtc;
    private bool _startedAtSeeded;

    /// <param name="outputDirectory">
    /// The workspace's <see cref="Workspace.OutputDir"/>. <b>Consumed, never derived</b> — US-S3-08
    /// resolved it once at startup and the sprint's exit checklist forbids any story computing its
    /// own base directory, so this type takes it as an argument and does not know how it was built.
    /// It is not required to exist: it is created on the first <see cref="StartRun"/>, and every read
    /// treats an absent directory as an empty registry.
    /// </param>
    /// <param name="workspace">
    /// The workspace <paramref name="outputDirectory"/> is expected to belong to, when there is one.
    /// Supplying it turns on the containment check below; <see langword="null"/> (every test that
    /// points this type at a bare temp directory, and nothing in production) skips it.
    /// <b>Deliberately has no default value</b> (a peer review's finding): a parameter that decides
    /// whether a security check runs must be stated at every call site, so "no workspace" is a choice
    /// somebody wrote down rather than one they inherited by omission.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="workspace"/> was supplied and <paramref name="outputDirectory"/> does not
    /// resolve inside its root.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>The output directory is containment-checked against the workspace root, fail-closed, at
    /// CONSTRUCTION</b> (a security review's MINOR finding). This type is one of the very few allowed
    /// to write, and its whole licence to do so is "everything written here is under the workspace's
    /// own output directory". That sentence is an assumption unless something checks it: with
    /// <c>&lt;root&gt;/.vouchfx</c> replaced by a symlink or junction pointing elsewhere, every run
    /// directory, metadata document, and events file this server writes lands outside the tree the
    /// operator authorised — through a path the operator never typed. The check is
    /// <see cref="PathSafetyGuard.CheckLocalPath"/>, the SAME resolution machinery every
    /// caller-supplied path goes through (segment-by-segment link resolution iterated to a fixed
    /// point), rather than a second, weaker string comparison written here. Construction is the seam
    /// because it fails loudest: no run is started, no directory is created, and the failure is a
    /// startup fault rather than a per-call one.
    /// </para>
    /// <para>
    /// <b>The residual TOCTOU, stated as plainly as <see cref="PathSafetyGuard"/> states its own.</b>
    /// Containment is decided against the filesystem as it was at construction time; every write
    /// happens afterwards, so anything with write access to an ancestor of the output directory can
    /// swap a segment for a link in between and no path-based check can close that window (only
    /// opening a handle and validating it could). Accepted, for the same reason that type accepts it:
    /// an attacker with write access to the workspace's own <c>.vouchfx</c> ancestry can already
    /// write these files directly. What the check DOES buy is that a link planted BEFORE the server
    /// started — the realistic case, and the one an operator cannot see — is refused rather than
    /// silently honoured for the process's whole lifetime.
    /// </para>
    /// </remarks>
    public FileRunRegistry(string outputDirectory, Workspace? workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (workspace is not null && PathSafetyGuard.CheckLocalPath(outputDirectory, workspace) is { } containmentError)
        {
            throw new ArgumentException(containmentError.Message, nameof(outputDirectory));
        }

        _outputDirectory = outputDirectory;
    }

    /// <inheritdoc />
    public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(specPaths);

        lock (_gate)
        {
            SeedStartedAtFloorFromDisk();

            var runId = RunRegistryCore.MintRunId();
            var entry = RunRegistryCore.CreateStartedEntry(
                runId,
                MintedEventsFilePath(runId),
                RunRegistryTimestamps.NextStartedAt(ref _lastStartedAtUtc),
                specPaths,
                labels);

            Persist(entry);
            return entry;
        }
    }

    /// <inheritdoc />
    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_gate)
        {
            // Read-modify-write against DISK, not against a mirror: the entry being transitioned may
            // have been written by a previous server process (that is the whole point of persisting
            // it), so there is no in-memory copy to consult in the general case.
            var existing = ReadEntry(runId);
            if (existing is null)
            {
                return null;
            }

            var updated = RunRegistryCore.ApplyStatusTransition(existing, status, outcome);
            Persist(updated);
            return updated;
        }
    }

    /// <inheritdoc />
    public RunRegistryEntry? TryGetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return ReadEntry(runId);
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRegistryEntry> ListRuns()
    {
        var entries = new List<RunRegistryEntry>();

        foreach (var runId in EnumerateRunIds())
        {
            if (ReadEntry(runId) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return RunRegistryCore.OrderMostRecentFirst(entries);
    }

    /// <summary>The directory holding one run's metadata and artefacts.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="runId"/> is not the shape <see cref="RunRegistryCore.MintRunId"/> produces —
    /// see <see cref="RunRegistryCore.IsWellFormedRunId"/> for why that is checked before a run id
    /// is ever spliced into a path.
    /// </exception>
    private string RunDirectory(string runId)
    {
        if (!RunRegistryCore.IsWellFormedRunId(runId))
        {
            throw new ArgumentException(
                $"'{Contracts.VfxCode.SanitiseForEcho(runId)}' is not a well-formed run id.", nameof(runId));
        }

        return Path.Combine(_outputDirectory, runId);
    }

    /// <summary>
    /// The one and only events-file path this registry ever mints for <paramref name="runId"/> —
    /// written by <see cref="StartRun"/> and required, exactly, by <see cref="ReadEntry"/>.
    /// </summary>
    /// <remarks>
    /// A single definition on purpose: the read-side check is only a trust anchor if it compares
    /// against the SAME expression the write side used, and two copies of
    /// <c>Path.Combine(dir, runId, EventsFileName)</c> could drift the day the layout changes.
    /// </remarks>
    private string MintedEventsFilePath(string runId) => Path.Combine(RunDirectory(runId), EventsFileName);

    /// <summary>
    /// Serialises <paramref name="entry"/> and publishes it atomically — see this type's remarks,
    /// crash-safety layers 1 and 2.
    /// </summary>
    private void Persist(RunRegistryEntry entry)
    {
        var directory = RunDirectory(entry.RunId);
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(directory, EntryFileName);
        var temporaryPath = $"{finalPath}.tmp-{Guid.NewGuid():N}";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RunRegistryDocument(CurrentFormatVersion, entry), DocumentJsonOptions);

        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        catch
        {
            // Best-effort residue cleanup on the failure path only. The success path never needs it
            // (the Move consumed the temp file), and leaving residue behind is harmless anyway —
            // readers only ever open EntryFileName — so a failure to clean up must not mask the
            // original exception, which the caller genuinely needs to see.
            TryDeleteQuietly(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Reads one entry, returning <see langword="null"/> for every "this entry is not usable" case
    /// rather than throwing — see this type's remarks, crash-safety layer 3.
    /// </summary>
    private RunRegistryEntry? ReadEntry(string runId)
    {
        if (!RunRegistryCore.IsWellFormedRunId(runId))
        {
            return null;
        }

        var path = Path.Combine(_outputDirectory, runId, EntryFileName);

        try
        {
            // A cheap short-circuit for the overwhelmingly common "no such run" lookup, so that path
            // costs a stat rather than a thrown-and-caught DirectoryNotFoundException. The open below
            // is still the authority — a file that vanishes in between simply throws into the catch.
            if (!File.Exists(path))
            {
                return null;
            }

            // FileShare.Delete is LOAD-BEARING, not defensive tidiness: without it this open blocks a
            // concurrent Persist's File.Move over the same name on Windows (ERROR_SHARING_VIOLATION),
            // so explain_run reading an entry while run_suite published one would make the WRITER
            // throw. FileShare.ReadWrite lets that writer's own temp-file traffic proceed alongside.
            // See this type's thread-safety remarks: the reader's share mode is what makes the
            // lock-free read and the atomic publish coexist, not the rename by itself.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length > MaxEntryFileBytes)
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<RunRegistryDocument>(stream, DocumentJsonOptions);
            if (document is null || document.Version != CurrentFormatVersion || document.Run is not { } entry)
            {
                return null;
            }

            // A document whose contents contradict its own location, or whose required fields are
            // absent or outside their vocabulary, is treated exactly like an unparseable one. `null!`
            // is genuinely reachable here: System.Text.Json will happily bind a missing JSON property
            // to a non-nullable reference-typed record parameter, so the compile-time annotations on
            // RunRegistryEntry say nothing about what a hand-edited or truncated file contains.
            //
            // The eventsFilePath clause is a MINTED-PATH TRUST ANCHOR: this registry mints exactly one
            // events path per run id (MintedEventsFilePath), so any other value in a document on disk
            // is forged or foreign, and rejecting it routes that entry through layer-3 fault
            // isolation exactly like a corrupt one.
            //
            // It is DEFENCE IN DEPTH now, not load-bearing, and that demotion is deliberate. It used
            // to be the anchor an explain_run containment EXEMPTION rested on — a well-formed run.json
            // naming, say, C:\Users\me\.ssh\id_rsa would have turned that file into an exempt,
            // readable "events file". Those exemptions have been retired (see
            // ExplainRunOrchestrator.ExplainAsync), so containment now refuses such a path on its own
            // merits regardless of what any entry claims. The clause is kept because an entry whose
            // contents contradict its own location is not a usable record on any reading, and because
            // a check that costs one string comparison should not be removed merely because a second
            // line of defence appeared behind it.
            //
            // The outcome, terminal-outcome, and specPaths clauses close the symmetry with the write
            // side: RunRegistryCore rejects an outcome outside RunVerdict's names, a terminal status
            // carrying no outcome, and an empty or blank spec-path list on the way IN, and a file on
            // disk is no more trusted than a caller. Without the terminal-outcome clause a
            // hand-written `{"status":"completed","outcome":null}` would read back as a FINISHED run
            // with no verdict — which explain_run would then default to, and a future list_runs would
            // project as a run that ended saying nothing.
            if (!string.Equals(entry.RunId, runId, StringComparison.Ordinal)
                || entry.Status is null
                || !RunRegistryStatus.IsKnown(entry.Status)
                || entry.EventsFilePath is null
                || !string.Equals(entry.EventsFilePath, MintedEventsFilePath(runId), PathSafetyGuard.PathComparison)
                || !RunRegistryCore.IsKnownOutcome(entry.Outcome)
                || (RunRegistryStatus.IsTerminal(entry.Status) && entry.Outcome is null)
                || entry.SpecPaths is null
                || entry.SpecPaths.Count == 0
                || entry.SpecPaths.Any(string.IsNullOrWhiteSpace)
                || entry.Labels is null)
            {
                return null;
            }

            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A partially-visible file, a permissions problem, a document this server cannot parse:
            // all mean "no usable record for this run", never "fail the caller's tool call". The
            // caught set is explicit rather than a blanket catch so a genuine programming error
            // (an ArgumentException from a malformed path, say) still surfaces.
            return null;
        }
    }

    /// <summary>
    /// The run ids present under <see cref="_outputDirectory"/>, bounded on BOTH axes — at most
    /// <see cref="MaxDirectoriesExamined"/> directory entries looked at, of which at most
    /// <see cref="MaxRunsScanned"/> are run ids. Any directory whose name is not a well-formed run id
    /// is ignored, so the output directory can hold unrelated content without confusing the registry.
    /// </summary>
    /// <remarks>
    /// <b>Materialised INSIDE the try, deliberately</b> (a security review's MINOR finding). LINQ over
    /// <see cref="Directory.EnumerateDirectories(string)"/> is lazy: the first filesystem call happens
    /// on the consumer's <c>MoveNext</c>, which — with a deferred query returned from here — is inside
    /// <see cref="ListRuns"/>'s <c>foreach</c>, OUTSIDE this method's catch. An output directory that
    /// became unreadable after the <see cref="Directory.Exists(string)"/> probe therefore threw
    /// straight through the guard that exists to absorb it. Enumerating to a list here is what puts
    /// every filesystem call the query makes back under the catch.
    /// <para>
    /// The two caps are applied in the order the walk meets them: <see cref="MaxDirectoriesExamined"/>
    /// BEFORE the run-id filter, so a directory holding a million foreign entries stops the
    /// enumeration rather than being traversed in full to find that none of them counted; then
    /// <see cref="MaxRunsScanned"/>, whose meaning ("how many RUNS one call will examine") is
    /// unchanged.
    /// </para>
    /// </remarks>
    private List<string> EnumerateRunIds()
    {
        try
        {
            if (!Directory.Exists(_outputDirectory))
            {
                return [];
            }

            return
            [
                .. Directory.EnumerateDirectories(_outputDirectory)
                    .Take(MaxDirectoriesExamined)
                    .Select(Path.GetFileName)
                    .Where(RunRegistryCore.IsWellFormedRunId)
                    .Take(MaxRunsScanned)!
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An output directory that vanished or became unreadable between the probe and the
            // enumeration is an empty registry, not a failed tool call. SecurityException joins the
            // set to match PathSafetyGuard's own caught family: it is the CAS-era sibling of
            // UnauthorizedAccessException that some hosts still surface for a denied directory
            // enumeration, and catching one but not the other would make "the directory is not
            // readable" fatal or non-fatal depending on which type the platform happened to throw.
            return [];
        }
    }

    /// <summary>
    /// Seeds <see cref="_lastStartedAtUtc"/> from the newest entry already on disk, once per
    /// instance, so <see cref="RunRegistryTimestamps.NextStartedAt"/>'s strictly-increasing
    /// guarantee survives a server restart as well as holding within one process.
    /// </summary>
    /// <remarks>
    /// Without this, a fresh process starting a run within the system timer's ~15 ms resolution of
    /// the previous process's last run could stamp an EQUAL timestamp, leaving the two ordered by
    /// the random run-id tie-break — which is exactly the restart-survival case
    /// <c>explain_run</c>'s default-to-last-run behaviour is asserted against. Called under
    /// <see cref="_gate"/> from <see cref="StartRun"/> only: reads are unaffected by the floor, so
    /// paying a directory scan on the first WRITE (and never on a read) is the cheapest correct
    /// placement.
    /// <para>
    /// The "seeded" flag is set only AFTER the scan completes (a security review's MINOR finding):
    /// setting it first meant a scan that threw would still have marked the instance seeded, so the
    /// monotonic floor would be silently abandoned for the rest of the process's life on the strength
    /// of one transient failure. <see cref="ListRuns"/> swallows its own I/O faults today, which makes
    /// this ordering belt-and-braces rather than a live bug — but the flag's meaning ("the floor HAS
    /// been established") should be true when it is set, not merely usually true.
    /// </para>
    /// </remarks>
    private void SeedStartedAtFloorFromDisk()
    {
        if (_startedAtSeeded)
        {
            return;
        }

        // Runs most-recent-first, so the head is the floor. Unconditional assignment is correct
        // because this runs exactly once per instance, BEFORE this instance has stamped anything.
        if (ListRuns() is [var newest, ..])
        {
            _lastStartedAtUtc = newest.StartedAtUtc;
        }

        _startedAtSeeded = true;
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The on-disk envelope: a format version beside the entry itself, so a future layout change is
    /// detectable rather than silently misread. Deliberately separate from
    /// <see cref="RunRegistryEntry"/> so the domain model carries no storage concern.
    /// </summary>
    private sealed record RunRegistryDocument(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("run")] RunRegistryEntry Run);
}
