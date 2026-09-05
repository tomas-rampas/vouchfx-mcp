using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// Spec §4.6's per-workspace run lock: an exclusively-held OS file handle on
/// <c>&lt;outputDir&gt;/.lock</c>, taken for the duration of one run and released when it ends —
/// including when the holding PROCESS dies without releasing anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism: the HANDLE is the lock; the file is only where the handle lives.</b>
/// <see cref="TryAcquire"/> opens <c>&lt;outputDir&gt;/.lock</c> with
/// <see cref="FileShare.None"/>. That share mode is the whole mechanism, and it is enforced by the
/// operating system rather than by anything written here:
/// <list type="bullet">
/// <item><description>
/// <b>Windows</b> share modes are mandatory and kernel-enforced. A second
/// <see cref="FileShare.None"/> open of the same path — from this process or any other — fails with
/// <see cref="IOException"/> (a sharing violation) while the first handle lives. When the holding
/// process exits, cleanly or by <c>TerminateProcess</c>, the kernel closes its handles, which drops
/// the share reservation and with it the claim. The FILE stays where it is on every path — nothing
/// deletes it any more (see the <see cref="FileOptions.None"/> paragraph below) — and there is
/// nothing to reclaim, because no part of the claim was ever recorded in it.
/// </description></item>
/// <item><description>
/// <b>Unix</b> has no mandatory share modes, so .NET maps <see cref="FileShare.None"/> onto an
/// advisory <c>flock(LOCK_EX | LOCK_NB)</c> — and <see cref="FileShare.None"/> specifically, which is
/// why it is spelled that way here rather than as the more permissive
/// <see cref="FileShare.Read"/>: every other share value maps to a SHARED lock, and two shared locks
/// do not conflict, so a "readable" lock file would have let both processes believe they had the
/// claim. A conflicting open fails with <see cref="IOException"/>, exactly as on Windows. <c>flock</c>
/// records are owned by the open file description and are released BY THE KERNEL when the process
/// dies, so a killed holder's claim evaporates there too.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>A LINK at the lock path is refused outright, before the open</b> (a security review's BLOCKER,
/// with a measured exploit). <c>.lock</c> sits at a fixed, well-known name inside a directory that is
/// routinely part of a cloned or shared tree, so an attacker — or a careless <c>git checkout</c> of a
/// repository someone else authored — can pre-plant a symbolic link there and
/// <see cref="FileMode.OpenOrCreate"/> will follow it. Two consequences were MEASURED by that review:
/// with <see cref="FileOptions.DeleteOnClose"/> — which this type requested on Windows at the time —
/// the release then DELETED THE LINK'S TARGET, turning this server's cleanest path into an
/// arbitrary-file-delete primitive; and a DANGLING link makes <see cref="FileMode.OpenOrCreate"/>
/// CREATE the attacker's chosen path instead of the lock file. The first of those is now gone by
/// construction — <see cref="FileOptions.None"/> is passed on both platforms, so a followed link can
/// at worst yield an exclusive handle on the target and, when dangling, create it — but the probe
/// stays: holding an exclusive handle on a file the operator never named, and creating a path of the
/// attacker's choosing, are both worth refusing, and the check costs one metadata probe.
/// <see cref="TryAcquire"/> therefore calls
/// <see cref="File.ResolveLinkTarget(string, bool)"/> first and reports
/// <see cref="RunLockResult.Unavailable"/> for anything that is a link at all. Refusing the whole
/// class of REPARSE POINTS, rather than resolving the target and containment-checking it, is
/// deliberate: containment would still permit a link aimed at a legitimate file INSIDE the workspace
/// (a suite, a previous run's <c>run.json</c>) and would have left that delete primitive pointed at
/// the operator's own data, and it is the only form of the check that also protects the
/// <c>workspace: null</c> construction, where there is no root to contain anything against. HARD
/// links were considered and are deliberately outside that class:
/// <see cref="File.ResolveLinkTarget(string, bool)"/> returns <see langword="null"/> for one, so this
/// probe cannot see it, and the consequence is benign — a hard link is a second NAME for one inode,
/// so the target's other name keeps the data whatever happens here — while the shape itself is
/// bounded, since a hard link can neither cross volumes nor point at a directory. Nothing legitimate
/// is lost — this server creates <c>.lock</c> itself and no supported configuration asks for it to be
/// a link. Like the constructor's containment check below, what this buys is refusal of a link
/// planted BEFORE the server started; one planted between the probe and the open is the same
/// post-startup-write-access residual that check's remark accepts, and losing that race is now
/// non-destructive — an exclusive handle, or a created path for a dangling link, never a deletion.
/// </para>
/// <para>
/// <b>Staleness story: there is no stale-lock detection here, and that is the design, not an
/// omission.</b> A pid written into a file and probed later is the classic alternative, and it is
/// wrong in three ways this mechanism simply does not have: a pid is reused by the OS, so "that
/// process still exists" can be true of a completely unrelated program; the probe is a race (the
/// holder can die between the probe and the reclaim); and the reclaim itself needs its own mutual
/// exclusion, i.e. the very thing being built. Delegating to the kernel's own handle/lock lifetime
/// removes all three: a lock exists exactly as long as a live process holds a handle, and the
/// question "is the holder still alive?" is answered by the OS at the moment of asking rather than
/// inferred from a file's contents.
/// </para>
/// <para>
/// <b><see cref="FileOptions.None"/> is passed on BOTH platforms — there is no
/// <see cref="FileOptions.DeleteOnClose"/> anywhere here any more, and the symmetry is the point.</b>
/// On Unix it is MANDATORY. .NET has no kernel flag to use there and EMULATES the option in the
/// dispose path — an ordinary <c>unlink</c> — and a gatekeeper review MEASURED that emulation
/// breaking mutual exclusion in two independent ways. First, ordering: the emulated <c>unlink</c> is
/// emitted BEFORE <c>flock(LOCK_UN)</c> (strace-measured), so between those two syscalls the path is
/// free while the lock is still held, and a contender that creates a fresh file at the same name gets
/// a DIFFERENT inode with no <c>flock</c> record on it — 7487 overlapping acquisitions were observed
/// across 621k contended attempts. Second, and worse, the same hole is reachable without any race at
/// all: <c>flock</c> records belong to the inode, and unlinking a held, flock'd file lets the next
/// taker create a new inode and acquire it while the first holder still believes it has the claim.
/// On Windows the flag was SAFE — a real kernel flag (<c>FILE_FLAG_DELETE_ON_CLOSE</c>) carried by
/// the same handle whose closure drops the share reservation, with the unlink refused while any
/// other handle holds the file — but it was justified only COSMETICALLY, so that a human inspecting a
/// quiet workspace would not find a file that looks like a live run. A security review priced that
/// cosmetic benefit against two costs and it lost twice. It is a DELETION PRIMITIVE, and the
/// difference is measured rather than argued: for a victim file reached through a link that wins the
/// probe→open race, the victim SURVIVES WITH ITS CONTENT under <see cref="FileOptions.None"/> and is
/// GONE under <see cref="FileOptions.DeleteOnClose"/> — the flag is the whole distance between losing
/// that race harmlessly and losing it destructively. It also brought a transient of its own: while
/// the last handle is closing, Windows leaves the name in a DELETE-PENDING state where a fresh open
/// is refused with something that is not <c>ERROR_SHARING_VIOLATION</c> (the status surfaces as an
/// access denial), so the failure-mapping rule below routed a contender arriving at an ordinary
/// hand-off to <see cref="RunLockResult.Unavailable"/>/<c>VFX-E-1502</c> — "this workspace cannot
/// host a run" — for a momentary, retryable overlap. Dropping the flag removes the primitive, the
/// transient, and the entire platform asymmetry in one edit: the file is created once and then
/// PERSISTS, permanently, everywhere, and the claim is only ever the OS's own record against that one
/// handle (Windows) or inode (Unix).
/// </para>
/// <para>
/// <b>The persisting file is INERT, on every platform — it is not "residue" to be cleaned up, it is
/// the design.</b> The reason is the first paragraph: acquisition never asks whether the file exists
/// (there is no <see cref="File.Exists(string)"/> anywhere in this type), only whether the OS will
/// grant an exclusive handle on it. <see cref="FileMode.OpenOrCreate"/> opens an existing
/// <c>.lock</c> and locks it just as happily as it creates a fresh one, so the next run proceeds —
/// which is how the acceptance criterion ("a stale lock from a process that no longer exists is
/// detected and reclaimed, not left permanently blocking") is met: by making the stale state
/// unobservable rather than by handling it. This holds identically for a file left by a clean
/// release and one left by a <c>SIGKILL</c>ed or <c>TerminateProcess</c>ed holder, which is the
/// point — there is no longer a state machine here with two cases in it, nor two platforms with
/// different ones. <c>WorkspaceRunLockTests</c> plants an existing <c>.lock</c> by hand and asserts
/// acquisition proceeds, and asserts the persistence after a clean release, on whatever platform the
/// suite runs.
/// </para>
/// <para>
/// <b>The exclusivity claims are MEASURED on Linux, not inferred</b> (probe run 2026-09-05 under
/// WSL Ubuntu 22.04, a <c>net8.0</c> assembly on the .NET 9 runtime; the same share mode this method
/// uses). In order: a second handle in the SAME process is refused with <see cref="IOException"/>; a
/// handle in another PROCESS while the first is held is refused with <see cref="IOException"/>; and
/// after <c>SIGKILL</c>ing the holder the file SURVIVES while the next acquisition SUCCEEDS. The
/// middle one is the exclusivity and crash-safety this design rests on; the last is the persistence
/// described above, confirmed to be exactly as inert as claimed. The first matters because it is not
/// obvious: <c>flock</c> conflicts are owned by the open file description rather than by the process,
/// so two <see cref="FileStream"/>s in one process contend with each other just as two processes do.
/// That probe also measured .NET's <see cref="FileOptions.DeleteOnClose"/> emulation removing the
/// file on a clean dispose — which is exactly the behaviour the gatekeeper's later, deeper probe
/// showed to be unsafe, and which this type consequently no longer asks for on any platform.
/// </para>
/// <para>
/// <b>The lock file's CONTENT is deliberately empty — the run registry is what names the active
/// run.</b> Spec §4.6 requires the rejection to carry the active <c>runId</c>, and the obvious
/// implementation (write it into the lock file) cannot work against a
/// <see cref="FileShare.None"/> handle: the exclusivity that makes the lock a lock is the same thing
/// that stops the rejected process from reading it. Rather than weaken the share mode, or add a
/// second sibling file that could disagree with the first, the id comes from the registry entry the
/// holder writes immediately after acquiring — same <c>&lt;outputDir&gt;</c>, same code path, one
/// authority (see <see cref="RunSuiteOrchestrator"/>, which does that lookup and documents the one
/// window in which it can come back empty).
/// </para>
/// <para>
/// <b>Failure mapping: <see cref="RunLockResult.HeldByAnotherRun"/> is claimed only for the
/// conditions that genuinely mean contention.</b> An earlier version reported EVERY
/// <see cref="IOException"/> from the open as contention, on the reasoning that both it and
/// <see cref="RunLockResult.Unavailable"/> map to <c>retryable: true</c> codes so the host's next
/// action is the same. A security review showed that reasoning to be wrong in the direction that
/// matters — the two codes read very differently to an operator, and the plantable conditions that
/// reached the contention arm were PERMANENT ones dressed up as a transient wait, complete with the
/// (possibly stale) <c>runId</c> of a run that has nothing to do with the failure. Two were measured:
/// a dangling symlink whose target's parent does not exist throws
/// <see cref="DirectoryNotFoundException"/>, and a symlink to a file some unrelated application holds
/// <see cref="FileShare.None"/> throws a plain <see cref="IOException"/>. The link paragraph above
/// removes that whole class before the open; this mapping is the second layer, for anything else that
/// arrives by the same shape:
/// <list type="bullet">
/// <item><description>
/// <see cref="DirectoryNotFoundException"/>, <see cref="FileNotFoundException"/>,
/// <see cref="PathTooLongException"/> and <see cref="DriveNotFoundException"/> — all
/// <see cref="IOException"/> subtypes, none of them a statement about concurrency — are
/// <see cref="RunLockResult.Unavailable"/>. They name a location that cannot host a run, which is
/// what <c>VFX-E-1502</c> already means.
/// </description></item>
/// <item><description>
/// <b>On Windows</b> a bare <see cref="IOException"/> is contention only when
/// <see cref="Exception.HResult"/>'s Win32 half is <c>ERROR_SHARING_VIOLATION</c> (32) or
/// <c>ERROR_LOCK_VIOLATION</c> (33) — the two codes the kernel produces for exactly this. Anything
/// else is <see cref="RunLockResult.Unavailable"/>.
/// </description></item>
/// <item><description>
/// <b>On Unix</b> a refused <c>flock</c> surfaces as a bare <see cref="IOException"/> with no
/// portable distinguishing code, so a bare <see cref="IOException"/> there remains
/// <see cref="RunLockResult.HeldByAnotherRun"/>. The residual over-claim is a genuine device error
/// on the same path — narrower than what it replaced, and the direction that keeps a real contention
/// from being reported as a broken workspace.
/// </description></item>
/// </list>
/// A denied ACL (<see cref="UnauthorizedAccessException"/>, which is also what Windows raises for a
/// DIRECTORY or a read-only file sitting at <c>.lock</c>) is
/// <see cref="RunLockResult.Unavailable"/> for the same reason it always was: a permission problem is
/// the one case that will never clear on its own and deserves to be named.
/// </para>
/// <para>
/// <b>This type may write, and is named in <c>ReadOnlySourceGuardTests</c>' fail-closed allow-list
/// for exactly the reason <see cref="FileRunRegistry"/> is.</b> The read-only invariant is that this
/// server never writes, modifies, or deletes a SUITE file — or anything else a caller named. Nothing
/// caller-supplied reaches a path here at all: <c>.lock</c> is a fixed literal under the output
/// directory US-S3-08 resolved from the operator's own <c>--workspace</c> flag, and that directory is
/// containment-checked against the workspace root at construction (below) before a single byte is
/// created.
/// </para>
/// </remarks>
public sealed class WorkspaceRunLock : IRunLock
{
    /// <summary>The lock file's name inside the workspace's output directory — spec §4.6's literal.</summary>
    public const string LockFileName = ".lock";

