namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for REQ-010's secret-hygiene contract at every process-spawn
/// boundary in this codebase. Mirrors <see cref="VendoredArtefactsTests"/>'s pattern of reading real
/// files from the checked-out repo rather than the built assembly, for the same reason: this is a
/// static, structural property of the SOURCE, not something observable by driving the compiled
/// server through the MCP harness — see <see cref="RealSecretHygieneMcpTests"/> for the
/// complementary observable-behaviour proof.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this guards:</b> none of the process-spawn sites in <c>src/</c> may ever construct an
/// EXPLICIT <c>ProcessStartInfo.Environment</c>/<c>EnvironmentVariables</c> dictionary entry — added
/// to, indexed into, or FILTERED (removed/cleared) — or call
/// <see cref="Environment.SetEnvironmentVariable(string, string?)"/>. Today, every spawned child (the
/// <c>vouchfx</c> CLI itself, and the in-process validation worker — always THIS SAME executable
/// re-invoked, never a container) INHERITS this server's environment implicitly: the .NET default
/// whenever <c>ProcessStartInfo.Environment</c> is left untouched. That inheritance is CORRECT and
/// NECESSARY — a suite's own <c>${secret:env/X}</c> reference (§17) can only resolve inside the
/// engine if the variable it names is actually present in the CHILD's environment, and the child's
/// environment is this server's own, passed through unmodified. What must NEVER happen is this
/// server explicitly BUILDING, MUTATING, or CURATING that dictionary — including by filtering which
/// variables a child sees, which the README's secret-hygiene note calls out just as explicitly as
/// injecting one — since either would mean this server had started deciding secret/environment
/// content itself, exactly the responsibility REQ-010 says only the engine (§17's redaction
/// authority) may hold. A future change that started doing so would cross that line, and this test
/// fails the moment such code appears in source.
/// </para>
/// <para>
/// <b>Fail-closed, not a hardcoded list a new site can silently escape:</b> the set of files checked
/// is not asserted by fiat — <see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/> derives
/// the REAL set of process-spawn sites directly from <c>src/</c> (every <c>*.cs</c> file, excluding
/// build output, containing a literal <c>new ProcessStartInfo</c>) and asserts it is EXACTLY
/// <see cref="GuardedProcessSpawnSiteRelativePaths"/> — the same list the per-file content guard
/// below is parameterised over via <see cref="GuardedSites"/>, so the two can never drift apart. A
/// fourth spawn site added anywhere in <c>src/</c> fails this test immediately, until
/// <see cref="GuardedProcessSpawnSiteRelativePaths"/> is updated to include it — which automatically
/// extends the content guard to cover it too.
/// </para>
/// <para>
/// This is deliberately paired with, not a substitute for, <see cref="RealSecretHygieneMcpTests"/>'s
/// end-to-end sentinel proof: that class proves the OBSERVABLE outcome (no response or notification
/// ever carries this server's own environment); this class proves the STRUCTURAL reason it cannot —
/// there is no code path that even reads the environment for that purpose, let alone serialises it
/// into a child's env or an agent-facing message.
/// </para>
/// </remarks>
public class SecretHygieneSourceGuardTests
{
    /// <summary>
    /// The single source of truth for both the per-file content guard
    /// (<see cref="ProcessSpawnSite_NeverBuildsAnExplicitEnvironmentDictionaryOrMutatesThisProcessEnvironment"/>,
    /// via <see cref="GuardedSites"/>) and the fail-closed completeness check
    /// (<see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/>) below — kept as ONE list so
    /// the two assertions can never drift against each other. Every entry is a real
    /// <c>new ProcessStartInfo(...)</c> call site in <c>src/</c> as of this file's own last edit.
    /// </summary>
    private static readonly string[] GuardedProcessSpawnSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Cli/VouchfxCliProcessRunner.cs",
        "src/Vouchfx.Mcp/Run/VouchfxCliSuiteRunner.cs",
        "src/Vouchfx.Mcp/Validation/ValidationWorkerClient.cs",
    ];

    [Theory]
    [MemberData(nameof(GuardedSites))]
    public void ProcessSpawnSite_NeverBuildsAnExplicitEnvironmentDictionaryOrMutatesThisProcessEnvironment(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.FullName, relativePath));

        // ProcessStartInfo.Environment / the legacy EnvironmentVariables alias — added to, indexed
        // into, or FILTERED (removed/cleared) — is the shape an explicit env-injection OR curation
        // would necessarily take. Doc-comment prose in these files freely says the word
        // "environment" (e.g. discussing WHY inheritance is correct) — this only guards the CODE
        // shapes that would actually inject, mutate, or filter something.
        Assert.DoesNotContain(".Environment[", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".Environment.Add", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".Environment.Remove(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".Environment.Clear(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".EnvironmentVariables[", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".EnvironmentVariables.Add", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".EnvironmentVariables.Remove(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".EnvironmentVariables.Clear(", text, StringComparison.Ordinal);

        // Mutating THIS process's own environment (as opposed to reading it, which
        // VouchfxCliPathResolver legitimately does for PATH/PATHEXT — deliberately not one of the
        // guarded files, since it is a path-resolution helper, not a process-spawn site) would be an
        // entirely separate, and equally out-of-scope, way to influence a child's inherited
        // environment from inside this server.
        Assert.DoesNotContain("SetEnvironmentVariable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet()
    {
        var srcRoot = Path.Combine(RepoRoot.FullName, "src");
        var actualSites = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path))
            .Where(path => File.ReadAllText(path).Contains("new ProcessStartInfo", StringComparison.Ordinal))
            .Select(ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var expectedSites = GuardedProcessSpawnSiteRelativePaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // Not a subset/superset check: EXACT equality, both ways. A new spawn site left out of
        // GuardedProcessSpawnSiteRelativePaths fails here (fail-closed); a stale entry for a file
        // that no longer spawns a process fails here too, so the list never quietly rots either way.
        Assert.Equal(expectedSites, actualSites);
    }

    /// <summary>Adapts <see cref="GuardedProcessSpawnSiteRelativePaths"/> for <see cref="MemberDataAttribute"/>.</summary>
    public static TheoryData<string> GuardedSites()
    {
        var data = new TheoryData<string>();
        foreach (var path in GuardedProcessSpawnSiteRelativePaths)
        {
            data.Add(path);
        }

        return data;
    }

    /// <summary>
    /// Excludes build output (<c>bin/</c>, <c>obj/</c>) from the <c>src/</c> scan: neither directory
    /// holds hand-written source, and generated/copied artefacts underneath them are not
    /// process-spawn sites this guard needs to reason about. Checked by PATH SEGMENT, not substring,
    /// so a hand-written file or directory that merely CONTAINS "bin"/"obj" as part of a longer name
    /// (there are none today, but the check should not assume that) is never wrongly excluded.
    /// </summary>
    private static bool IsBuildOutputPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.Ordinal) || segments.Contains("obj", StringComparer.Ordinal);
    }

    /// <summary>
    /// Normalises to a forward-slash, repo-root-relative path — matching the literal form
    /// <see cref="GuardedProcessSpawnSiteRelativePaths"/> uses — so the comparison in
    /// <see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/> is identical on Windows (where
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> yields backslash-separated
    /// paths) and Linux CI (where the native separator already is a forward slash).
    /// </summary>
    private static string ToRepoRelativeForwardSlashPath(string fullPath) =>
        Path.GetRelativePath(RepoRoot.FullName, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Mirrors <c>VendoredArtefactsTests.RepoRoot</c> exactly — see that property's remarks.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");
            var repoRoot = testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

            return repoRoot;
        }
    }
}
