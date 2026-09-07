using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// US-S5-02: the <c>author_scenario</c> prompt renders THIS repository's actual authoring procedure —
/// and never names a tool this server does not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every assertion is against the RENDERED text</b>, not the template. A banned identifier could
/// enter through a substituted argument value or a section block just as easily as through the body,
/// and only the rendered output is what a host actually reads.
/// </para>
/// <para>
/// <b>The banned-string checks run over a FORMATTING-STRIPPED copy as well as the raw text</b> — see
/// <see cref="PromptTextAssertions.StripFormattingNoise"/>. Markdown gives several ways to write an
/// literal substring search would miss (<c>`write_spec`</c> is caught, but <c>write\_spec</c> and
/// <c>**write**_spec</c> are not), and a check that a reviewer believes is exhaustive but is not is
/// worse than no check at all.
/// </para>
/// </remarks>
public class AuthorScenarioPromptTests
{
    /// <summary>
    /// Every identifier this sprint retires — the SHARED list, so all four prompts are held to one
    /// set rather than to per-prompt copies that can drift.
    /// </summary>
    /// <remarks>
    /// The list itself moved to <see cref="PromptTextAssertions"/> when US-S5-03 added the second
    /// prompt. This member stays as the xUnit <c>MemberData</c> source for this class's own theory.
    /// </remarks>
    public static TheoryData<string> BannedIdentifiers() => PromptTextAssertions.BannedIdentifiers();

    /// <summary>
    /// THE ONE definition of the optional-argument matrix — 2³ = 8 combinations, with the values every
    /// case uses (AC-004).
    /// </summary>
    /// <remarks>
    /// <b>One generator, not two</b> (a code review's finding). This class previously carried two
    /// independent 8-way matrices with DIFFERENT fixture values — one driving the render-succeeds
    /// theory, one driving the banned-identifier sweep — so the two could disagree about what "every
    /// combination" meant, and a case covered by one was not necessarily covered by the other.
    /// </remarks>
    internal static IEnumerable<(string? FlowId, string? SpecPath, string? Constraints)> OptionalArgumentMatrix()
    {
        foreach (var flowId in new string?[] { null, "checkout-v2" })
        {
            foreach (var specPath in new string?[] { null, "e2e/provision-customer.e2e.yaml" })
            {
                foreach (var constraints in new string?[] { null, "max 8 steps" })
                {
                    yield return (flowId, specPath, constraints);
                }
            }
        }
    }

    /// <summary>The same matrix, as xUnit theory data.</summary>
    public static TheoryData<string?, string?, string?> OptionalArgumentCombinations()
    {
        var data = new TheoryData<string?, string?, string?>();
        foreach (var (flowId, specPath, constraints) in OptionalArgumentMatrix())
        {
            data.Add(flowId, specPath, constraints);
        }

        return data;
    }

    // ── Front matter (AC-001) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePrompt_DeclaresExactlyTheFourArguments_WithTheRightRequiredness()
    {
        var prompt = PromptRepository.Get(PromptCatalogue.AuthorScenario.Name);

        Assert.Equal("author_scenario", prompt.Name);
        Assert.Equal(
            ["flowDescription", "flowId", "specPath", "constraints"],
            prompt.Arguments.Select(argument => argument.Name));

        // Requiredness is the half a host branches on, so it is asserted per argument rather than as
        // a count of required ones.
        Assert.True(Single(prompt, "flowDescription").Required);
        Assert.False(Single(prompt, "flowId").Required);
        Assert.False(Single(prompt, "specPath").Required);
        Assert.False(Single(prompt, "constraints").Required);

        // A host shows these to a user when collecting arguments; an undescribed one is unusable.
        Assert.All(prompt.Arguments, argument => Assert.False(string.IsNullOrWhiteSpace(argument.Description)));
        Assert.False(string.IsNullOrWhiteSpace(prompt.Description));
    }