    /// <summary><c>ERROR_SHARING_VIOLATION</c> — Windows' "another process has this file open".</summary>
    private const int ErrorSharingViolation = 32;

    /// <summary><c>ERROR_LOCK_VIOLATION</c> — Windows' "a byte-range lock refuses this access".</summary>
    private const int ErrorLockViolation = 33;

    private readonly string _outputDirectory;

    /// <param name="outputDirectory">
    /// The workspace's <see cref="Workspace.OutputDir"/>. <b>Consumed, never derived</b> — the same
    /// rule <see cref="FileRunRegistry"/> follows, and the sprint's exit checklist forbids a story
    /// computing its own base directory. Not required to exist: it is created on first acquisition,
    /// exactly as the registry creates it on the first run it records.
    /// </param>
    /// <param name="workspace">
    /// The workspace <paramref name="outputDirectory"/> is expected to belong to, when there is one.
    /// Supplying it turns on the containment check below; <see langword="null"/> (tests pointing this
    /// type at a bare temp directory, and nothing in production) skips it. <b>Deliberately has no
    /// default value</b>, mirroring <see cref="FileRunRegistry"/>: a parameter that decides whether a
    /// security check runs must be stated at every call site.
    /// </param>
    /// <exception cref="RunArtefactStorageException">
    /// <paramref name="workspace"/> was supplied and <paramref name="outputDirectory"/> does not
    /// resolve inside its root.
    /// </exception>
    /// <remarks>
    /// The containment check is <see cref="PathSafetyGuard.CheckLocalPath"/> — the same
    /// segment-by-segment link resolution every caller-supplied path goes through, not a weaker
    /// string comparison written here — for the same reason <see cref="FileRunRegistry"/> runs it:
    /// with <c>&lt;root&gt;/.vouchfx</c> replaced by a link, this type would create and delete a file
    /// outside the tree the operator authorised. Its residual TOCTOU is
    /// <see cref="FileRunRegistry"/>'s, unchanged and accepted for the same reason: anything able to
    /// swap a segment of the workspace's own <c>.vouchfx</c> ancestry can already write there
    /// directly, and what the check buys is refusal of a link planted BEFORE the server started.
    /// </remarks>
    public WorkspaceRunLock(string outputDirectory, Workspace? workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (workspace is not null && PathSafetyGuard.CheckLocalPath(outputDirectory, workspace) is { } containmentError)
        {
            throw new RunArtefactStorageException(containmentError.Message, nameof(outputDirectory));
        }

        _outputDirectory = outputDirectory;
        LockFilePath = Path.Combine(outputDirectory, LockFileName);
    }

