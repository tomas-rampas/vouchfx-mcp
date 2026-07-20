using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests.Cli;

/// <summary>
/// Covers <see cref="VouchfxCliPathResolver"/> — specifically MAJOR 1's fix (CWE-427): a directory
/// NOT present in the (synthetic) PATH is NEVER searched, no matter what it contains or how it
/// relates to the calling process's actual current working directory.
/// </summary>
/// <remarks>
/// <para>
/// Every test here uses the internal, testable overload
/// (<c>ResolveAbsolutePath(pathVariable, pathExtVariable, isWindows)</c>), which takes PATH as an
/// explicit string parameter and has no notion of a working directory anywhere in its signature —
/// so these tests never need to mutate the real, process-wide <see cref="Environment.CurrentDirectory"/>
/// (a global that would risk interfering with other tests running concurrently) to prove the point:
/// a fake executable sitting in a directory that is never mentioned in the supplied PATH string is,
/// structurally, not reachable by this method at all.
/// </para>
/// <para>
/// <b>Platform-aware by construction (fleet's known Windows-vs-Linux test-divergence gotcha):</b>
/// most tests below pass <see cref="OperatingSystem.IsWindows"/> — the REAL host platform — as the
/// resolver's <c>isWindows</c> argument, and build their fixture file via
/// <see cref="CreateFakeVouchfxExecutable"/>, which creates a WINDOWS-shaped fixture
/// (<c>vouchfx.exe</c>) on Windows or a UNIX-shaped one (a bare <c>vouchfx</c> file with the
/// execute permission bit actually set, via <see cref="File.SetUnixFileMode"/>) on every other
/// platform. This is deliberate, not incidental: on Windows a bare <c>File.Exists</c> check is
/// genuinely sufficient (case-insensitive filesystem, no permission-bit concept), but on Linux the
/// PRODUCTION resolver correctly requires BOTH the bare (no-PATHEXT) name AND the execute bit —
/// see <see cref="VouchfxCliPathResolver"/>'s own remarks — so a fixture that skips either would
/// make these tests pass for the wrong reason on Windows while failing outright on Linux CI, which
/// is exactly what an earlier, Windows-only-shaped version of this file did. The two tests that
/// deliberately test WINDOWS-specific candidate-generation logic in isolation
/// (<see cref="ResolveAbsolutePath_WindowsWithoutPathExtVariable_FallsBackToDefaultExtensions"/>,
/// <see cref="ResolveAbsolutePath_WindowsPlainFileWithNoExtension_IsNotMatchedWithoutAPathExtHit"/>)
/// are the deliberate exception: they hardcode <c>isWindows: true</c> regardless of the host OS,
/// because they are unit-testing that one pure code branch on purpose — see their own remarks for
/// why that remains correct on any host.
/// </para>
/// </remarks>
public class VouchfxCliPathResolverTests
{
    // ── MAJOR 1's direct guard: PATH membership, not mere existence on disk, determines discovery ─

