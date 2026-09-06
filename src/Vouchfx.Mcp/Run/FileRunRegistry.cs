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
    /// The largest a single entry document may be — enforced on BOTH sides: <see cref="Persist"/>
    /// refuses to write one over it, and <see cref="ListRuns"/> skips one on disk that exceeds it
    /// rather than reading it into memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The READ side is the older half, and it exists because the registry directory is on a
    /// filesystem this server does not exclusively own — a bounded read at every boundary is this
    /// codebase's standing rule (see <c>EventsFileReader</c>, <c>BoundedStreamReader</c>), and an
    /// unbounded one here would let anything that could drop a large file into the output directory
    /// exhaust this process's memory through <c>explain_run</c>.
    /// </para>
    /// <para>
    /// <b>The WRITE side was added because the caller-side bounds do not in fact imply this one</b>
    /// (a gatekeeper/security review's finding, and the arithmetic here used to say otherwise). The
    /// caller-supplied parts are bounded at the tool boundary — at most
    /// <c>RunSuiteOrchestrator.MaxLabelCount</c> labels of bounded key/value length, and at most
    /// <c>SuitePathExpander.MaxExpandedPaths</c> spec paths totalling
    /// <c>SuitePathExpander.MaxExpandedPathCharacters</c> — but those are CHARACTER bounds, and this
    /// is a BYTE one. <c>JavaScriptEncoder.Default</c> (which <c>JsonSerializerDefaults.Web</c> uses)
    /// escapes every non-ASCII character to a six-byte <c>\uXXXX</c> sequence, so 24,000 characters
    /// of non-ASCII path text serialise to ~144 KB. For ordinary ASCII paths an entry really is a few
    /// kilobytes and never approaches this; for the non-ASCII case the write-side check is what turns
    /// "the run is recorded and then permanently invisible to every reader" into a refusal the caller
    /// is told about (<c>VFX-E-1502</c>, nothing run).
    /// </para>
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

    /// <summary>
    /// The inclusive year range a persisted <see cref="RunRegistryEntry.StartedAtUtc"/> must fall in
    /// for the entry to be considered usable; anything outside is skipped like any other corrupt
    /// entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a poisoning guard, not a plausibility nicety</b> (a security review's MINOR
    /// finding). <see cref="SeedStartedAtFloorFromDisk"/> seeds the monotonic floor from the NEWEST
    /// entry on disk, and <see cref="RunRegistryTimestamps.NextStartedAt"/> advances a floor it has
    /// caught up to with <c>floor.AddTicks(1)</c> — which THROWS
    /// <see cref="ArgumentOutOfRangeException"/> at <see cref="DateTimeOffset.MaxValue"/>. A single
    /// planted or corrupted <c>run.json</c> carrying that timestamp therefore made every subsequent
    /// <see cref="StartRun"/> fail with an uncoded framework exception — and, being on disk, it did so
    /// again after every restart. Bounding the value on READ is what turns that permanent, uncoded
    /// denial of service into one skipped entry.
    /// </para>
    /// <para>
    /// The range is deliberately generous rather than tight: this is not trying to decide whether a
    /// timestamp is CORRECT, only whether it is a run this software could have recorded. The lower
    /// bound predates the project by decades and the upper leaves seven millennia of headroom, so no
    /// legitimate entry — including one written on a host with a badly wrong clock — is at risk, while
    /// the arithmetic edge that actually bites is excluded by a wide margin. Applied in
    /// <see cref="ReadEntry"/>'s consistency clause for the reason every clause there exists: a file
    /// on disk is no more trusted than a caller.
    /// </para>
    /// </remarks>
    public const int MinPlausibleStartedAtYear = 2000;

    /// <inheritdoc cref="MinPlausibleStartedAtYear"/>
    public const int MaxPlausibleStartedAtYear = 9000;

    private static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Indented purely so a developer inspecting .vouchfx/runs by hand can read it; these
        // documents are small and never on a hot path, so the extra bytes cost nothing measurable.
        WriteIndented = true,
    };

    private readonly string _outputDirectory;
    private readonly int _maxRunsScanned;
    private readonly int _maxDirectoriesExamined;
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
    /// <exception cref="RunArtefactStorageException">
    /// <paramref name="workspace"/> was supplied and <paramref name="outputDirectory"/> does not
    /// resolve inside its root. An <see cref="ArgumentException"/> subtype, so the documented
    /// contract is unchanged for anyone catching the base type — see that type for why
    /// <c>Program.cs</c> needs to catch this case by name (a peer review's NIT).
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
        : this(outputDirectory, workspace, MaxRunsScanned, MaxDirectoriesExamined)
    {
    }

    /// <param name="maxRunsScanned">
    /// The run-directory scan cap this instance applies, defaulting to <see cref="MaxRunsScanned"/>.
    /// </param>
    /// <param name="maxDirectoriesExamined">
    /// The directory-entry cap this instance applies, defaulting to
    /// <see cref="MaxDirectoriesExamined"/>.
    /// </param>
    /// <remarks>
    /// <see langword="internal"/> purely as a TEST SEAM, mirroring
    /// <c>GetRunEventsOrchestrator.BuildPage</c>'s <c>maxLines</c> parameter and its reasoning: reaching
    /// <see cref="MaxRunsScanned"/> honestly costs ten thousand directory creations per case (and
    /// <see cref="MaxDirectoriesExamined"/> ten times that), which would add minutes to the suite to
    /// establish a boolean a small cap establishes exactly as well.
    /// <para>
    /// <b>Both caps are seams, and the second was added because its branch was otherwise unreachable
    /// from a test</b> (a gatekeeper review's minor finding). <see cref="EnumerateRunIds"/>' own
    /// documentation claimed "both caps are probed" while only the run cap had a seam, so the
    /// foreign-content branch — the one that exists precisely for a directory a legitimate workspace
    /// would not produce — was never exercised. Nothing outside this assembly can reach either
    /// parameter, and the production constructor above pins both real figures so no production path can
    /// accidentally take a different one.
    /// </para>
    /// </remarks>
    internal FileRunRegistry(
        string outputDirectory, Workspace? workspace, int maxRunsScanned, int maxDirectoriesExamined = MaxDirectoriesExamined)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRunsScanned, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDirectoriesExamined, 1);

        if (workspace is not null && PathSafetyGuard.CheckLocalPath(outputDirectory, workspace) is { } containmentError)
        {
            throw new RunArtefactStorageException(containmentError.Message, nameof(outputDirectory));
        }

        _outputDirectory = outputDirectory;
        _maxRunsScanned = maxRunsScanned;
        _maxDirectoriesExamined = maxDirectoriesExamined;
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
    public RunListing ListRuns()
    {
        var entries = new List<RunRegistryEntry>();
        var runIds = EnumerateRunIds(out var scanCapped);

        foreach (var runId in runIds)
        {
            if (ReadEntry(runId) is { } entry)
            {
                entries.Add(entry);
            }
        }

        // The cap is reported from the ENUMERATION, not from the entry count: a corrupt or unreadable
        // run.json is skipped by ReadEntry, so `entries.Count == MaxRunsScanned` is neither necessary
        // nor sufficient evidence that the walk stopped short. Only EnumerateRunIds knows.
        return new RunListing(RunRegistryCore.OrderMostRecentFirst(entries), scanCapped);
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
    /// Serialises <paramref name="entry"/>, refuses it if the serialised form exceeds
    /// <see cref="MaxEntryFileBytes"/>, and otherwise publishes it atomically — see this type's
    /// remarks, crash-safety layers 1 and 2.
    /// </summary>
    /// <exception cref="IOException">
    /// The serialised document is larger than <see cref="MaxEntryFileBytes"/>, i.e. larger than
    /// <see cref="ReadEntry"/> would ever read back.
    /// </exception>
    private void Persist(RunRegistryEntry entry)
    {
        var directory = RunDirectory(entry.RunId);
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(directory, EntryFileName);
        var temporaryPath = $"{finalPath}.tmp-{Guid.NewGuid():N}";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RunRegistryDocument(CurrentFormatVersion, entry), DocumentJsonOptions);

        // Enforced HERE, on the bytes, because this is the only place they exist: every upstream
        // bound is a character count, and the encoder's non-ASCII escaping means no character count
        // implies this one (see MaxEntryFileBytes). Writing a document the reader would then skip is
        // strictly worse than refusing it — the run would proceed, produce a verdict, and be invisible
        // to explain_run's default and to every later listing, with nothing anywhere saying why.
        //
        // IOException, DELIBERATELY, and not RunArtefactStorageException. That type derives from
        // ArgumentException, which RunSuiteOrchestrator's StartRun catch — explicitly scoped to
        // IOException/UnauthorizedAccessException/SecurityException so a genuine programming error
        // still surfaces as the bug it is — does NOT catch. Throwing it here would escape the
        // orchestrator uncoded, as a framework exception with a stack trace and no VFX code, which is
        // precisely the hole the taxonomy exists to close. IOException lands on the same catalogued
        // VFX-E-1502 "the run could not be recorded before it started, nothing was run" a full disk
        // does, which is also the honest description of this condition: the storage cannot hold this
        // entry.
        //
        // The check applies to the COMPLETING write too, where an entry within a few dozen bytes of
        // the cap at StartRun could be pushed past it by `outcome` and `finishedAt`. That refusal
        // lands in RunSuiteOrchestrator's guarded completing-write catch — the verdict is still
        // returned to the caller and the failure is announced on stderr — which is the same handling
        // any other storage fault at that point already gets, and strictly better than writing an
        // entry no reader accepts.
        if (bytes.Length > MaxEntryFileBytes)
        {
            throw new IOException(
                $"The run registry entry for '{entry.RunId}' serialises to {bytes.Length:N0} bytes, past "
                + $"the {MaxEntryFileBytes:N0}-byte limit a reader will accept. Nothing was written.");
        }

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
            //
            // The startedAt clause is the same doctrine applied to a NUMBER rather than a string, and
            // it is the one clause here that defends against an uncoded CRASH rather than a wrong
            // answer: see MinPlausibleStartedAtYear for the DateTimeOffset.MaxValue entry that
            // otherwise re-poisons StartRun on every restart.
            if (!string.Equals(entry.RunId, runId, StringComparison.Ordinal)
                || entry.Status is null
                || !RunRegistryStatus.IsKnown(entry.Status)
                || entry.EventsFilePath is null
                || !string.Equals(entry.EventsFilePath, MintedEventsFilePath(runId), PathSafetyGuard.PathComparison)
                || !RunRegistryCore.IsKnownOutcome(entry.Outcome)
                || (RunRegistryStatus.IsTerminal(entry.Status) && entry.Outcome is null)
                || entry.StartedAtUtc.UtcDateTime.Year is < MinPlausibleStartedAtYear or > MaxPlausibleStartedAtYear
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
    /// <b>Consumed INSIDE the try, deliberately</b> (a security review's MINOR finding).
    /// <see cref="Directory.EnumerateDirectories(string)"/> is lazy: its first filesystem call happens
    /// on the consumer's <c>MoveNext</c>, which — for a deferred query RETURNED from here — would be
    /// inside <see cref="ListRuns"/>'s <c>foreach</c>, OUTSIDE this method's catch. An output directory
    /// that became unreadable after the <see cref="Directory.Exists(string)"/> probe therefore threw
    /// straight through the guard that exists to absorb it. The <c>foreach</c> below is what puts every
    /// filesystem call the enumeration makes back under the catch.
    /// <para>
    /// <b>A running walk, NOT <c>.Take(cap + 1).ToList()</c></b> (a gatekeeper/security review's minor
    /// finding, restoring an earlier shape). The two-<c>ToList</c> version materialised up to
    /// <see cref="MaxDirectoriesExamined"/> + 1 path strings — 100,001 of them — before filtering any,
    /// so a workspace with a large output directory paid that allocation on every single
    /// <see cref="ListRuns"/> call, including the <see cref="SeedStartedAtFloorFromDisk"/> one on the
    /// first write. Counting as it goes holds only the run ids it keeps, and reaches the identical
    /// boundary semantics by the identical device: each cap is reported hit on ARRIVAL at the entry one
    /// PAST it, so a directory holding exactly the cap was enumerated in full and is NOT capped, while
    /// one holding cap + 1 is. Comparing a kept count against the cap instead would conflate those two.
    /// </para>
    /// <para>
    /// The two caps are applied in the order the walk meets them: <see cref="MaxDirectoriesExamined"/>
    /// BEFORE the run-id filter, so a directory holding a million foreign entries stops the
    /// enumeration rather than being traversed in full to find that none of them counted; then
    /// <see cref="MaxRunsScanned"/>, whose meaning ("how many RUNS one call will examine") is
    /// unchanged.
    /// </para>
    /// </remarks>
    /// <param name="scanCapped">
    /// Set when either cap stopped the walk before the directory was exhausted, so the returned ids may
    /// not be every run present. Reported to hosts through <see cref="RunListing.ScanCapped"/> and
    /// <c>list_runs</c>' <c>truncated</c> field.
    /// <para>
    /// <b>Detected by ARRIVING at the entry one past the cap</b> — the same "read one ahead to learn
    /// whether there is a next" device the two pagers use for their own cursors, expressed here as a
    /// running counter rather than a materialised extra element. Comparing the kept count against the
    /// cap instead would be wrong at exactly the boundary: a directory holding precisely
    /// <see cref="MaxRunsScanned"/> runs was enumerated in full and is not capped, yet counts
    /// identically to one holding 10,001. <b>BOTH caps are genuinely probed</b>, each by a test driving
    /// its own <see langword="internal"/> seam parameter (<c>maxRunsScanned</c> and
    /// <c>maxDirectoriesExamined</c>) — the second seam exists because this sentence previously claimed
    /// coverage the suite did not have: reaching the real 100,000-entry directory cap honestly is not
    /// something a test can afford, so without a seam that branch went unexercised. It matters
    /// separately from the first because <see cref="MaxDirectoriesExamined"/> can stop the walk over
    /// foreign content before <see cref="MaxRunsScanned"/> is ever approached, and a host's question
    /// ("is this all of them?") has the same answer either way.
    /// </para>
    /// </param>
    private List<string> EnumerateRunIds(out bool scanCapped)
    {
        scanCapped = false;

        try
        {
            if (!Directory.Exists(_outputDirectory))
            {
                return [];
            }

            var runIds = new List<string>();
            var directoriesExamined = 0;

            // Enumerated INSIDE the try for the reason this method's remarks give, and lazily: the
            // loop holds one path at a time plus the run ids it keeps, never a materialised copy of a
            // large directory. Each cap is reported on ARRIVAL at the entry one past it, so
            // exactly-at-the-cap is NOT capped — see the scanCapped parameter's own remarks.
            foreach (var directory in Directory.EnumerateDirectories(_outputDirectory))
            {
                if (directoriesExamined >= _maxDirectoriesExamined)
                {
                    scanCapped = true;
                    break;
                }

                directoriesExamined++;

                // Any directory whose name is not a well-formed run id is foreign content: it counts
                // against the directory cap (it was examined) and never against the run cap.
                if (Path.GetFileName(directory) is not { } name || !RunRegistryCore.IsWellFormedRunId(name))
                {
                    continue;
                }

                if (runIds.Count >= _maxRunsScanned)
                {
                    scanCapped = true;
                    break;
                }

                runIds.Add(name);
            }

            return runIds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An output directory that vanished or became unreadable between the probe and the
            // enumeration is an empty registry, not a failed tool call. SecurityException joins the
            // set to match PathSafetyGuard's own caught family: it is the CAS-era sibling of
            // UnauthorizedAccessException that some hosts still surface for a denied directory
            // enumeration, and catching one but not the other would make "the directory is not
            // readable" fatal or non-fatal depending on which type the platform happened to throw.
            //
            // scanCapped stays false here, and that is deliberate rather than an oversight: an
            // unreadable directory is not a capped scan, it is an absent one, and the honest report of
            // it is an empty registry — the same answer this catch has always produced. A host seeing
            // no runs at all is not at risk of believing it has seen all of a large set.
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
    /// <para>
    /// <b>The floor this seeds is only as trustworthy as the entry it comes from</b>, which is why
    /// <see cref="ReadEntry"/> bounds <see cref="RunRegistryEntry.StartedAtUtc"/> to
    /// <see cref="MinPlausibleStartedAtYear"/>..<see cref="MaxPlausibleStartedAtYear"/>. Without that
    /// bound a single entry carrying <see cref="DateTimeOffset.MaxValue"/> — planted, or corrupted
    /// into that shape — became the floor here, and <c>floor.AddTicks(1)</c> in
    /// <see cref="RunRegistryTimestamps.NextStartedAt"/> then threw
    /// <see cref="ArgumentOutOfRangeException"/> out of every <see cref="StartRun"/> for the life of
    /// the process, and of every process after it.
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
