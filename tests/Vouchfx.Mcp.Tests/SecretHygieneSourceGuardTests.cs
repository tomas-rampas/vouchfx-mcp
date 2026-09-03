using System.Text.RegularExpressions;

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
/// <b>Whitespace-tolerant matching (a review fix):</b> the per-file content guard uses REGEX, not
/// exact substrings — <c>startInfo.Environment ["X"] = ...</c> or <c>startInfo.Environment
/// .Add(...)</c> are legal C# that an exact <c>".Environment["</c>/<c>".Environment.Add"</c>
/// substring check would miss entirely, since whitespace (including a line break) is legal between
/// the member-access dot, the member name, and the following <c>[</c>/<c>(</c>. Every forbidden
/// shape below tolerates arbitrary whitespace (<c>\s*</c>, which matches newlines too) at each such
/// position, so reformatting alone can never smuggle a real mutation past this guard.
/// </para>
/// <para>
/// <b>Fail-closed, not a hardcoded list a new site can silently escape:</b> the set of files checked
/// is not asserted by fiat — <see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/> derives
/// the REAL set of process-spawn sites directly from <c>src/</c> using TWO independent signals (see
/// that test's own remarks for exactly what each catches and why both exist) and asserts the union is
/// EXACTLY <see cref="GuardedProcessSpawnSiteRelativePaths"/> — the same list the per-file content
/// guard is parameterised over via <see cref="GuardedSites"/>, so the two can never drift apart. A
/// fourth spawn site added anywhere in <c>src/</c>, in ANY construction style, fails this test
/// immediately, until <see cref="GuardedProcessSpawnSiteRelativePaths"/> is updated to include it —
/// which automatically extends the content guard to cover it too.
/// </para>
/// <para>
/// This is deliberately paired with, not a substitute for, <see cref="RealSecretHygieneMcpTests"/>'s
/// end-to-end sentinel proof: that class proves the OBSERVABLE outcome (no response, notification, or
/// resource ever carries this server's own environment); this class proves the STRUCTURAL reason it
/// cannot — there is no code path that even reads the environment for that purpose, let alone
/// serialises it into a child's env or an agent-facing message.
/// </para>
/// </remarks>
public class SecretHygieneSourceGuardTests
{
    /// <summary>
    /// The single source of truth for both the per-file content guard
    /// (<see cref="ProcessSpawnSite_NeverBuildsAnExplicitEnvironmentDictionaryOrMutatesThisProcessEnvironment"/>,
    /// via <see cref="GuardedSites"/>) and the fail-closed completeness check
    /// (<see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/>) below — kept as ONE list so
    /// the two assertions can never drift against each other. Every entry is a real process-spawn
    /// site in <c>src/</c> as of this file's own last edit.
    /// </summary>
    private static readonly string[] GuardedProcessSpawnSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Cli/VouchfxCliProcessRunner.cs",
        "src/Vouchfx.Mcp/Run/VouchfxCliSuiteRunner.cs",
        "src/Vouchfx.Mcp/Validation/ValidationWorkerClient.cs",
    ];

    /// <summary>
    /// Forbidden <c>ProcessStartInfo.Environment</c>/<c>EnvironmentVariables</c> mutation shapes, as
    /// whitespace-tolerant regexes (a review fix — see this type's remarks). <c>\s</c> matches line
    /// breaks as well as spaces/tabs in .NET regex by default, so these catch the shape regardless of
    /// how it is reformatted or wrapped across lines.
    /// </summary>
    private static readonly (string Description, Regex Pattern)[] ForbiddenEnvironmentMutationShapes =
    [
        ("an indexer assignment on ProcessStartInfo.Environment", new Regex(@"\.Environment\s*\[", RegexOptions.Compiled)),
        ("ProcessStartInfo.Environment.Add(...)", new Regex(@"\.Environment\s*\.\s*Add\s*\(", RegexOptions.Compiled)),
        ("ProcessStartInfo.Environment.Remove(...)", new Regex(@"\.Environment\s*\.\s*Remove\s*\(", RegexOptions.Compiled)),
        ("ProcessStartInfo.Environment.Clear()", new Regex(@"\.Environment\s*\.\s*Clear\s*\(", RegexOptions.Compiled)),
        ("an indexer assignment on ProcessStartInfo.EnvironmentVariables", new Regex(@"\.EnvironmentVariables\s*\[", RegexOptions.Compiled)),
        ("ProcessStartInfo.EnvironmentVariables.Add(...)", new Regex(@"\.EnvironmentVariables\s*\.\s*Add\s*\(", RegexOptions.Compiled)),
        ("ProcessStartInfo.EnvironmentVariables.Remove(...)", new Regex(@"\.EnvironmentVariables\s*\.\s*Remove\s*\(", RegexOptions.Compiled)),
        ("ProcessStartInfo.EnvironmentVariables.Clear()", new Regex(@"\.EnvironmentVariables\s*\.\s*Clear\s*\(", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Every documented way to CONSTRUCT a <c>ProcessStartInfo</c> that this guard's completeness
    /// check (<see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/>) looks for — see that
    /// test's remarks for what each alternative catches.
    /// </summary>
    private static readonly Regex ProcessStartInfoConstructionPattern = new(
        @"new\s+(global::)?(System\s*\.\s*Diagnostics\s*\.\s*)?ProcessStartInfo\b" +
        @"|(global::)?(System\s*\.\s*Diagnostics\s*\.\s*)?ProcessStartInfo\s+\w+\s*=\s*new\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// The belt-and-braces signal (a review fix — see <see cref="ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/>'s
    /// remarks): however a <c>ProcessStartInfo</c> was constructed, it is inert without a call that
    /// actually spawns the child. Keying off the SPAWN call, not the object that configures it, closes
    /// any gap in <see cref="ProcessStartInfoConstructionPattern"/>'s own coverage of construction
    /// syntax this test's author did not anticipate.
    /// </summary>
    private static readonly Regex ProcessStartCallPattern = new(
        @"(global::)?(System\s*\.\s*Diagnostics\s*\.\s*)?Process\s*\.\s*Start\s*\(",
        RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(GuardedSites))]
    public void ProcessSpawnSite_NeverBuildsAnExplicitEnvironmentDictionaryOrMutatesThisProcessEnvironment(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot.FullName, relativePath));

        // Doc-comment prose in these files freely says the word "environment" (e.g. discussing WHY
        // inheritance is correct) — these patterns only match the CODE shapes that would actually
        // inject, mutate, or filter something, never bare prose.
        foreach (var (description, pattern) in ForbiddenEnvironmentMutationShapes)
        {
            Assert.False(
                pattern.IsMatch(text),
                $"'{relativePath}' appears to contain {description} (matched by /{pattern}/) — an explicit " +
                "environment mutation, which REQ-010 forbids at every process-spawn site.");
        }

        // Mutating THIS process's own environment (as opposed to reading it, which
        // VouchfxCliPathResolver legitimately does for PATH/PATHEXT — deliberately not one of the
        // guarded files, since it is a path-resolution helper, not a process-spawn site) would be an
        // entirely separate, and equally out-of-scope, way to influence a child's inherited
        // environment from inside this server. A single C# identifier can never contain whitespace,
        // so an exact substring check is already whitespace-proof here — no regex needed.
        Assert.DoesNotContain("SetEnvironmentVariable", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fail-closed completeness check (a review fix): the per-file guard above only checks the files
    /// it is TOLD to check — a new process-spawn site added anywhere else in <c>src/</c> would
    /// silently escape it, in a way a purely literal <c>"new ProcessStartInfo"</c> substring search
    /// could ALSO miss (a target-typed <c>ProcessStartInfo x = new();</c>, a fully qualified
    /// <c>System.Diagnostics.ProcessStartInfo</c>, or <c>new</c> and the type name split across a line
    /// break are all legal C# a literal search does not see).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two independent signals, unioned:</b> (1) <see cref="ProcessStartInfoConstructionPattern"/>
    /// — <c>new ProcessStartInfo(...)</c> (optionally <c>global::</c>- and/or
    /// <c>System.Diagnostics.</c>-qualified, and with arbitrary whitespace/line breaks between every
    /// token), OR a target-typed declaration/field/assignment of the shape
    /// <c>ProcessStartInfo name = new(...)</c>. (2) <see cref="ProcessStartCallPattern"/> — a literal
    /// <c>Process.Start(...)</c> call (again qualification- and whitespace-tolerant), independent of
    /// how the <see cref="System.Diagnostics.ProcessStartInfo"/> passed to it was built. A
    /// <see cref="System.Diagnostics.ProcessStartInfo"/> is inert on its own — a hostile or careless
    /// change is only a real process-spawn site once something actually calls
    /// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> — so keying
    /// off THAT call as a second, independent signal closes any gap in the construction pattern's own
    /// coverage (a factory method, a collection initialiser, or any other construction syntax this
    /// test's author did not anticipate), belt-and-braces, exactly as requested in review.
    /// </para>
    /// <para>
    /// <b>What is verified NOT to over-match:</b> confirmed directly against this repository — the
    /// SAME three files match both signals, and no other <c>src/**/*.cs</c> file matches either,
    /// including <c>VouchfxCliPathResolver.cs</c>, whose doc comments discuss
    /// <c>ProcessStartInfo.FileName</c> in prose but never write <c>new ProcessStartInfo</c> or call
    /// <c>Process.Start</c>. Both patterns require the LITERAL type/method names immediately (only
    /// interior whitespace/qualification is tolerated) — a doc comment merely mentioning
    /// "ProcessStartInfo" without a preceding <c>new</c>/<c>=</c> or a following <c>.Start(</c> cannot
    /// match either one, so this guard does not need to special-case comments to stay accurate; if
    /// that ever changes, the exact-equality assertion below fails LOUDLY rather than silently
    /// widening the guarded set.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet()
    {
        var srcRoot = Path.Combine(RepoRoot.FullName, "src");
        var csFiles = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path))
            .ToArray();

        var constructionSites = csFiles
            .Where(path => ProcessStartInfoConstructionPattern.IsMatch(File.ReadAllText(path)))
            .Select(ToRepoRelativeForwardSlashPath);
        var startCallSites = csFiles
            .Where(path => ProcessStartCallPattern.IsMatch(File.ReadAllText(path)))
            .Select(ToRepoRelativeForwardSlashPath);

        var actualSites = constructionSites
            .Union(startCallSites, StringComparer.Ordinal)
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
    /// process-spawn sites this guard needs to reason about. Checked on the REPO-RELATIVE path, not
    /// the raw absolute one: a checkout rooted somewhere that happens to contain a "bin" or "obj"
    /// path SEGMENT above the repo root (e.g. a machine-specific clone location like
    /// <c>C:\Users\x\bin\vouchfx-mcp</c>) must never cause a false exclusion — only a bin/obj segment
    /// INSIDE the repo tree counts. Mirrors <c>VfxCodeCatalogueTests</c>' and
    /// <c>ErrorCatalogueFilesystemParityTests</c>' identically-named helpers exactly (all three now
    /// share this same relative-path implementation, precisely so they cannot silently disagree on
    /// what counts as build output).
    /// </summary>
    private static bool IsBuildOutputPath(string fullPath)
    {
        var relative = Path.GetRelativePath(RepoRoot.FullName, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal);
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
