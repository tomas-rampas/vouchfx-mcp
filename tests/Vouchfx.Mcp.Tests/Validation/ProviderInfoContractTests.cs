using System.Text.RegularExpressions;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="ProviderInfoContract"/> — US-S2-05's explicit split of spec §5.2's
/// <c>ProviderInfo</c> field list into "the catalogue tools populate it" and "it belongs to the
/// provider hub, so they deliberately never do".
/// </summary>
/// <remarks>
/// <para>
/// <b>The partition test is the point of this file.</b> The story's requirement is not merely that
/// an underivable field is absent — it is that the split is a stated, checkable fact rather than an
/// emergent property of whichever fields somebody remembered to populate. A field added to spec
/// §5.2 that lands in neither set, or in both, fails here; and moving a field across is a
/// deliberate edit to a named constant with a test watching it, not a silent behaviour change.
/// That is how the engine v1.0.0-rc.6 repin moved four fields: upstream ask U5 delivered
/// <c>tier</c>, <c>supportsVerifyMode</c>, <c>example</c> and <c>docsUrl</c>, and the fifth field
/// this file used to hold back, <c>vouched</c>, turned out never to be an engine fact at all.
/// </para>
/// </remarks>
public class ProviderInfoContractTests
{
    /// <summary>
    /// Spec §5.2's <c>ProviderInfo</c> interface, transcribed here in declaration order.
    /// </summary>
    /// <remarks>
    /// Deliberately a SECOND, independent copy of the field list rather than a read of
    /// <see cref="ProviderInfoContract.SpecFields"/>: this test's job is to catch the production
    /// list drifting away from the spec, and a test that read the production list could only ever
    /// agree with it. Update this copy only when <c>specs/vouchfx-ai-mcp-spec.md</c> §5.2 itself
    /// changes.
    /// </remarks>
    private static readonly string[] SpecSection52Fields =
    [
        "stepType",
        "family",
        "provider",
        "tier",
        "vouched",
        "summary",
        "parameters",
        "supportsVerifyMode",
        "requiredResources",
        "example",
        "docsUrl",
    ];

    [Fact]
    public void SpecFields_MatchSpecSection52Verbatim()
    {
        Assert.Equal(SpecSection52Fields, ProviderInfoContract.SpecFields);
    }

