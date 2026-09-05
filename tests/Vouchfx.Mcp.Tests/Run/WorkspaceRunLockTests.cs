using Vouchfx.Mcp.Run;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// US-S3-04's lock at the unit seam: spec §4.6's <c>&lt;outputDir&gt;/.lock</c>, its exclusivity,
/// its release, and the staleness property its acceptance criterion asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every exclusivity assertion here is genuinely cross-INSTANCE, and that is not the same claim as
/// cross-process — which is why it is not the only coverage.</b> Each case constructs a second
/// <see cref="WorkspaceRunLock"/> rather than reusing the first, so nothing is shared in memory and
/// the answer can only come from the operating system: on Windows the second open is refused by the
/// kernel's share-mode reservation, on Unix by <c>flock</c>. The genuinely-two-process proof lives in
/// <c>RealCrossProcessRunLockTests</c>, which spawns a holder in another OS process and kills it.
/// </para>
/// <para>
/// <b>The lock file persists on EVERY platform, and two cases pin that from both directions.</b>
/// <c>FileOptions.None</c> is passed everywhere — no <c>FileOptions.DeleteOnClose</c> at all (its
/// Unix emulation unlinks a still-flock'd file, and on Windows it was a deletion primitive bought for
/// cosmetics; see <see cref="WorkspaceRunLock"/>'s remarks) — so the file survives every release and
/// every kill. <see cref="TryAcquire_AfterRelease_SucceedsAgainAndTheLockFilePersistsInert"/> asserts
/// the state this type actually leaves behind, and
/// <see cref="TryAcquire_WithLeftoverLockFileAndNoHolder_Succeeds"/> plants that state by hand — from
/// no holder at all, which no release can produce — and asserts acquisition proceeds. Together they
/// pin the property the design rests on: the HANDLE is the lock, and the file's existence means
/// nothing.
/// </para>
/// <para>
/// <b>The link cases are anti-exploit tests, not tidiness tests.</b> <c>.lock</c> sits at a fixed
/// name inside a directory that travels with a cloned tree, so a link can be planted there before
/// the server ever starts. What is asserted is not merely that acquisition is refused but that the
/// LINK'S TARGET is still there afterwards: the measured exploit was
/// <c>FileOptions.DeleteOnClose</c> following the link and deleting the target on release. That flag
/// is now gone, which is the second, structural half of the fix — but the survival assertions stay,
/// because they are what would catch either half being undone. Self-gated on hosts that will not
/// create links, in the shape <c>PathSafetyGuardTests</c> established — see
/// <see cref="LinksUnavailableMarker"/>.
/// </para>
/// </remarks>
public class WorkspaceRunLockTests : IDisposable
{
    /// <summary>
    /// What a link-dependent case below prints when the OS refuses to create the link — the same
    /// announced self-gate <c>PathSafetyGuardTests</c> uses, for the same reason (xunit 2.9.3 has no
    /// <c>Assert.Skip</c>, and adding a package for one would breach CLAUDE.md's pinning discipline).
    /// </summary>
    internal const string LinksUnavailableMarker =
        "SKIPPED (not asserted): this host refused to create a link";

    private readonly string _sandbox;
    private readonly string _outputDirectory;
    private readonly ITestOutputHelper _output;

    public WorkspaceRunLockTests(ITestOutputHelper output)
    {
        _output = output;
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-runlock-" + Guid.NewGuid().ToString("N"));
        _outputDirectory = Path.Combine(_sandbox, ".vouchfx", "runs");
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    [Fact]
    public void TryAcquire_WhenFree_AcquiresAndCreatesTheLockFileAtTheSpecPath()
    {
        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        var acquired = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());

        try
        {
            // Spec §4.6 names the path literally; asserting it here is what stops a refactor from
            // silently relocating the one file two independent server processes must agree on.
            Assert.Equal(Path.Combine(_outputDirectory, ".lock"), runLock.LockFilePath);
            Assert.True(File.Exists(runLock.LockFilePath));
        }
        finally
        {
            acquired.Release.Dispose();
        }
    }

