using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level guard for <c>Resources/VouchfxResourceUris.cs</c>'s central claim — "a URI is a
/// PUBLISHED CONTRACT a host caches against, so it must have exactly one spelling in this codebase" —
/// which until issue #87 was a comment asking to be believed.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed and why a guard became worth writing.</b> While every <c>vouchfx://</c> string was
/// consumed by a resource REGISTRY, the single-spelling rule was easy to keep by accident: the
/// registries sit in one directory and read one file. Issue #87 put a <c>resourceUri</c> into two TOOL
/// payloads (<c>get_run_artifacts</c>' <c>reports.events</c> and <c>get_run_events</c>' own), which
/// makes a second kind of party — an orchestrator, in a different directory, with no reason to think
/// about resource listings — a publisher of the same string. An interpolated
/// <c>$"vouchfx://runs/{runId}/events"</c> written there would build, format, pass its own tests, and
/// ship a URI that silently stops matching the advertised template the day the template changes.
/// </para>
/// <para>
/// <b>The rule, stated exactly.</b> The literal text <c>vouchfx://</c> may appear in EXECUTABLE source
/// (string literals included) in one file only. Everything else refers to a constant or calls the
/// expander on that file. Prose is unaffected — comments are stripped before matching, so the dozen
/// doc comments across <c>src/</c> that mention a URI by name stay legal and stay useful.
/// </para>
/// <para>
/// <b>Scanned with string literals INTACT</b>
/// (<see cref="SourceGuardScan.SourceWithCommentsStrippedOnly"/>), unlike the call-site guards, and
/// that is the whole point: the forbidden shape here IS a literal. The ordinary
/// comments-and-literals-stripping scan blanks exactly the text this guard exists to find — the same
/// correction <see cref="StructuredLogHygieneSourceGuardTests"/> records.
/// </para>
/// <para>
/// <b>What it does not catch, stated honestly.</b> A URI assembled from pieces
/// (<c>"vouchfx" + "://" + …</c>) would slip past, as would one built from a constant declared
/// elsewhere. This is a boundary on the obvious and overwhelmingly likely failure — someone typing the
/// URI where they need it — not a proof of impossibility. The <c>vouchfx-docs:///</c> scheme is
/// deliberately out of scope: it is Sprint 1's frozen vendored-document scheme, its two registries own
/// their own templates, and retro-fitting them is plan D4's explicit non-goal.
/// </para>
/// </remarks>
public class VouchfxResourceUriSourceGuardTests
{
    /// <summary>
    /// The ONE file in <c>src/</c> allowed to spell a <c>vouchfx://</c> URI in executable source: the
    /// catalogue that declares every template and expands the one that gets published.
    /// </summary>
    private const string UriAuthorityRelativePath = "src/Vouchfx.Mcp/Resources/VouchfxResourceUris.cs";

    /// <summary>The scheme prefix, as it is written in a literal.</summary>
    private static readonly Regex VouchfxUriLiteral =
        new(@"vouchfx://", RegexOptions.Compiled);

    [Fact]
    public void TheVouchfxUriScheme_IsSpelledInExactlyOneFileInSrc()
    {
        var actual = SourceGuardScan.SourceFilesInSrc()
            .Where(path => VouchfxUriLiteral.IsMatch(SourceGuardScan.SourceWithCommentsStrippedOnly(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([UriAuthorityRelativePath], actual);
    }

    [Fact]
    public void TheAuthorityFile_StillExistsAndStillDeclaresTheScheme()
    {
        // Anti-vacuity: a renamed or emptied authority file would make the set check above pass over
        // nothing at all, which is the one way a fail-closed guard fails open.
        var fullPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName,
            UriAuthorityRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(fullPath),
            $"Expected the URI authority at '{fullPath}' — update this guard if it moved.");

        Assert.Matches(VouchfxUriLiteral, SourceGuardScan.SourceWithCommentsStrippedOnly(fullPath));
    }

    [Fact]
    public void TheScan_SeesLiteralsAndIgnoresProse()
    {
        // The guard is only as good as what it reads, so both halves are pinned here rather than
        // assumed: a URI inside a string (including an interpolated one — the shape an orchestrator
        // would most plausibly write) is visible, and the same URI in a comment is not.
        const string interpolatedLiteral = """
            var uri = $"vouchfx://runs/{runId}/events";
            """;
        Assert.Matches(VouchfxUriLiteral, SourceGuardScan.StripCommentsOnly(interpolatedLiteral));

        const string prose = """
            // vouchfx://runs/{runId}/events is served by RunResourceRegistry.
            /* and vouchfx://docs/dsl-guide by DslGuideResourceRegistry. */
            """;
        Assert.DoesNotMatch(VouchfxUriLiteral, SourceGuardScan.StripCommentsOnly(prose));
    }
}
