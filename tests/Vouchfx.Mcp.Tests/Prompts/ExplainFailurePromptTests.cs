using Vouchfx.Mcp.Prompts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// US-S5-04: the <c>explain_failure</c> prompt — one step's outcome, in ≤ 200 words, for a developer
/// who has never used vouchfx.
/// </summary>
public class ExplainFailurePromptTests
{
    private const string PromptName = "explain_failure";

    internal static string Render(string runId = "run-42", string stepId = "check-balance") =>
        PromptRepository.Get(PromptName).Render(
            new Dictionary<string, string?> { ["runId"] = runId, ["stepId"] = stepId });

    internal static IEnumerable<string> AllRenderings()
    {
        yield return Render();
        yield return Render("run-7", "await-settlement-row");
    }

    [Fact]
    public void ThePrompt_DeclaresBothArgumentsRequired()
    {
        var prompt = PromptRepository.Get(PromptName);

        Assert.Equal("explain_failure", prompt.Name);
        Assert.Equal(["runId", "stepId"], prompt.Arguments.Select(argument => argument.Name));
        Assert.All(prompt.Arguments, argument => Assert.True(argument.Required));
        Assert.All(prompt.Arguments, argument => Assert.Null(argument.Default));
    }

    [Theory]
    [InlineData(null, "check-balance")]
    [InlineData("run-42", null)]
    [InlineData(null, null)]
    public void AMissingRequiredArgument_IsRefused(string? runId, string? stepId)
    {
        var arguments = new Dictionary<string, string?>();
        if (runId is not null)
        {
            arguments["runId"] = runId;
        }

        if (stepId is not null)
        {
            arguments["stepId"] = stepId;
        }

        Assert.Throws<PromptArgumentException>(() => PromptRepository.Get(PromptName).Render(arguments));
    }

    [Fact]
    public void BothArgumentsAreEchoed()
    {
        var rendered = Render();

        Assert.Contains("run-42", rendered, StringComparison.Ordinal);
        Assert.Contains("check-balance", rendered, StringComparison.Ordinal);
    }

    // ── Gherkin 3: length bound and taxonomy vocabulary ────────────────────────────────────────

    [Fact]
    public void TheInstructionIsLengthBoundedAtTwoHundredWords()
    {
        var rendered = Render();

        PromptTextAssertions.AssertPhrasePresent("200 words or fewer", rendered);
    }