    [Fact]
    public void TryAcquire_CreatesTheOutputDirectoryWhenItDoesNotExistYet()
    {
        // The registry creates outputDir on the first run it records; the lock is taken BEFORE that,
        // so on a brand-new workspace the lock is the thing that must create it.
        Assert.False(Directory.Exists(_outputDirectory));

        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);
        var acquired = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());
        acquired.Release.Dispose();

        Assert.True(Directory.Exists(_outputDirectory));
    }

    [Fact]
    public void TryAcquire_WhileHeldByAnotherInstance_ReportsHeldByAnotherRun()
    {
        var holder = new WorkspaceRunLock(_outputDirectory, workspace: null);
        var contender = new WorkspaceRunLock(_outputDirectory, workspace: null);

        var acquired = Assert.IsType<RunLockResult.Acquired>(holder.TryAcquire());

        try
        {
            Assert.IsType<RunLockResult.HeldByAnotherRun>(contender.TryAcquire());
        }
        finally
        {
            acquired.Release.Dispose();
        }
    }

    [Fact]
    public void TryAcquire_AfterRelease_SucceedsAgainAndTheLockFilePersistsInert()
    {
        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        var first = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());
        first.Release.Dispose();

        // EVERY platform, deliberately. This assertion was `Assert.False`, guarded by
        // OperatingSystem.IsWindows(), back when FileOptions.DeleteOnClose was requested there. It is
        // requested nowhere now — on Unix the emulation unlinks a still-flock'd file, and on Windows
        // the kernel flag was a deletion primitive on the losing side of the link race, bought for
        // cosmetics (both measured; see WorkspaceRunLock's remarks) — so a lock file that outlives a
        // clean release IS the intended behaviour, identically on both platforms, rather than a Unix
        // quirk to be tolerated.
        Assert.True(
            File.Exists(runLock.LockFilePath),
            "The lock file was deleted on release — DeleteOnClose, or an equivalent unlink, is back.");

        // And it is inert: reacquisition over this type's OWN residue succeeds. That provenance is
        // what keeps this distinct from TryAcquire_WithLeftoverLockFileAndNoHolder_Succeeds below,
        // which plants a file no release produced.
        var second = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());
        second.Release.Dispose();
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        // IRunLock's contract promises this, so RunSuiteOrchestrator can dispose from a `finally`
        // without tracking whether an earlier path already did.
        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);
        var acquired = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());

        acquired.Release.Dispose();
        acquired.Release.Dispose();

        var reacquired = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());
        reacquired.Release.Dispose();
    }

    /// <summary>
    /// The story's second Gherkin scenario at the unit seam: a lock file left behind by a process
    /// that no longer exists must not block anything. The file is planted deliberately, with no
    /// holder ever having existed — the state a killed holder leaves on either platform, now that
    /// <c>FileOptions.DeleteOnClose</c> is requested nowhere, and one no release path could fake.
    /// </summary>
    [Fact]
    public void TryAcquire_WithLeftoverLockFileAndNoHolder_Succeeds()
    {
        Directory.CreateDirectory(_outputDirectory);
        var lockFilePath = Path.Combine(_outputDirectory, ".lock");
        File.WriteAllText(lockFilePath, string.Empty);
        Assert.True(File.Exists(lockFilePath));

        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        var acquired = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());
        acquired.Release.Dispose();
    }

    /// <summary>
    /// The same, for residue that is not empty — a pid-and-runId file left by some earlier or future
    /// scheme. Acquisition must not read it, parse it, or be influenced by it: the OS handle is the
    /// only authority, which is precisely why there is no stale-detection code to get wrong.
    /// </summary>
    [Fact]
    public void TryAcquire_WithLeftoverLockFileCarryingContent_IgnoresTheContentEntirely()
    {
        Directory.CreateDirectory(_outputDirectory);
        File.WriteAllText(Path.Combine(_outputDirectory, ".lock"), """{"pid":999999,"runId":"run-deadbeef"}""");

        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        var acquired = Assert.IsType<RunLockResult.Acquired>(runLock.TryAcquire());
        acquired.Release.Dispose();
    }

    [Fact]
    public void Constructor_RejectsAnEmptyOutputDirectory()
    {
        Assert.ThrowsAny<ArgumentException>(() => new WorkspaceRunLock("   ", workspace: null));
    }

    // ── A link planted at the lock path (a security review's BLOCKER) ───────────────────────────

    /// <summary>
    /// The measured exploit, closed: a symbolic link pre-planted at <c>&lt;outputDir&gt;/.lock</c>
    /// must be REFUSED, and — the assertion that makes this an anti-exploit test rather than a
    /// tidiness one — the link's TARGET must still exist after acquisition and release.
    /// </summary>
    /// <remarks>
    /// Without the refusal, <see cref="FileMode.OpenOrCreate"/> follows the link — and when
    /// <c>FileOptions.DeleteOnClose</c> was still requested, the release then deleted whatever it
    /// pointed at: an arbitrary-file-delete primitive reachable by anyone who can put a file in the
    /// workspace before the server starts, which a cloned repository is. The target here stands in
    /// for that file. Both halves of the fix are now in place, and the two assertions below cover one
    /// each: the refusal catches the probe being dropped, and the target's survival catches
    /// <c>FileOptions.DeleteOnClose</c> coming back — which is only an exploit again if both are lost
    /// at once, hence both assertions.
    /// </remarks>
    [Fact]
    public void TryAcquire_WhenASymbolicLinkIsPlantedAtTheLockPath_IsRefusedAndTheTargetSurvives()
    {
        Directory.CreateDirectory(_outputDirectory);

        var target = Path.Combine(_sandbox, "precious.txt");
        File.WriteAllText(target, "must survive");

        var lockPath = Path.Combine(_outputDirectory, ".lock");
        try
        {
            File.CreateSymbolicLink(lockPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            SkipBecauseLinksAreUnavailable(ex);
            return;
        }

        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        var result = runLock.TryAcquire();

        var unavailable = Assert.IsType<RunLockResult.Unavailable>(result);

        // Never HeldByAnotherRun: a planted link is a permanent condition, and VFX-E-1501 would
        // invite a host to poll a workspace that will never accept a run.
        Assert.IsAssignableFrom<IOException>(unavailable.Failure);

        // The whole point of the original BLOCKER. Had the open followed the link while
        // DeleteOnClose was still requested, the release would have taken the target with it.
        Assert.True(File.Exists(target), "The link's target was deleted — the exploit is open.");
        Assert.Equal("must survive", File.ReadAllText(target));
    }

    /// <summary>
    /// The privilege-free variant of the same plant. On Windows a DIRECTORY junction
    /// (<c>mklink /J</c>) needs no Developer Mode and no <c>SeCreateSymbolicLinkPrivilege</c>, so it
    /// is the form an unprivileged attacker actually has; .NET cannot create one, hence the shell.
    /// Elsewhere — and on a Windows host where even that is refused — the case falls back to a
    /// directory symbolic link and then to the announced self-gate.
    /// </summary>
    [Fact]
    public void TryAcquire_WhenAJunctionIsPlantedAtTheLockPath_IsRefusedAndTheTargetSurvives()
    {
        Directory.CreateDirectory(_outputDirectory);

        var target = Path.Combine(_sandbox, "precious-directory");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "must survive");

        var lockPath = Path.Combine(_outputDirectory, ".lock");
        if (!TryCreateJunctionOrDirectorySymbolicLink(lockPath, target, out var cause))
        {
            SkipBecauseLinksAreUnavailable(cause!);
            return;
        }

        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        // Unavailable either way — refused as a link if .NET resolves the reparse point, and refused
        // by the open as a non-file if it does not. Both are correct; neither may be a concurrency
        // answer, which is what this asserts.
        Assert.IsType<RunLockResult.Unavailable>(runLock.TryAcquire());

        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    /// <summary>
    /// A DIRECTORY sitting where the lock file goes is a permanent condition, not contention.
    /// Nothing plants this maliciously as a rule — it is the shape a mistaken <c>mkdir</c> or a
    /// half-restored backup leaves — but the classification matters for the same reason: a host told
    /// <c>RunInProgress</c> would poll it forever.
    /// </summary>
    [Fact]
    public void TryAcquire_WhenADirectorySitsAtTheLockPath_ReportsUnavailable()
    {
        Directory.CreateDirectory(Path.Combine(_outputDirectory, ".lock"));

        var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

        Assert.IsType<RunLockResult.Unavailable>(runLock.TryAcquire());
    }

    /// <summary>
    /// A read-only file at the lock path: the open needs write access it cannot get, which is a
    /// permission problem — <see cref="RunLockResult.Unavailable"/>, never
    /// <see cref="RunLockResult.HeldByAnotherRun"/>.
    /// </summary>
    [Fact]
    public void TryAcquire_WhenAReadOnlyFileSitsAtTheLockPath_ReportsUnavailable()
    {
        Directory.CreateDirectory(_outputDirectory);
        var lockPath = Path.Combine(_outputDirectory, ".lock");
        File.WriteAllText(lockPath, string.Empty);
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);

        try
        {
            var runLock = new WorkspaceRunLock(_outputDirectory, workspace: null);

            Assert.IsType<RunLockResult.Unavailable>(runLock.TryAcquire());
        }
        finally
        {
            // Cleared here rather than in Dispose: a read-only file defeats the recursive delete,
            // and leaving temp residue behind would be this test's own mess.
            File.SetAttributes(lockPath, FileAttributes.Normal);
        }
    }

    // ── The IOException → result mapping (a security review's MAJOR) ────────────────────────────
    //
    // Asserted against WorkspaceRunLock.ClassifyOpenFailure directly, which is why that method is
    // internal. These subtypes cannot be planted end to end: TryAcquire creates the output directory
    // itself, so a missing directory or an unreachable drive is refused by that earlier guard and a
    // front-door test would pass without ever reaching this rule. The genuine sharing-violation path
    // is covered end to end by TryAcquire_WhileHeldByAnotherInstance_ReportsHeldByAnotherRun above.

    [Theory]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(PathTooLongException))]
    [InlineData(typeof(DriveNotFoundException))]
    public void ClassifyOpenFailure_ForAPermanentIOExceptionSubtype_IsUnavailableNotHeldByAnotherRun(Type failureType)
    {
        var failure = (IOException)Activator.CreateInstance(failureType)!;

        var classified = WorkspaceRunLock.ClassifyOpenFailure(failure);

        var unavailable = Assert.IsType<RunLockResult.Unavailable>(classified);
        Assert.Same(failure, unavailable.Failure);
    }

    /// <summary>
    /// Windows names contention precisely, so a bare <see cref="IOException"/> is accepted as
    /// <see cref="RunLockResult.HeldByAnotherRun"/> only for <c>ERROR_SHARING_VIOLATION</c> (32) and
    /// <c>ERROR_LOCK_VIOLATION</c> (33). Unix has no equivalent code for a refused <c>flock</c>, so a
    /// bare <see cref="IOException"/> stays contention there — hence the platform split in the
    /// expectation rather than two differently-named tests.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void ClassifyOpenFailure_ForAWindowsSharingOrLockViolation_IsHeldByAnotherRun(int win32Error)
    {
        var failure = new IOException("contended") { HResult = unchecked((int)0x80070000) | win32Error };

        Assert.IsType<RunLockResult.HeldByAnotherRun>(WorkspaceRunLock.ClassifyOpenFailure(failure));
    }

    [Fact]
    public void ClassifyOpenFailure_ForABareIOExceptionThatIsNotAWindowsSharingViolation_IsPlatformDependent()
    {
        // ERROR_NOT_READY — a device fault, not contention. This is the exact shape the old
        // catch-all produced a (possibly stale) runId for.
        var failure = new IOException("device not ready") { HResult = unchecked((int)0x80070015) };

        var classified = WorkspaceRunLock.ClassifyOpenFailure(failure);

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<RunLockResult.Unavailable>(classified);
        }
        else
        {
            Assert.IsType<RunLockResult.HeldByAnotherRun>(classified);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a reparse point at <paramref name="linkPath"/> aimed at <paramref name="target"/>:
    /// a real junction on Windows (<c>mklink /J</c>, which needs no privilege), otherwise a directory
    /// symbolic link. <see langword="false"/> means this host would not create either.
    /// </summary>
    private static bool TryCreateJunctionOrDirectorySymbolicLink(string linkPath, string target, out Exception? cause)
    {
        cause = null;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                });

                if (process is not null)
                {
                    process.WaitForExit(30_000);
                    if (process.HasExited && process.ExitCode == 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                cause = ex;
            }
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            cause = ex;
            return false;
        }
    }

    private void SkipBecauseLinksAreUnavailable(Exception cause) =>
        _output.WriteLine($"{LinksUnavailableMarker} ({cause.GetType().Name}: {cause.Message}).");
}
