using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for US-S5-01's B1 fix: <see cref="Vouchfx.Mcp.Specs.SpecIndexParser"/>
/// — the only code that hands a workspace suite's bytes to YamlDotNet — is called from the WORKER
/// child process and from nowhere else in <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant this holds, and why it needs a test rather than a comment.</b> YamlDotNet's
/// Scanner can be driven into an unbounded, uninterruptible ~100%-CPU spin by a twelve-byte
/// well-formed input (<c>a: b</c> followed by a more-indented <c>a: b</c>), and
/// <c>YamlSafetyGuard.CheckNestingDepth</c> is no defence because that check IS the Scanner. A call to
/// <c>SpecIndexParser.Parse</c> on this server's own request thread therefore wedges the process
/// permanently — no <see cref="System.Threading.CancellationToken"/> can recover it, because the loop
/// has no cooperative cancellation point to observe. That was the shipped defect this guard exists to
/// stop recurring: <c>vouchfx://workspace/specs</c> originally parsed in-process, and one mis-indented
/// suite in a developer's own <c>e2e/</c> directory killed the resource for the life of the server.
/// </para>
/// <para>
/// A comment saying "do not call this from the server" is exactly the kind of instruction a future
/// change reads past — the method is public, it is convenient, and calling it directly would make
/// every test in this repository pass. Only a structural check fails.
/// </para>
/// <para>
/// <b>Mirrors <see cref="CursorCallSiteSourceGuardTests"/>' shape exactly</b> — a whitespace-tolerant
/// regex over source with comments and string literals stripped, plus a fail-closed EXACT-equality
/// check against a named set, so a new call site fails by name and a stale entry cannot rot.
/// </para>
/// </remarks>
public class SpecIndexParserSourceGuardTests
{
    /// <summary>
    /// The only file in <c>src/</c> allowed to invoke the parser: <c>Program.cs</c>, whose
    /// <c>--spec-index-worker</c> mode IS the disposable child process.
    /// </summary>
    /// <remarks>
    /// One entry, and it should stay one entry. A second would mean either a second worker mode (fine,
    /// name it here with its reasoning) or a call from the long-lived server (not fine — that is the
    /// defect). Notably NOT on this list: <c>Specs/WorkspaceSpecIndexer.cs</c>, which builds the index
    /// and must reach the parse only through <c>SpecIndexWorkerClient</c>'s process boundary.
    /// </remarks>
    private static readonly string[] GuardedCallSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Program.cs",
    ];

    /// <summary>
    /// An INVOCATION of the parser, qualified by its owning type. Qualifying it keeps the pattern from
    /// firing on unrelated <c>Parse</c> members elsewhere in the assembly, and — because
    /// <c>SpecIndexParser</c> is a static class — every real call site necessarily spells the type out.
    /// </summary>
    private static readonly Regex ParserInvocation =
        new(@"SpecIndexParser\s*\.\s*Parse\s*\(", RegexOptions.Compiled);

    [Fact]
    public void TheSpecIndexParser_HasExactlyTheWorkerAsItsCallSiteInSrc()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => ParserInvocation.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            GuardedCallSiteRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void TheWorkerCallSite_StillExistsAndStillCallsTheParser()
    {
        // Anti-vacuity in both directions: a renamed or deleted file would make the set check above
        // pass over nothing, and a Program.cs that stopped calling the parser would mean the worker
        // mode had been gutted while this guard kept reporting success.
        foreach (var relativePath in GuardedCallSiteRelativePaths)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            Assert.Matches(ParserInvocation, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }

    [Fact]
    public void TheWorkspaceSpecIndexer_ReachesTheParseOnlyThroughTheProcessBoundary()
    {
        // The specific regression, named. This file is the one a future change would most plausibly
        // "simplify" by calling the parser directly — it already knows the paths, and doing so would
        // delete a whole worker. The set check above catches it; this states WHICH file and WHY, so
        // the failure message is a reason rather than a diff.
        var indexer = Path.Combine(
            SourceGuardScan.RepoRoot.FullName,
            Path.Combine("src", "Vouchfx.Mcp", "Specs", "WorkspaceSpecIndexer.cs"));

        Assert.True(File.Exists(indexer), $"Expected the indexer at '{indexer}'.");

        var source = SourceGuardScan.ExecutableSourceOf(indexer);

        Assert.DoesNotMatch(ParserInvocation, source);

        // And it does still reach the parse — through the client, which spawns the child. Without
        // this the assertion above would pass just as happily on an indexer that had dropped the
        // parse altogether.
        Assert.Matches(new Regex(@"SpecIndexWorkerClient\s*\.\s*ParseAsync\s*\(", RegexOptions.Compiled), source);
    }

    [Fact]
    public void TheInvocationPattern_MatchesCallsAndNotDeclarationsOrMentions()
    {
        // Sanity check for the regex above, because the whole guard is only as good as it is.
        Assert.Matches(ParserInvocation, "entry = SpecIndexParser.Parse(i, paths[i]);");
        Assert.Matches(ParserInvocation, "var e = SpecIndexParser\n    .Parse(0, path);");

        // The declaration itself lives in SpecIndexParser.cs and is deliberately not a call site —
        // otherwise the type that DEFINES the parse would have to name itself in the allow-list.
        Assert.DoesNotMatch(ParserInvocation, "public static SpecIndexWorkerEntry Parse(int index, string path)");

        // An unqualified Parse elsewhere in the assembly is not this parser.
        Assert.DoesNotMatch(ParserInvocation, "JsonDocument.Parse(stdout)");
    }
}