    [Fact]
    public void DerivedAndHubOwnedSets_PartitionTheSpecFieldList()
    {
        // Disjoint: no field may be claimed as both populated and deliberately absent.
        Assert.Empty(ProviderInfoContract.DerivedToday.Intersect(ProviderInfoContract.HubOwned, StringComparer.Ordinal));

        // Exhaustive: every spec field is accounted for by exactly one side of the split.
        var union = ProviderInfoContract.DerivedToday
            .Concat(ProviderInfoContract.HubOwned)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            SpecSection52Fields.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            union);
    }

    [Fact]
    public void HubOwnedSet_IsExactlyVouched_NowThatUpstreamAskU5HasLanded()
    {
        // The record of two decisions, each of which a future edit must make consciously.
        //
        // 1. sprint-00-overview.md §3 listed SIX fields under ask U5. US-S2-05 derived
        //    requiredResources locally (RequiredResourceCatalogueTests), leaving five; engine
        //    v1.0.0-rc.6 (vouchfx#556) then delivered four of those five on `list --json`, which
        //    StepCatalogueParser relays.
        // 2. The fifth, vouched, the engine deliberately does NOT emit: the Vouched badge endorses a
        //    specific version of a Community provider and lives in the provider hub's registry, which
        //    the engine does not read. It is absent here for that reason, not pending anything.
        Assert.Equal(["vouched"], ProviderInfoContract.HubOwned.ToArray());

        foreach (var delivered in new[] { "tier", "supportsVerifyMode", "example", "docsUrl" })
        {
            // Cast to IEnumerable<string> so ONE Assert.Contains overload applies: a FrozenSet
            // satisfies both Assert.Contains<T>(T, ISet<T>) and its IReadOnlySet counterpart, which
            // is ambiguous.
            Assert.Contains(delivered, (IEnumerable<string>)ProviderInfoContract.DerivedToday);
        }
    }

    [Fact]
    public void AbsentFieldsNotice_NamesEveryHubOwnedField_AndNoPopulatedOne()
    {
        var notice = ProviderInfoContract.AbsentFieldsNotice;

        foreach (var field in ProviderInfoContract.HubOwned)
        {
            Assert.Contains(field, notice, StringComparison.Ordinal);
        }

        // The notice must not name a field the tools DO populate — a host reading it would otherwise
        // stop looking for that field. Matched on word boundaries, so the sentence may still use a
        // field's name as part of a longer word (e.g. the record name "ProviderInfo", or the plural
        // "providers", is not a claim about the "provider" field).
        foreach (var field in ProviderInfoContract.DerivedToday)
        {
            Assert.False(
                Regex.IsMatch(notice, $@"\b{Regex.Escape(field)}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
                $"The absent-field notice names '{field}', which the catalogue tools DO populate.");
        }

        // U5 has landed: a notice still describing the absence as pending that ask is the stale
        // claim the rc.6 repin removed.
        Assert.DoesNotContain("U5", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("pending", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocsBlockquotes_NameEveryHubOwnedField_AndNoPopulatedOne()
    {
        // m2 (second-reviewer follow-up): the published tools-and-resources page's "Deliberately
        // absent" blockquotes are a THIRD copy of the absent-field list, after
        // ProviderInfoContract.HubOwned and each catalogue tool's own description. Precedent:
        // ErrorCatalogueFilesystemParityTests gates the docs/errors pages against the code catalogue.
        // Without this, a field changing sides would leave the published site still describing the
        // old split — which is exactly what U5 landing would have done. This binds the site to the
        // constant: every absent field must be named in the page, and no populated field may sit
        // inside an absent-field blockquote.
        var docPath = Path.Combine(RepoRoot.FullName, "docs", "tools-and-resources.md");
        Assert.True(File.Exists(docPath), "Expected docs/tools-and-resources.md to exist — the repo-root walk is broken.");
        var text = File.ReadAllText(docPath);

        foreach (var absent in ProviderInfoContract.HubOwned)
        {
            Assert.Contains(absent, text, StringComparison.Ordinal);
        }

        // Contiguous runs of blockquote ('>') lines whose joined text names the absent list.
        var absentBlockquotes = AbsentFieldBlockquotes(text);
        Assert.NotEmpty(absentBlockquotes);

        foreach (var block in absentBlockquotes)
        {
            foreach (var absent in ProviderInfoContract.HubOwned)
            {
                Assert.Contains(absent, block, StringComparison.Ordinal);
            }

            foreach (var populated in ProviderInfoContract.DerivedToday)
            {
                // Word boundaries, ordinal/case-sensitive — so "provider" does not match
                // "ProviderInfo" (the record's name, which every such blockquote legitimately cites).
                Assert.False(
                    Regex.IsMatch(block, $@"\b{Regex.Escape(populated)}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
                    $"A 'Deliberately absent' blockquote names '{populated}', which the catalogue tools "
                    + "DO populate — a field that changed sides in ProviderInfoContract but not in the docs.");
            }

            Assert.DoesNotContain("U5", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The "Deliberately absent" blockquotes in <paramref name="markdown"/>: each a contiguous run
    /// of blockquote (<c>&gt;</c>) lines whose joined text names the absent-field list.
    /// </summary>
    private static List<string> AbsentFieldBlockquotes(string markdown)
    {
        var blocks = new List<string>();
        var current = new List<string>();

        void Flush()
        {
            if (current.Count > 0)
            {
                var joined = string.Join('\n', current);
                if (joined.Contains("Deliberately absent", StringComparison.Ordinal))
                {
                    blocks.Add(joined);
                }

                current.Clear();
            }
        }

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith('>'))
            {
                current.Add(line);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return blocks;
    }

    /// <summary>Mirrors <c>ErrorCatalogueFilesystemParityTests.RepoRoot</c> exactly — see that property's remarks.</summary>
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