    [Fact]
    public void ARequiredArgument_IsRefusedWhenMissingOrBlank()
    {
        var prompt = PromptRepository.Get(PromptCatalogue.AuthorScenario.Name);

        Assert.Throws<PromptArgumentException>(() => prompt.Render(new Dictionary<string, string?>()));
        Assert.Throws<PromptArgumentException>(
            () => prompt.Render(new Dictionary<string, string?> { ["flowDescription"] = "   " }));
    }

    // ── AC-003 / Gherkin 1: no retired identifier survives, in ANY argument combination ─────────

    [Theory]
    [MemberData(nameof(BannedIdentifiers))]
    public void ARetiredIdentifier_NeverAppearsInTheRenderedText(string banned)
    {
        // Swept across all eight optional-argument combinations, because a section block is exactly
        // where a stale instruction would hide from a single-combination check.
        foreach (var rendered in AllRenderings())
        {
            PromptTextAssertions.AssertIdentifierAbsent(banned, rendered);
        }
    }

    [Fact]
    public void ARetiredIdentifier_IsNotSmuggledThroughAnArgumentValue()
    {
        // The prompt echoes its arguments. If a host passed one of these as a flowDescription the text
        // would legitimately contain it — which is why the sweep above uses fixed benign values, and
        // why this test states the boundary explicitly rather than leaving it as an unexamined gap.
        // What must hold is that the PROCEDURE never names one; an echoed argument is the caller's own
        // text coming back, not an instruction from this server.
        var rendered = Render("author a flow that replaces write_spec");

        Assert.Contains("write_spec", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Call `write_spec`", rendered, StringComparison.Ordinal);
    }

    // ── AC-002 / Gherkin 2: the real tools, in the right order ─────────────────────────────────

    [Theory]
    [InlineData("plan_coverage", "scaffold_suite")]
    [InlineData("list_step_types", "describe_step_type")]
    [InlineData("validate_suite", "run_suite")]
    [InlineData("run_suite", "explain_run")]
    public void TheProcedureNamesToolsInTheOrderItMustCallThem(string first, string second)
    {
        var rendered = Render();

        var firstAt = rendered.IndexOf(first, StringComparison.Ordinal);
        var secondAt = rendered.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstAt >= 0, $"'{first}' does not appear in the rendered prompt.");
        Assert.True(secondAt >= 0, $"'{second}' does not appear in the rendered prompt.");
        Assert.True(firstAt < secondAt, $"'{first}' must be mentioned before '{second}'.");
    }

    [Theory]
    [InlineData("get_schema")]
    [InlineData("plan_coverage")]
    [InlineData("scaffold_suite")]
    [InlineData("list_step_types")]
    [InlineData("describe_step_type")]
    [InlineData("validate_suite")]
    [InlineData("normalize_suite")]
    [InlineData("run_suite")]
    [InlineData("explain_run")]
    [InlineData("get_step_timeline")]
    [InlineData("get_run_artifacts")]
    [InlineData("classificationHints")]
    public void TheProcedureStillNamesEveryStepItShould(string identifier) =>
        // COMPLETENESS only — "the adaptation did not silently lose a step". It deliberately does NOT
        // claim these names are real; that is PromptSurfaceCrossCheck's job, and conflating the two is
        // what let `format: markdown` ship. See EveryIdentifierAndValue_MatchesTheAdvertisedSurface.
        Assert.Contains(identifier, Render(), StringComparison.Ordinal);

