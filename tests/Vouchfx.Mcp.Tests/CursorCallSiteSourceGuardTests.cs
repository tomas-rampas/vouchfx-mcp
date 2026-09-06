using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for the sprint's "one cursor implementation, verified by a shared
/// unit-test fixture, not two" exit-checklist item: every <see cref="Vouchfx.Mcp.Run.OpaqueCursor"/>
/// encode/decode call site in <c>src/</c> is one of the two paginated orchestrators, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the checklist item needed a TEST rather than a review note.</b> The shared fixture
/// (<c>Run/OpaqueCursorContract</c>) proves the two tools' cursors behave identically; it says
/// nothing about a THIRD tool that hand-rolls its own encoding beside them. That is the failure the
/// checklist actually guards against — "not two independently-written cursor encodings" — and it is
/// invisible to every other gate in this repo: a bespoke base64 position in some future
/// <c>list_artifacts</c> would build, format, and pass its own tests perfectly happily. This
/// converts the item from a thing a reviewer must remember into a thing CI enforces, which is what
/// US-S3-03's carry-in asked for.
/// </para>
/// <para>
/// <b>What it can and cannot catch, stated honestly.</b> It is a boundary on WHERE cursor code may
/// live, not a proof that no other pagination scheme exists: a tool that invented a plaintext
/// <c>"offset=42"</c> token would never call <see cref="Vouchfx.Mcp.Run.OpaqueCursor"/> at all and so
/// would not appear here. What this does guarantee is the direction that matters in practice — a new
/// paginated tool cannot quietly acquire a SECOND encoding of the same concept while the first is
/// sitting in the same directory, because the moment it uses the shared type it must be named here,
/// and the moment it does not, its own review has to explain why.
/// </para>
/// <para>
/// <b>Mirrors <see cref="RunLockSourceGuardTests"/>' and <see cref="ReadOnlySourceGuardTests"/>'
/// shape exactly</b> — a whitespace-tolerant regex over source with comments and string literals
/// stripped, and a fail-closed EXACT-equality check against a named set, so a new call site fails by
/// name and a stale entry cannot rot.
/// </para>
/// </remarks>
public class CursorCallSiteSourceGuardTests
{
    /// <summary>
    /// The only files in <c>src/</c> allowed to mint or verify a cursor: the two paginated
    /// orchestrators, one per <c>CursorScopes</c> constant.
    /// </summary>
    private static readonly string[] GuardedCallSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Run/GetRunEventsOrchestrator.cs",
        "src/Vouchfx.Mcp/Run/ListRunsOrchestrator.cs",
    ];

    /// <summary>
    /// An INVOCATION of either half of the cursor contract, qualified by its owning type. Matching
    /// <c>OpaqueCursor.Encode</c>/<c>OpaqueCursor.TryDecode</c> rather than the bare method names
    /// keeps the pattern from firing on unrelated <c>Encode</c>/<c>TryDecode</c> members elsewhere in
    /// the assembly, and — because <c>OpaqueCursor</c> is a static class — every real call site
    /// necessarily spells the type out.
    /// </summary>
    private static readonly Regex CursorInvocation =
        new(@"OpaqueCursor\s*\.\s*(Encode|TryDecode)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void TheOpaqueCursor_HasExactlyTheTwoPaginatedOrchestratorsAsCallSitesInSrc()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => CursorInvocation.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            GuardedCallSiteRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void EveryNamedCallSite_StillExistsAndStillUsesTheSharedCursor()
    {
        // Anti-vacuity in both directions: a renamed or deleted file would make the set check above
        // pass over nothing, and a file that stopped using the shared type would silently narrow what
        // this guard covers without failing anything.
        foreach (var relativePath in GuardedCallSiteRelativePaths)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            Assert.Matches(CursorInvocation, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }

    [Fact]
    public void TheInvocationPattern_MatchesCallsAndNotDeclarationsOrMentions()
    {
        // Sanity check for the regex above, because the whole guard is only as good as it is.
        Assert.Matches(CursorInvocation, "var cursor = OpaqueCursor.Encode(scope, binding, position);");
        Assert.Matches(CursorInvocation, "if (!OpaqueCursor\n    .TryDecode(cursor, scope, binding, out var p, out var r))");

        // The declarations themselves live in OpaqueCursor.cs and are deliberately not call sites —
        // otherwise the type that DEFINES the contract would have to name itself in the allow-list.
        Assert.DoesNotMatch(CursorInvocation, "public static string Encode(string scope, string binding, long position)");
        Assert.DoesNotMatch(CursorInvocation, "public static bool TryDecode(");

        // A prose mention of the type (which comments are stripped of before matching anyway) is not
        // a call.
        Assert.DoesNotMatch(CursorInvocation, "OpaqueCursor is THE cursor implementation for this server");
    }
}
