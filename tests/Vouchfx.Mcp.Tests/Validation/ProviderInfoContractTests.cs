using System.Text.RegularExpressions;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="ProviderInfoContract"/> — US-S2-05's explicit split of spec §5.2's
/// <c>ProviderInfo</c> field list into "this server derives it today" and "this field waits on
/// upstream ask U5".
/// </summary>
/// <remarks>
/// <para>
/// <b>The partition test is the point of this file.</b> The story's requirement is not merely that
/// the gated fields are absent — it is that the split is a stated, checkable fact rather than an
/// emergent property of whichever fields somebody remembered to populate. A field added to spec
/// §5.2 that lands in neither set, or in both, fails here; and when U5 actually ships, moving a
/// field out of the gated set is a deliberate edit to a named constant with a test watching it,
/// not a silent behaviour change.
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
    public void DerivedAndU5GatedSets_PartitionTheSpecFieldList()
    {
        // Disjoint: no field may be claimed as both derived and gated.
        Assert.Empty(ProviderInfoContract.DerivedToday.Intersect(ProviderInfoContract.U5Gated, StringComparer.Ordinal));

        // Exhaustive: every spec field is accounted for by exactly one side of the split.
        var union = ProviderInfoContract.DerivedToday
            .Concat(ProviderInfoContract.U5Gated)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            SpecSection52Fields.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            union);
    }

    [Fact]
    public void U5GatedSet_IsExactlyTheFiveFieldsThisServerCannotDeriveToday()
    {
        // sprint-00-overview.md §3 lists SIX fields under ask U5: tier, vouched, requiredResources,
        // supportsVerifyMode, example, docsUrl. US-S2-05 removes requiredResources from that list —
        // it IS derivable here today, from the vendored schema's step-type set crossed with
        // UndeclaredDependencyRule's step-type -> dependency-kind table (see
        // RequiredResourceCatalogueTests). The remaining five stay gated. This assertion is the
        // record of that decision: a future U5 landing must edit it consciously.
        Assert.Equal(
            ["docsUrl", "example", "supportsVerifyMode", "tier", "vouched"],
            ProviderInfoContract.U5Gated.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void U5PendingNotice_NamesTheAskAndEveryGatedField()
    {
        var notice = ProviderInfoContract.U5PendingNotice;

        Assert.Contains("U5", notice, StringComparison.Ordinal);

        foreach (var field in ProviderInfoContract.U5Gated)
        {
            Assert.Contains(field, notice, StringComparison.Ordinal);
        }

        // The notice must not name a field it does not gate — a host reading it would otherwise
        // stop looking for a field this server does populate. Matched on word boundaries, so the
        // sentence may still use a gated field's name as part of a longer word (e.g. the type name
        // "ProviderInfo" is not a claim about the "provider" field).
        foreach (var field in ProviderInfoContract.DerivedToday)
        {
            Assert.False(
                Regex.IsMatch(notice, $@"\b{Regex.Escape(field)}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
                $"The U5 notice names '{field}', which this server DOES derive today.");
        }
    }

    [Fact]
    public void DocsUrl_IsGated_BecauseNoProviderPageConventionExistsInThisRepo()
    {
        // sprint-00-overview.md §4 risk 6 / US-S2-05's third acceptance criterion: docsUrl would be
        // derivable IF a docs/providers/{family}.{provider}.md convention existed. It does not —
        // measured below against the repository itself — so the field stays omitted rather than
        // pointing every provider at a 404.
        // Cast to IEnumerable<string> so ONE Assert.Contains overload applies: a FrozenSet satisfies
        // both Assert.Contains<T>(T, ISet<T>) and its IReadOnlySet counterpart, which is ambiguous.
        Assert.Contains("docsUrl", (IEnumerable<string>)ProviderInfoContract.U5Gated);

        var docsDirectory = Path.Combine(RepoRoot.FullName, "docs");
        // Anti-vacuity: a broken repo-root walk would make the assertion below pass for the wrong
        // reason (nothing exists under a path that is not the repo).
        Assert.True(Directory.Exists(docsDirectory), $"Expected '{docsDirectory}' to exist — the repo-root walk is broken.");

        var providersDirectory = Path.Combine(docsDirectory, "providers");
        Assert.False(
            Directory.Exists(providersDirectory),
            $"'{providersDirectory}' now exists. The per-provider docs convention US-S2-05 recorded as "
            + "absent has landed: derive docsUrl from it and move the field out of "
            + "ProviderInfoContract.U5Gated.");
    }

    [Fact]
    public void DocsBlockquotes_NameEveryGatedField_AndNoDerivedOne()
    {
        // m2 (second-reviewer follow-up): the published tools-and-resources page's "Deliberately
        // absent" blockquotes are a THIRD copy of the U5 list, after ProviderInfoContract.U5Gated
        // and each catalogue tool's own description. Precedent: ErrorCatalogueFilesystemParityTests
        // gates the docs/errors pages against the code catalogue. Without this, U5 actually landing
        // (a field leaving U5Gated) would leave the published site still telling readers the field
        // is pending — wrong, and untested. This binds the site to the constant: every gated field
        // must be named in the page, and no derived field may sit inside a gated blockquote.
        var docPath = Path.Combine(RepoRoot.FullName, "docs", "tools-and-resources.md");
        Assert.True(File.Exists(docPath), $"Expected '{docPath}' to exist — the repo-root walk is broken.");
        var text = File.ReadAllText(docPath);

        foreach (var gated in ProviderInfoContract.U5Gated)
        {
            Assert.Contains(gated, text, StringComparison.Ordinal);
        }

        // Contiguous runs of blockquote ('>') lines whose joined text names the gated list.
        var gatedBlockquotes = GatedBlockquotes(text);
        Assert.NotEmpty(gatedBlockquotes);

        foreach (var derived in ProviderInfoContract.DerivedToday)
        {
            foreach (var block in gatedBlockquotes)
            {
                // Word boundaries, ordinal/case-sensitive — so "provider" does not match "ProviderInfo"
                // (the record's name, which every gated blockquote legitimately cites).
                Assert.False(
                    Regex.IsMatch(block, $@"\b{Regex.Escape(derived)}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
                    $"A 'Deliberately absent' blockquote names '{derived}', which this server DOES "
                    + "derive today — a field that left U5Gated but not the docs.");
            }
        }
    }

    /// <summary>
    /// The "Deliberately absent" blockquotes in <paramref name="markdown"/>: each a contiguous run
    /// of blockquote (<c>&gt;</c>) lines whose joined text names the gated list.
    /// </summary>
    private static List<string> GatedBlockquotes(string markdown)
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