    /// <summary>
    /// The absolute path of the file whose exclusive handle IS the lock — spec §4.6's
    /// <c>&lt;outputDir&gt;/.lock</c>.
    /// </summary>
    /// <remarks>
    /// Exposed so tests can assert the location the spec names, and so the cross-process test fixture
    /// can hold the REAL lock rather than a copy of its open flags. Nothing in <c>src/</c> reads this
    /// property; the lock is always taken through <see cref="TryAcquire"/>.
    /// </remarks>
    public string LockFilePath { get; }

    /// <inheritdoc />
    public RunLockResult TryAcquire()
    {
        try
        {
            // The output directory is created lazily, here and in FileRunRegistry.Persist, rather
            // than at workspace resolution: Workspace.Resolve is pure path computation plus one
            // existence probe and creates nothing, so a host that merely passes --workspace and never
            // runs anything gets no directories it did not ask for.
            Directory.CreateDirectory(_outputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A file sitting where the directory should be, a read-only mount, a denied ACL. None of
            // these is "a run is in progress", and reporting one as such would have a host poll a
            // location that will never accept a run.
            return new RunLockResult.Unavailable(ex);
        }

        try
        {
            // SECURITY (a review's BLOCKER, with a measured exploit): refuse a link at the lock path
            // BEFORE opening it — see this type's remarks. Deliberately inside this try, so that a
            // probe failing for a reason of its own is classified by the same arms below rather than
            // escaping uncoded.
            if (IsLinkAtLockPath())
            {
                // Message-safe by construction: no path, no resolved target. Only the exception's
                // TYPE ever leaves this process (RunLockResult.Unavailable's own remarks), and the
                // operator-facing guidance for "something is planted at .lock" lives on the
                // VFX-E-1502 catalogue page rather than in a string built here.
                return new RunLockResult.Unavailable(
                    new IOException("The run lock path is a link; refusing to open it."));
            }

            // FileShare.None is the whole mechanism — see this type's remarks; it is what maps onto
            // an EXCLUSIVE flock(LOCK_EX | LOCK_NB) on Unix, and the exclusivity comes from it alone
            // (a review MEASURED FileAccess.Write + FileShare.None yielding LOCK_EX just the same, so
            // the earlier claim that FileAccess.ReadWrite was what made the flock exclusive was
            // wrong). ReadWrite is kept because it is the honest description of a handle that is
            // opened, not because it changes the lock. FileOptions.None on BOTH platforms, with no
            // DeleteOnClose anywhere: on Unix its emulation unlinks a still-flock'd file and breaks
            // mutual exclusion, and on Windows — where it was safe but only cosmetic — it is a
            // deletion primitive on the losing side of the link race and adds a delete-pending
            // transient that mis-reports a clean hand-off (both measured; see the remarks). The file
            // is therefore created once and then persists, inertly, everywhere. bufferSize 1 because
            // nothing is ever written through this stream: it exists to be held open, and a buffer
            // would be memory allocated to carry no bytes.
            var handle = new FileStream(
                LockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            return new RunLockResult.Acquired(handle);
        }
        catch (IOException ex)
        {
            return ClassifyOpenFailure(ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new RunLockResult.Unavailable(ex);
        }
    }

    /// <summary>
    /// <see langword="true"/> when something is present at <see cref="LockFilePath"/> and it is a
    /// link (symbolic link, junction, or any other reparse point .NET resolves as one).
    /// </summary>
    /// <remarks>
    /// <see cref="File.ResolveLinkTarget(string, bool)"/> is asked for the IMMEDIATE target
    /// (<c>returnFinalTarget: false</c>) rather than the final one on purpose: the question here is
    /// "is this entry a link?", and asking for the final target would walk the chain — doing the
    /// attacker's dereference for them, and throwing on a dangling link instead of answering. A
    /// missing entry is the ordinary first-acquisition case and is not a link, so
    /// <see cref="FileNotFoundException"/>/<see cref="DirectoryNotFoundException"/> are absorbed
    /// here; every other failure is left to <see cref="TryAcquire"/>'s own classification.
    /// </remarks>
    private bool IsLinkAtLockPath()
    {
        try
        {
            return File.ResolveLinkTarget(LockFilePath, returnFinalTarget: false) is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decides whether an <see cref="IOException"/> from the open means "somebody else holds this
    /// file" (<see cref="RunLockResult.HeldByAnotherRun"/>) or anything else
    /// (<see cref="RunLockResult.Unavailable"/>) — the whole of this type's failure-mapping rule, in
    /// one place.
    /// </summary>
    /// <remarks>
    /// <b><see langword="internal"/> so <c>WorkspaceRunLockTests</c> can assert the rule directly</b>
    /// (via this assembly's <c>InternalsVisibleTo</c>). The rerouted subtypes are the point of the
    /// rule and are precisely the ones that cannot be planted end to end: <c>TryAcquire</c> creates
    /// the output directory itself, so a missing directory or an unreachable drive is refused by that
    /// earlier guard rather than by this one, and a test driving those through the front door would
    /// pass without ever exercising this mapping. See this type's failure-mapping remarks for why
    /// each arm is where it is.
    /// </remarks>
    internal static RunLockResult ClassifyOpenFailure(IOException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure is DirectoryNotFoundException or FileNotFoundException or PathTooLongException or DriveNotFoundException)
        {
            // IOException subtypes that name a location which cannot host a run — never a statement
            // about concurrency, and reporting one as RunInProgress would hand a host a retryable
            // wait (and somebody else's runId) for a condition that will never clear.
            return new RunLockResult.Unavailable(failure);
        }

        if (!OperatingSystem.IsWindows())
        {
            // A refused flock arrives as a bare IOException with no portable distinguishing code.
            return new RunLockResult.HeldByAnotherRun();
        }

        // The Win32 code lives in the low 16 bits of the HRESULT (0x8007xxxx) the BCL builds.
        return (failure.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation
            ? new RunLockResult.HeldByAnotherRun()
            : new RunLockResult.Unavailable(failure);
    }
}