    [Fact]
    public void ResolveAbsolutePath_FakeExecutableOutsideSyntheticPath_ThenAddedToPath_TransitionsFromNotFoundToFound()
    {
        // The exact CWE-427 scenario: a fake "vouchfx" sitting in a directory standing in for an
        // untrusted, attacker-controlled workspace (what Windows' CreateProcess would otherwise
        // search FIRST for a bare command name, ahead of PATH — see VouchfxCliPathResolver's
        // remarks). The SAME directory, the SAME file, is resolved twice: once while it is absent
        // from the synthetic PATH (must be null), and once after it is added (must be found, as an
        // absolute path) — proving PATH membership is what determines discovery, not the file's
        // mere presence on disk somewhere the resolver could theoretically stumble onto it.
        var untrustedWorkspaceDir = CreateTempDirectory();
        var fakeExecutablePath = CreateFakeVouchfxExecutable(untrustedWorkspaceDir);

        try
        {
            var unrelatedPathDir = CreateTempDirectory();
            try
            {
                var beforeAddingToPath = VouchfxCliPathResolver.ResolveAbsolutePath(
                    unrelatedPathDir, CurrentPlatformPathExt, OperatingSystem.IsWindows());

                Assert.Null(beforeAddingToPath);
            }
            finally
            {
                Directory.Delete(unrelatedPathDir);
            }

            var afterAddingToPath = VouchfxCliPathResolver.ResolveAbsolutePath(
                untrustedWorkspaceDir, CurrentPlatformPathExt, OperatingSystem.IsWindows());

            Assert.NotNull(afterAddingToPath);
            Assert.True(Path.IsPathRooted(afterAddingToPath), $"Expected an absolute path, got '{afterAddingToPath}'.");
            Assert.Equal(Path.GetFullPath(fakeExecutablePath), afterAddingToPath, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(untrustedWorkspaceDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveAbsolutePath_MultipleDirectoriesOnPath_ReturnsTheFirstMatchInOrder()
    {
        var firstDir = CreateTempDirectory();
        var secondDir = CreateTempDirectory();

        try
        {
            // firstDir deliberately contains nothing — only secondDir has the executable.
            var secondDirExecutable = CreateFakeVouchfxExecutable(secondDir);

            var syntheticPath = string.Join(Path.PathSeparator, firstDir, secondDir);

            var result = VouchfxCliPathResolver.ResolveAbsolutePath(
                syntheticPath, CurrentPlatformPathExt, OperatingSystem.IsWindows());

            Assert.Equal(Path.GetFullPath(secondDirExecutable), result, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(firstDir, recursive: true);
            Directory.Delete(secondDir, recursive: true);
        }
    }

    // ── Edge cases ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveAbsolutePath_NoPathVariable_ReturnsNullWithoutThrowing()
    {
        Assert.Null(VouchfxCliPathResolver.ResolveAbsolutePath(null, CurrentPlatformPathExt, OperatingSystem.IsWindows()));
        Assert.Null(VouchfxCliPathResolver.ResolveAbsolutePath(string.Empty, CurrentPlatformPathExt, OperatingSystem.IsWindows()));
    }

    [Fact]
    public void ResolveAbsolutePath_PathDirectoryDoesNotExist_SkipsItWithoutThrowing()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-does-not-exist");

        var result = VouchfxCliPathResolver.ResolveAbsolutePath(
            nonExistentDir, CurrentPlatformPathExt, OperatingSystem.IsWindows());

        Assert.Null(result);
    }

    // ── Windows-specific candidate-generation logic, tested in isolation on ANY host OS ─────────

    [Fact]
    public void ResolveAbsolutePath_WindowsWithoutPathExtVariable_FallsBackToDefaultExtensions()
    {
        // Deliberately hardcodes isWindows: true regardless of the host OS this test happens to
        // run on: this is a pure unit test of ONE code branch (BuildCandidateNames' PATHEXT-unset
        // fallback to .COM/.EXE/.BAT/.CMD) — a bare File.Exists check under isWindows: true never
        // touches File.GetUnixFileMode at all, so it behaves identically on any host filesystem AS
        // LONG AS the fixture's case exactly matches a generated candidate (Windows filesystems are
        // case-insensitive so this would pass there regardless, but Linux's are not — hence the
        // exact-case ".EXE" below, matching DefaultWindowsExtensions verbatim, rather than relying
        // on a Windows-only case-insensitive match to paper over a mismatch).
        var directory = CreateTempDirectory();
        try
        {
            var executablePath = Path.Combine(directory, "vouchfx.EXE");
            File.WriteAllText(executablePath, "not a real executable");

            var result = VouchfxCliPathResolver.ResolveAbsolutePath(directory, pathExtVariable: null, isWindows: true);

            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveAbsolutePath_WindowsPlainFileWithNoExtension_IsNotMatchedWithoutAPathExtHit()
    {
        // Same reasoning as above: hardcodes isWindows: true on purpose. A bare "vouchfx" file
        // (no extension) must NOT satisfy a Windows search unless PATHEXT itself contains an empty
        // entry — this guards against accidentally treating "File.Exists" on the bare command name
        // as sufficient on Windows. The "no match" outcome is exact-case-independent (there is no
        // candidate name this fixture could accidentally collide with), so it is genuinely
        // unfindable on any host OS running this assertion.
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "vouchfx"), "not a real executable");

            var result = VouchfxCliPathResolver.ResolveAbsolutePath(directory, ".EXE", isWindows: true);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A representative PATHEXT value for the CURRENT host platform: a real value on Windows
    /// (irrelevant, but harmless, on any other platform — <see cref="VouchfxCliPathResolver"/>
    /// never consults it when <c>isWindows</c> is false).
    /// </summary>
    private static string? CurrentPlatformPathExt => OperatingSystem.IsWindows() ? ".EXE" : null;

    /// <summary>
    /// Creates a fake <c>vouchfx</c> executable in <paramref name="directory"/>, shaped correctly
    /// for whichever platform this test is ACTUALLY running on, and returns its full path.
    /// </summary>
    /// <remarks>
    /// On Windows: a plain <c>vouchfx.exe</c> file — existence alone is what
    /// <see cref="VouchfxCliPathResolver"/> requires there (no permission-bit concept). On every
    /// other platform: a BARE <c>vouchfx</c> file (no extension — PATHEXT is Windows-only) with the
    /// execute permission bit actually set via <see cref="File.SetUnixFileMode"/>, matching exactly
    /// what the production resolver's real <see cref="File.GetUnixFileMode"/> check requires there.
    /// A fixture that skipped either of those platform-specific shapes would make the affected test
    /// pass on Windows for the wrong reason while failing outright on Linux CI.
    /// </remarks>
    private static string CreateFakeVouchfxExecutable(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsPath = Path.Combine(directory, "vouchfx.exe");
            File.WriteAllText(windowsPath, "not a real executable");
            return windowsPath;
        }

        var unixPath = Path.Combine(directory, "vouchfx");
        File.WriteAllText(unixPath, "#!/bin/sh\necho fake\n");
        File.SetUnixFileMode(
            unixPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return unixPath;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
