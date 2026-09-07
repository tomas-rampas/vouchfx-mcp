using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// US-S5-04: the <c>review_spec</c> prompt — a pre-flight checklist for an existing suite.
/// </summary>
/// <remarks>
/// The advertised-surface cross-check needs a live harness and therefore lives in
/// <c>RealReviewSpecPromptMcpTests</c>, not here. Shared text machinery is
/// <see cref="PromptTextAssertions"/>'.
/// </remarks>
public class ReviewSpecPromptTests
{
    private const string PromptName = "review_spec";
    private const string SamplePath = "e2e/provision-customer.e2e.yaml";

    internal static string Render(string path = SamplePath) =>
        PromptRepository.Get(PromptName).Render(new Dictionary<string, string?> { ["path"] = path });

    /// <summary>Every render shape this prompt has — one, since its only argument is required.</summary>
    internal static IEnumerable<string> AllRenderings()
    {
        yield return Render();
        yield return Render("e2e/checkout/place-order.e2e.yaml");
    }

    [Fact]
    public void ThePrompt_DeclaresPathAsItsOnlyRequiredArgument()
    {
        var prompt = PromptRepository.Get(PromptName);

        Assert.Equal("review_spec", prompt.Name);
        var path = Assert.Single(prompt.Arguments);
        Assert.Equal("path", path.Name);
        Assert.True(path.Required);
        Assert.Null(path.Default);
        Assert.False(string.IsNullOrWhiteSpace(path.Description));
    }

    [Fact]
    public void AMissingPath_IsRefused() =>
        Assert.Throws<PromptArgumentException>(
            () => PromptRepository.Get(PromptName).Render(new Dictionary<string, string?>()));

    [Fact]
    public void ThePathIsEchoed() =>
        Assert.Contains(SamplePath, Render(), StringComparison.Ordinal);

    // ── Gherkin 1: every named review category appears ─────────────────────────────────────────

    /// <summary>
    /// The seven categories the AC names, each as a phrase the rendered checklist must carry.
    /// </summary>
    /// <remarks>
    /// Re-typed from the AC rather than derived from anything in <c>src/</c>: three of the seven are
    /// checked mechanically by <c>validate_suite</c> and four are not, so no single production
    /// constant owns this list — and deriving it from the half that exists would silently drop the
    /// half that needs human judgment, which is the half this prompt is mostly for.
    /// </remarks>
    public static TheoryData<string> ReviewCategories() =>
    [
        "tautolog",              // assertions that cannot fail
        "verifyMode: RETRY",     // missing RETRY on async steps
        "hard-coded id",
        "negative-path",
        "coverage gap",
        "unused capture",
        "secret literal",
    ];

    [Theory]
    [MemberData(nameof(ReviewCategories))]
    public void TheChecklistCoversEveryNamedCategory(string category) =>
        PromptTextAssertions.AssertPhrasePresent(category, Render());

    [Fact]
    public void TheOutputIsAChecklistWithSeverityAndConcreteEdits()
    {
        var rendered = Render();

        // The AC's own three words for the deliverable. A review that produced prose rather than a
        // checklist with severities would be a different (and less usable) thing.
        PromptTextAssertions.AssertPhrasePresent("checklist", rendered);
        PromptTextAssertions.AssertPhrasePresent("severity", rendered);
        PromptTextAssertions.AssertPhrasePresent("concrete edit", rendered);
    }

    // ── Gherkin 2: plan_coverage replaces the retired get_topology ─────────────────────────────

    [Theory]
    [MemberData(nameof(PromptTextAssertions.BannedIdentifiers), MemberType = typeof(PromptTextAssertions))]
    public void ARetiredIdentifier_NeverAppearsInTheRenderedText(string banned)
    {
        foreach (var rendered in AllRenderings())
        {
            PromptTextAssertions.AssertIdentifierAbsent(banned, rendered);
        }
    }

    [Fact]
    public void PlanCoverageIsNamedAsTheCoverageGapSource()
    {
        var rendered = Render();

        // The story's explicit substitution: spec §7.3 said `get_topology`, which this repo does not
        // have and this sprint retired. `plan_coverage` is the gap-finding tool it does have.
        Assert.Contains("plan_coverage", rendered, StringComparison.Ordinal);

        Assert.True(
            rendered.Contains("coverage gap", StringComparison.OrdinalIgnoreCase),
            "The checklist does not name coverage gaps.");
    }

    // ── The mechanical/judgment split ──────────────────────────────────────────────────────────

    [Fact]
    public void TheProcedureRunsValidateSuiteFirst_AndSaysWhichCategoriesItAlreadyCovers()
    {
        var rendered = Render();

        // Three of the seven categories are already decided by validate_suite's semantic rules, so a
        // review that re-derived them by eye would be slower AND less reliable than the machine. The
        // prompt says so and names the codes, so a host can tell which findings it is expected to
        // produce itself.
        Assert.Contains("validate_suite", rendered, StringComparison.Ordinal);

        foreach (var code in new[]
        {
            VfxCodeCatalogue.UnusedCapture,
            VfxCodeCatalogue.SecretLiteralInSuite,
            VfxCodeCatalogue.AsyncStepWithoutRetry,
        })
        {
            Assert.Contains(code, rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryCodeTheProcedureCites_IsARealCataloguedCode()
    {
        // A prompt citing VFX-D-1299 would send a host to explain_diagnostic for a page that does not
        // exist. Every VFX-* token in the rendered text is checked against the catalogue.
        var rendered = Render();

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(rendered, @"VFX-[DE]-\d{4}"))
        {
            Assert.Contains(match.Value, VfxCodeCatalogue.All.Select(entry => entry.Code));
        }
    }

    [Fact]
    public void TheReviewIsReadOnly_AndSaysSo()
    {
        var rendered = Render();

        // A review produces recommendations; this server writes nothing and the prompt must not imply
        // the host should either, at this stage.
        PromptTextAssertions.AssertPhrasePresent("never writes", rendered);
    }

    [Fact]
    public void EveryRendering_IsSubstantialAndFullySubstituted()
    {
        foreach (var rendered in AllRenderings())
        {
            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.True(rendered.Length > 1200, $"Rendered prompt is implausibly short ({rendered.Length} chars).");
            Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", rendered, StringComparison.Ordinal);
        }
    }
}
