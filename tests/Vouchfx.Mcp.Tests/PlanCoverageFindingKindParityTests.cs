using System.Text.RegularExpressions;
using Vouchfx.Mcp.Planning;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A bidirectional parity gate between the finding kinds <c>PlanCoverageResponseBudget</c> ranks for
/// truncation and the kinds <c>docs/tools-and-resources.md</c> publishes as <c>plan_coverage</c>'s
/// vocabulary — the same "derive both directions from disk, fail on either" shape
/// <see cref="ErrorCatalogueFilesystemParityTests"/> uses for codes and their pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this gate is needed: the failure it catches is SILENT.</b> The kind vocabulary belongs to
/// the engine, not to this server. If an upstream release renames (say) <c>step-flaky</c>, nothing
/// here breaks loudly — the old spelling simply stops matching, and
/// <c>PlanCoverageResponseBudget</c>'s lookup falls through to its unrecognised-kind band. That is a
/// deliberately safe fallback (band 4 still keeps an unknown kind ahead of the two flood kinds), and
/// that is exactly the problem: a <c>step-flaky</c> quietly demoted out of band 1 changes WHICH
/// findings survive truncation on every large repository, and no existing test would notice. Tying
/// the table to the documented list makes the rename fail at the moment someone updates the docs —
/// the earliest point at which this repository knows about it at all.
/// </para>
/// <para>
/// <b>Direction 2 is the one that catches a partial edit.</b> Adding a kind to the docs without
/// ranking it leaves it silently at band 4; ranking one that is not documented means the published
/// vocabulary is incomplete. Both are real drift, so both fail.
/// </para>
/// <para>
/// <b>This gate does NOT claim the documented list matches the ENGINE.</b> Nothing in this repository
/// can assert that offline — the engine's kind vocabulary is not in the vendored artefacts, and
/// <c>RealPlanCoverageAgainstPinnedCliTests</c> only observes the kinds one fixture happens to
/// produce. What this pins is that the two places THIS repository states the vocabulary agree with
/// each other, so a maintainer updating one is forced to update the other.
/// </para>
/// </remarks>
public class PlanCoverageFindingKindParityTests
{
    /// <summary>
    /// Matches the backtick-quoted kind tokens in the "Finding kinds" bullet of
    /// <c>docs/tools-and-resources.md</c>. Deliberately narrow — all-lowercase segments joined by at
    /// least one hyphen — and that narrowness IS the filter: every other backtick token the bullet
    /// carries is either camelCase (<c>suggestedTypes</c>, <c>suggestedStepId</c>) or
    /// snake_case (<c>scaffold_suite</c>), so none of them can match, and no allow-list is needed to
    /// exclude them. A reviewed-benign exception set was removed from here because it was vacuous:
    /// not one of its entries was matchable by this pattern in the first place.
    /// </summary>
    private static readonly Regex KindTokenPattern = new(@"`([a-z]+(?:-[a-z]+)+)`", RegexOptions.Compiled);

    [Fact]
    public void EveryRankedFindingKind_IsPublishedInTheToolDocs()
    {
        var documented = DocumentedFindingKinds();
        var ranked = PlanCoverageResponseBudget.RankedFindingKinds.ToHashSet(StringComparer.Ordinal);

        // Anti-vacuity: a docs parse that silently matched nothing would make both directions pass by
        // finding nothing to compare — the guard ErrorCatalogueFilesystemParityTests states for its
        // own derived sets.
        Assert.NotEmpty(documented);
        Assert.NotEmpty(ranked);

        var unpublished = ranked.Except(documented).OrderBy(kind => kind, StringComparer.Ordinal).ToArray();

        Assert.True(
            unpublished.Length == 0,
            "PlanCoverageResponseBudget ranks finding kinds that docs/tools-and-resources.md's "
            + "'Finding kinds' bullet does not list: " + string.Join(", ", unpublished)
            + ". Either the docs are stale, or the rank table is ranking a kind that no longer exists "
            + "upstream (in which case the real kind is silently sorting into the unrecognised band).");
    }

    [Fact]
    public void EveryDocumentedFindingKind_IsRankedForTruncation()
    {
        var documented = DocumentedFindingKinds();
        var ranked = PlanCoverageResponseBudget.RankedFindingKinds.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(documented);
        Assert.NotEmpty(ranked);

        var unranked = documented.Except(ranked).OrderBy(kind => kind, StringComparer.Ordinal).ToArray();

        Assert.True(
            unranked.Length == 0,
            "docs/tools-and-resources.md publishes finding kinds that PlanCoverageResponseBudget does "
            + "not rank: " + string.Join(", ", unranked)
            + ". They would sort into the unrecognised band rather than the band their meaning "
            + "warrants. Add them to FindingPriorityByKind.");
    }

    /// <summary>
    /// The kinds named in the "Finding kinds" bullet of <c>plan_coverage</c>'s docs section, read
    /// from disk rather than from an embedded copy — for the reason
    /// <see cref="ErrorCatalogueFilesystemParityTests"/> gives: an embedded snapshot can lag the file
    /// a maintainer just edited.
    /// </summary>
    private static HashSet<string> DocumentedFindingKinds()
    {
        var docsPath = Path.Combine(SourceGuardScan.RepoRoot.FullName, "docs", "tools-and-resources.md");
        var lines = File.ReadAllLines(docsPath);

        var bulletStart = Array.FindIndex(lines, line => line.StartsWith("- **Finding kinds**", StringComparison.Ordinal));
        Assert.True(
            bulletStart >= 0,
            $"Could not find the '- **Finding kinds**' bullet in {docsPath}. If that bullet was "
            + "renamed, update this test rather than deleting it — it is the only thing pinning the "
            + "truncation rank table to the published vocabulary.");

        // The bullet runs until the next top-level bullet; its continuation lines are indented.
        var bulletText = string.Join(
            ' ',
            lines.Skip(bulletStart)
                .TakeWhile((line, index) => index == 0 || line.StartsWith("  ", StringComparison.Ordinal)));

        return KindTokenPattern.Matches(bulletText)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