    [Fact]
    public void TheOutcomeIsExplainedWithTheResponseStringVocabulary_NeverTheWireTokens()
    {
        var rendered = Render();

        // sprint-00-overview.md §5: a host-facing surface uses the response strings. All four, because
        // the explanation has to be able to describe whichever one the step actually got.
        foreach (var outcome in new[] { "Pass", "Fail", "EnvironmentError", "Inconclusive" })
        {
            Assert.Contains(outcome, rendered, StringComparison.Ordinal);
        }

        // And NEVER the engine's own wire tokens — those belong to get_run_events' raw relay, and a
        // developer being introduced to vouchfx must not be handed two vocabularies for one idea.
        foreach (var wireToken in new[] { "PASS", "FAIL", "ENV_ERROR", "INCONCLUSIVE" })
        {
            Assert.DoesNotContain(wireToken, rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheAudienceIsStatedExplicitly() =>
        // "to a developer unfamiliar with vouchfx" is the AC's own framing, and it is what makes the
        // difference between this prompt and explain_run's own output.
        PromptTextAssertions.AssertPhrasePresent("unfamiliar with vouchfx", Render());

    [Fact]
    public void TheThreeThingsToExplain_AreAllNamed()
    {
        var rendered = Render();

        PromptTextAssertions.AssertPhrasePresent("attempted", rendered);
        PromptTextAssertions.AssertPhrasePresent("observed", rendered);
        PromptTextAssertions.AssertPhrasePresent("what the outcome means", rendered);
    }

    // ── The signature adaptation (US-S5-03's lesson, applied) ──────────────────────────────────

    /// <summary>
    /// The procedure resolves the arguments <c>get_step_timeline</c> actually needs before calling it.
    /// </summary>
    /// <remarks>
    /// <b>MEASURED against the tool: <c>get_step_timeline</c> takes THREE required arguments —
    /// <c>runId</c>, <c>specPath</c> and <c>stepId</c>.</b> This prompt is given only two of them, so
    /// a procedure that called it directly would fail on a missing argument. <c>specPath</c> is
    /// resolved from <c>get_run_status</c>'s <c>specPaths</c>, which is exactly what that tool's own
    /// argument description tells a caller to do — and it must be one of the paths the run covered, or
    /// the call is refused with VFX-E-1509.
    /// <para>
    /// This is the same class of AC-vs-contract gap US-S5-03 hit with <c>diagnose_run</c>, found the
    /// same way: by reading the signature before writing the procedure.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheProcedureResolvesSpecPathBeforeCallingGetStepTimeline()
    {
        var rendered = Render();

        Assert.Contains("get_run_status", rendered, StringComparison.Ordinal);
        Assert.Contains("specPaths", rendered, StringComparison.Ordinal);
        Assert.Contains("specPath", rendered, StringComparison.Ordinal);
        Assert.Contains("get_step_timeline", rendered, StringComparison.Ordinal);

        // Order: the resolution must precede the call that needs it.
        Assert.True(
            rendered.IndexOf("get_run_status", StringComparison.Ordinal)
            < rendered.IndexOf("get_step_timeline", StringComparison.Ordinal),
            "get_run_status must be named before get_step_timeline, which needs its specPaths.");
    }

    [Fact]
    public void AnUnknownStepIdIsExplainedAsARefusal_NotAnEmptyTimeline()
    {
        var rendered = Render();

        // The tool refuses an unrecorded step id with VFX-E-1510 rather than answering with an empty
        // timeline — a deliberate design (a step with no attempts is a real and different success), and
        // one a host will otherwise read as a bug in its own call.
        Assert.Contains("VFX-E-1510", rendered, StringComparison.Ordinal);
        PromptTextAssertions.AssertPhrasePresent("empty timeline", rendered);
    }

    [Fact]
    public void EveryCodeTheProcedureCites_IsARealCataloguedCode()
    {
        var rendered = Render();

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(rendered, @"VFX-[DE]-\d{4}"))
        {
            Assert.Contains(
                match.Value, Vouchfx.Mcp.Contracts.VfxCodeCatalogue.All.Select(entry => entry.Code));
        }
    }

    [Fact]
    public void TheAttemptOutcomeVocabularyIsTheToolsOwn_NotTheVerdictTaxonomy()
    {
        var rendered = Render();

        // get_step_timeline's per-attempt `outcome` is its OWN enum — matched/unmatched/error — never
        // the four-value verdict taxonomy. Conflating them is the exact confusion this prompt exists
        // to prevent in a reader, so the prompt itself must not make it.
        foreach (var attemptOutcome in new[] { "matched", "unmatched" })
        {
            Assert.Contains(attemptOutcome, rendered, StringComparison.Ordinal);
        }
    }

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
    public void EveryRendering_IsSubstantialAndFullySubstituted()
    {
        foreach (var rendered in AllRenderings())
        {
            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.True(rendered.Length > 900, $"Rendered prompt is implausibly short ({rendered.Length} chars).");
            Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheVerdictTaxonomyItExplains_IsTheServersOwn() =>
        // Anti-drift: the four response strings the prompt teaches are RunVerdict's own names, so a
        // future rename of the taxonomy cannot leave this prompt teaching a vocabulary the server no
        // longer speaks.
        Assert.All(
            Enum.GetNames<RunVerdict>(),
            name => Assert.Contains(name, Render(), StringComparison.Ordinal));
}