    [Fact]
    public void TheStepsThatCarryAnArgumentValue_SpellThatValueOut()
    {
        var rendered = Render();

        // The two BLOCKERs, pinned as the positive facts they are. `get_schema` with no format is
        // fine; `get_schema` with the WRONG one fails at VFX-E-1006, and `normalize_suite` without
        // `normalize: true` returns a null normalizedYaml — leaving the D3 hand-off step with nothing
        // to write. Both are argument-VALUE defects, invisible to any check on identifier presence.
        Assert.Contains("`format: summary`", rendered, StringComparison.Ordinal);
        Assert.Contains("`normalize: true`", rendered, StringComparison.Ordinal);

        // Backticked as a WHOLE PAIR, deliberately: PromptSurfaceCrossCheck's value rules only see a
        // `key: value` token when the whole pair sits inside one backtick span. Written the old way —
        // "(level: `full`)", with only the value backticked — the `level` rule never fired at all,
        // which read as coverage and was not (a spec review's finding).
        Assert.Contains("`level: full`", rendered, StringComparison.Ordinal);

        // And the refusal branch exists, so a null normalizedYaml is not a dead end.
        PromptTextAssertions.AssertPhrasePresent("normalizationRefused", rendered);
    }

    [Fact]
    public void EveryDeclaredArgument_IsUsedByTheBody_AndEveryPlaceholderIsDeclared()
    {
        // M1: the declaration↔body leg the "drift is impossible" claim did NOT cover. Front matter
        // declaring `specPath` while the body writes `{{specpath}}` renders silently empty — a host
        // gets a procedure with a hole in it and nothing anywhere fails.
        var prompt = PromptRepository.Get(PromptCatalogue.AuthorScenario.Name);

        Assert.Equal(
            prompt.Arguments.Select(argument => argument.Name).OrderBy(name => name, StringComparer.Ordinal),
            PromptDocumentParser.ReferencedArgumentNames(prompt.Body).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void TheValidationStep_NamesTheFullLevelAndTheFiveIterationCap()
    {
        var rendered = Render();

        // Sprint 2 landed `level`, so the AC's conditional resolves to the tool-exists branch.
        Assert.Contains("`level: full`", rendered, StringComparison.Ordinal);
        Assert.Contains("Maximum 5 iterations", rendered, StringComparison.Ordinal);
        Assert.Contains("verbatim", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteStep_TellsTheHostToWriteWithItsOwnToolsAndToNormaliseFirst()
    {
        var rendered = Render();

        // D3: this server never writes a suite file. The instruction has to be unambiguous about WHO
        // writes, or a host will look for a tool that does not exist.
        PromptTextAssertions.AssertPhrasePresent("your own file-editing tools", rendered);
        Assert.Contains("normalize_suite", rendered, StringComparison.Ordinal);
        PromptTextAssertions.AssertPhrasePresent("never writes", rendered);
    }

    // ── Gherkin 3: the taxonomy rule survives adaptation word for word ─────────────────────────

    [Fact]
    public void TheTaxonomyRule_IsPreservedWordForWord()
    {
        var rendered = Render();

        // Spec §7.1 step 10's own words, CASE-SENSITIVELY and word for word — the AC asks for exactly
        // that. This is the single most important sentence in the prompt: it is what stops a host
        // "fixing" a genuine product defect by weakening the test that found it.
        PromptTextAssertions.AssertPhrasePresent("do not change the assertion to make it pass", rendered, ignoreCase: false);

        // All four outcomes must be branched on, or a host has no rule for the one it got.
        foreach (var outcome in new[] { "Pass", "Fail", "EnvironmentError", "Inconclusive" })
        {
            Assert.Contains(outcome, rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFinalReportingStep_IsPreserved()
    {
        var rendered = Render();

        foreach (var fragment in new[]
        {
            "path written", "what the scenario proves", "what it does not cover", "open questions",
        })
        {
            PromptTextAssertions.AssertPhrasePresent(fragment, rendered);
        }
    }

    // ── AC-004 / Gherkin 4: every optional-argument combination renders ────────────────────────

    [Theory]
    [MemberData(nameof(OptionalArgumentCombinations))]
    public void EveryOptionalArgumentCombination_RendersNonEmptyText(
        string? flowId, string? specPath, string? constraints)
    {
        var rendered = Render("provision a customer", flowId, specPath, constraints);

        Assert.False(string.IsNullOrWhiteSpace(rendered));

        // Substantive, not merely non-empty: a template that silently swallowed its body would pass a
        // whitespace check. The floor is well under the real length and well over any degenerate one.
        Assert.True(rendered.Length > 1500, $"Rendered prompt is implausibly short ({rendered.Length} chars).");

        // No unsubstituted placeholder or unclosed section ever reaches a host.
        Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("}}", rendered, StringComparison.Ordinal);

        // The required argument is always echoed, whatever the optional ones do.
        Assert.Contains("provision a customer", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuppliedOptionalArgument_IsEchoed_AndAnOmittedOneLeavesNoDanglingLabel()
    {
        var withAll = Render(
            flowDescription: "provision a customer",
            flowId: "checkout-v2",
            specPath: "e2e/provision-customer.e2e.yaml",
            constraints: "max 8 steps");

        Assert.Contains("checkout-v2", withAll, StringComparison.Ordinal);
        Assert.Contains("e2e/provision-customer.e2e.yaml", withAll, StringComparison.Ordinal);
        Assert.Contains("max 8 steps", withAll, StringComparison.Ordinal);

        var withNone = Render("provision a customer");

        Assert.DoesNotContain("checkout-v2", withNone, StringComparison.Ordinal);
        Assert.DoesNotContain("max 8 steps", withNone, StringComparison.Ordinal);

        // With no specPath the prompt must still tell the host WHERE to write, or the procedure has a
        // hole exactly where D3 hands the job over.
        Assert.Contains("specsDir", withNone, StringComparison.Ordinal);

        // AND the complementary half (a code review's finding): asserting only that the fallback
        // APPEARS when specPath is absent leaves the negative section untested in the direction that
        // actually matters — a `{{^specPath}}` that never closed would render the "choose a path"
        // paragraph alongside an explicit target, telling the host two different things at once.
        Assert.DoesNotContain("specsDir", withAll, StringComparison.Ordinal);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static PromptArgumentDefinition Single(PromptDefinition prompt, string name) =>
        Assert.Single(prompt.Arguments, argument => argument.Name == name);

    private static string Render(
        string flowDescription = "provision a customer",
        string? flowId = null,
        string? specPath = null,
        string? constraints = null)
    {
        var arguments = new Dictionary<string, string?> { ["flowDescription"] = flowDescription };
        if (flowId is not null)
        {
            arguments["flowId"] = flowId;
        }

        if (specPath is not null)
        {
            arguments["specPath"] = specPath;
        }

        if (constraints is not null)
        {
            arguments["constraints"] = constraints;
        }

        return PromptRepository.Get(PromptCatalogue.AuthorScenario.Name).Render(arguments);
    }

    /// <summary>Every one of the eight optional-argument combinations, from the ONE matrix.</summary>
    internal static IEnumerable<string> AllRenderings() =>
        OptionalArgumentMatrix().Select(
            combination => Render(
                "provision a customer", combination.FlowId, combination.SpecPath, combination.Constraints));

    [Fact]
    public void TheFormattingStripper_CollapsesTheEvasionsItClaimsTo()
    {
        // The shared guard is only as good as this helper, so the helper is tested directly. It lives
        // on PromptTextAssertions now (US-S5-03 extracted it when the second prompt arrived); this
        // test stays here because it is where the evasions it models were first found.
        Assert.Contains(
            "write_spec", PromptTextAssertions.StripFormattingNoise(@"write\_spec"), StringComparison.Ordinal);
        Assert.Contains(
            "write_spec", PromptTextAssertions.StripFormattingNoise("**write**_spec"), StringComparison.Ordinal);
        Assert.Contains(
            "write_spec", PromptTextAssertions.StripFormattingNoise("`write_spec`"), StringComparison.Ordinal);
        Assert.Contains(
            "write_spec", PromptTextAssertions.StripFormattingNoise("write​_spec"), StringComparison.Ordinal);

        // And it does not manufacture a match out of unrelated prose.
        Assert.DoesNotContain(
            "write_spec",
            PromptTextAssertions.StripFormattingNoise("write the spec yourself"),
            StringComparison.Ordinal);
    }
}
