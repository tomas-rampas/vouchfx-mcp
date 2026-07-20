using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests.Cli;

/// <summary>
/// Covers <see cref="VouchfxCliPathResolver"/> — specifically MAJOR 1's fix (CWE-427): a directory
/// NOT present in the (synthetic) PATH is NEVER searched, no matter what it contains or how it
/// relates to the calling process's actual current working directory.
/// </summary>
/// <remarks>
/// Every test here uses the internal, testable overload
/// (<c>ResolveAbsolutePath(pathVariable, pathExtVariable, isWindows)</c>), which takes PATH as an
/// explicit string parameter and has no notion of a working directory anywhere in its signature —
/// so these tests never need to mutate the real, process-wide <see cref="Environment.CurrentDirectory"/>
/// (a global that would risk interfering with other tests running concurrently) to prove the point:
/// a fake executable sitting in a directory that is never mentioned in the supplied PATH string is,
/// structurally, not reachable by this method at all.
/// </remarks>
public class VouchfxCliPathResolverTests
{
    // ── MAJOR 1's direct guard: PATH membership, not mere existence on disk, determines discovery ─

    [Fact]
    public void ResolveAbsolutePath_FakeExecutableOutsideSyntheticPath_ThenAddedToPath_TransitionsFromNotFoundToFound()
    {
        // The exact CWE-427 scenario: a "vouchfx.exe" sitting in a directory standing in for an
        // untrusted, attacker-controlled workspace (what Windows' CreateProcess would otherwise
        // search FIRST for a bare command name, ahead of PATH — see VouchfxCliPathResolver's
        // remarks). The SAME directory, the SAME file, is resolved twice: once while it is absent
        // from the synthetic PATH (must be null), and once after it is added (must be found, as an
        // absolute path) — proving PATH membership is what determines discovery, not the file's
        // mere presence on disk somewhere the resolver could theoretically stumble onto it.
        var untrustedWorkspaceDir = CreateTempDirectory();
        var fakeExecutablePath = Path.Combine(untrustedWorkspaceDir, "vouchfx.exe");
        File.WriteAllText(fakeExecutablePath, "not a real executable");

        try
        {
            var unrelatedPathDir = CreateTempDirectory();
            try
            {
                var pathExcludingTheFake = unrelatedPathDir;

                var beforeAddingToPath = VouchfxCliPathResolver.ResolveAbsolutePath(
                    pathExcludingTheFake, pathExtVariable: ".EXE", isWindows: true);

                Assert.Null(beforeAddingToPath);
            }
            finally
            {
                Directory.Delete(unrelatedPathDir);
            }

            var pathIncludingTheFake = untrustedWorkspaceDir;

            var afterAddingToPath = VouchfxCliPathResolver.ResolveAbsolutePath(
                pathIncludingTheFake, pathExtVariable: ".EXE", isWindows: true);

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
            var secondDirExecutable = Path.Combine(secondDir, "vouchfx.exe");
            File.WriteAllText(secondDirExecutable, "not a real executable");

            var syntheticPath = string.Join(Path.PathSeparator, firstDir, secondDir);

            var result = VouchfxCliPathResolver.ResolveAbsolutePath(syntheticPath, ".EXE", isWindows: true);

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
        Assert.Null(VouchfxCliPathResolver.ResolveAbsolutePath(null, null, isWindows: true));
        Assert.Null(VouchfxCliPathResolver.ResolveAbsolutePath(string.Empty, null, isWindows: true));
    }

    [Fact]
    public void ResolveAbsolutePath_PathDirectoryDoesNotExist_SkipsItWithoutThrowing()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-does-not-exist");

        var result = VouchfxCliPathResolver.ResolveAbsolutePath(nonExistentDir, ".EXE", isWindows: true);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveAbsolutePath_WindowsWithoutPathExtVariable_FallsBackToDefaultExtensions()
    {
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
        // A bare "vouchfx" file (no extension) must NOT satisfy a Windows search unless PATHEXT
        // itself contains an empty entry — this guards against accidentally treating "File.Exists"
        // on the bare command name as sufficient on Windows.
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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
