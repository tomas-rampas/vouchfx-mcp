using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// US-S5-03: the <c>heal_run</c> prompt walks a host through healing an <c>EnvironmentError</c> or
/// <c>Inconclusive</c> run with THIS repo's tools — and forbids acting on a <c>Fail</c>.
/// </summary>
/// <remarks>
/// The banned-identifier and wrap-tolerant text machinery is <see cref="PromptTextAssertions"/>',
/// shared rather than re-derived — that is what the US-S5-02 infrastructure was built for. What is
/// specific here is the scope vocabulary, the <c>reason.kind</c> vocabulary and the Fail prohibition.
/// <para>
/// <b>The advertised-surface cross-check is NOT run from this class</b>, and an earlier version of
/// this remark implied otherwise. It needs a live harness, so it lives in
/// <c>RealHealRunPromptMcpTests</c> — which is where the wire round trip is too. That separation is
/// worth stating because the first version of this story omitted the cross-check for this prompt
/// entirely, and the prompt would have failed it.
/// </para>
/// </remarks>
public class HealRunPromptTests
{
    private const string PromptName = "heal_run";

    /// <summary>Renders with the given arguments; <c>allowedScopes</c> omitted means "take the default".</summary>
    internal static string Render(string runId = "run-42", string? allowedScopes = null)
    {
        var arguments = new Dictionary<string, string?> { ["runId"] = runId };
        if (allowedScopes is not null)
        {
            arguments["allowedScopes"] = allowedScopes;
        }

        return PromptRepository.Get(PromptName).Render(arguments);
    }

    /// <summary>
    /// Every argument shape this prompt renders under — the default, two narrowings, and the EXPLICITLY
    /// EMPTY case.
    /// </summary>
    /// <remarks>
    /// <b>The empty case is here so the <c>{{^allowedScopes}}</c> branch is enrolled in every sweep
    /// that consumes this</b> (a code review's nit): the banned-identifier sweep, the
    /// substitution-completeness check and the advertised-surface cross-check all iterate these
    /// renderings, and until it was added the entire "apply nothing" paragraph — a branch that exists
    /// precisely because it carries a different instruction from the others — was covered by its own
    /// dedicated tests and by nothing else. A branch a sweep never sees is a branch a retired
    /// identifier could hide in.
    /// </remarks>
    internal static IEnumerable<string> AllRenderings()
    {
        yield return Render();
        yield return Render(allowedScopes: "environment");
        yield return Render(allowedScopes: "environment, timeouts");
        yield return Render(allowedScopes: string.Empty);
    }

    // ── Front matter ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePrompt_DeclaresRunIdRequiredAndAllowedScopesOptional()
    {
        var prompt = PromptRepository.Get(PromptName);

        Assert.Equal("heal_run", prompt.Name);
        Assert.Equal(["runId", "allowedScopes"], prompt.Arguments.Select(argument => argument.Name));

        Assert.True(Assert.Single(prompt.Arguments, a => a.Name == "runId").Required);
        Assert.False(Assert.Single(prompt.Arguments, a => a.Name == "allowedScopes").Required);

        Assert.All(prompt.Arguments, a => Assert.False(string.IsNullOrWhiteSpace(a.Description)));
    }

    [Fact]
    public void AMissingRunId_IsRefused()
    {
        var prompt = PromptRepository.Get(PromptName);

        Assert.Throws<PromptArgumentException>(() => prompt.Render(new Dictionary<string, string?>()));
        Assert.Throws<PromptArgumentException>(
            () => prompt.Render(new Dictionary<string, string?> { ["runId"] = "  " }));
    }

    // ── THE cross-reference the story mandates ─────────────────────────────────────────────────

    /// <summary>
    /// The prompt's default <c>allowedScopes</c> is IDENTICAL to the scope vocabulary
    /// <see cref="SpecEditProposal.Scope"/> can ever carry.
    /// </summary>
    /// <remarks>
    /// <b>The story calls the two diverging "a real defect", and it is right in both directions.</b> A
    /// prompt listing a scope the Healer never emits tells a host to look for proposals that cannot
    /// exist; a prompt omitting one the Healer does emit silently forbids applying a whole class of
    /// legitimate fix, with no error anywhere — the host just quietly does less than it was asked to.
    /// Set equality both ways is the only assertion that catches both.
    /// <para>
    /// Read from <see cref="SpecEditScopes.All"/>, the production constant US-S4-03 created precisely
    /// so this vocabulary has one owner. The prompt's front matter states the list too — deliberately,
    /// so <c>prompts/list</c> is self-describing — and this test is what makes that duplication safe.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDefaultAllowedScopes_AreExactlyTheScopesTheHealerCanEmit()
    {
        var declared = Assert.Single(
            PromptRepository.Get(PromptName).Arguments, a => a.Name == "allowedScopes");

        Assert.NotNull(declared.Default);

        var defaultScopes = declared.Default!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            SpecEditScopes.All.OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
            defaultScopes);

        // And the four the story names, re-typed from the AC rather than from either source — so a
        // coordinated change to both the constant and the prompt still has to face the requirement.
        Assert.Equal(["capture", "environment", "match", "timeouts"], defaultScopes);
    }

    /// <summary>
    /// The marker that opens the line stating which scopes are applicable in this session.
    /// </summary>
    /// <remarks>
    /// <b>The scope assertions target this LINE, never bare words, and that is not fussiness.</b>
    /// Every one of the four scope names is also ordinary English that the procedure legitimately
    /// uses elsewhere: the Fail rule says "do not widen a <c>match</c>", step 2 lists the
    /// <c>reason.kind</c> values <c>timeout</c> and <c>capture_unmet</c>, and step 4 talks about
    /// applying a change to the <c>environment</c>. A bare <c>DoesNotContain("match")</c> would
    /// therefore fail on correct text, and — worse — a bare <c>Contains</c> would PASS on a prompt
    /// whose scope list had been dropped entirely, because the word survives in the prose. Extracting
    /// the line is what makes the narrowing assertion mean what it says.
    /// </remarks>
    private const string ScopeLineMarker = "**Scopes you may apply in this session:**";

    /// <summary>The scope names the rendered prompt actually presents as applicable.</summary>
    private static string[] RenderedScopes(string rendered)
    {
        var at = rendered.IndexOf(ScopeLineMarker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The rendered prompt has no '{ScopeLineMarker}' line.");

        var lineEnd = rendered.IndexOf('\n', at);
        var line = lineEnd < 0 ? rendered[at..] : rendered[at..lineEnd];

        return [.. line[ScopeLineMarker.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(scope => scope.Trim('`', '.', '*'))];
    }

    [Fact]
    public void TheDefaultScopeList_IsWhatRendersWhenTheArgumentIsOmitted() =>
        // Gherkin scenario 3: "the rendered text's default scope list is exactly environment,
        // timeouts, match, capture". Exactly — so set equality, not containment.
        Assert.Equal(
            SpecEditScopes.All.OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
            RenderedScopes(Render()).OrderBy(scope => scope, StringComparer.Ordinal).ToArray());

    // ── Gherkin 4: a narrowing actually narrows ────────────────────────────────────────────────

    [Fact]
    public void ACustomAllowedScopes_NarrowsWhatTheHostIsToldItMayApply()
    {
        var narrowed = Render(allowedScopes: "environment");

        // The assertion that makes the argument mean something: a prompt that rendered the same text
        // regardless would pass every other test in this class.
        Assert.Equal(["environment"], RenderedScopes(narrowed));

        // And it still instructs applying ONLY in-scope proposals — the narrowing has to be an
        // INSTRUCTION, not merely a shorter list sitting inertly in the text.
        PromptTextAssertions.AssertPhrasePresent("ONLY when its", narrowed);
        PromptTextAssertions.AssertPhrasePresent("is reported, never applied", narrowed);
    }

    [Fact]
    public void ATwoScopeNarrowing_RendersBothAndExcludesTheRest() =>
        Assert.Equal(["environment", "timeouts"], RenderedScopes(Render(allowedScopes: "environment, timeouts")));

    /// <summary>The marker that opens the filter instruction in the proposals step.</summary>
    private const string FilterLineMarker = "**Apply a proposal ONLY when its `scope` is one of:";

    [Fact]
    public void TheScopeListAppearsAtBothDecisionPoints()
    {
        // Twice, deliberately: once as the session's declared permission near the top, and again at
        // the filter step where a proposal is actually accepted or refused. A host reading the
        // procedure linearly must not have to scroll back to learn what it may apply.
        //
        // Asserted by locating BOTH marker lines rather than by counting occurrences of a scope name —
        // "environment" also appears in step 5's "burns a container environment", so a count would be
        // measuring the prose rather than the instruction.
        var rendered = Render(allowedScopes: "environment");

        Assert.Contains(ScopeLineMarker, rendered, StringComparison.Ordinal);

        var filterAt = rendered.IndexOf(FilterLineMarker, StringComparison.Ordinal);
        Assert.True(filterAt >= 0, $"The rendered prompt has no '{FilterLineMarker}' instruction.");

        var filterEnd = rendered.IndexOf('\n', filterAt);
        var filterLine = filterEnd < 0 ? rendered[filterAt..] : rendered[filterAt..filterEnd];

        Assert.Contains("environment", filterLine, StringComparison.Ordinal);
        Assert.DoesNotContain("timeouts", filterLine, StringComparison.Ordinal);
    }

    // ── Gherkin 1: the Fail prohibition ────────────────────────────────────────────────────────

    [Fact]
    public void TheFailProhibition_IsStatedUnambiguouslyAndNearTheTop()
    {
        var rendered = Render();

        // Spec §7.2's substance, verbatim: "You must NOT act on a Fail outcome except to explain it."
        PromptTextAssertions.AssertPhrasePresent("must NOT act on a", rendered);
        PromptTextAssertions.AssertPhrasePresent("except to explain it", rendered);

        // The same word-for-word rule author_scenario carries, so the two prompts cannot drift on the
        // single most important sentence either of them contains.
        PromptTextAssertions.AssertPhrasePresent(
            "do not weaken an assertion to force a pass", rendered, ignoreCase: false);

        // NEAR THE TOP is part of the AC, not decoration: a host that stops reading after the first
        // screen must still have seen it. Asserted as a position, within the first quarter of the text.
        var at = rendered.IndexOf("must NOT act on a", StringComparison.Ordinal);
        Assert.True(at >= 0 && at < rendered.Length / 4, $"The Fail prohibition appears too late (offset {at} of {rendered.Length}).");
    }

    // ── Gherkin 2: never write_spec, and the host applies the edit ─────────────────────────────

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
    public void TheHostAppliesAnyEditWithItsOwnFileTools()
    {
        var rendered = Render();

        PromptTextAssertions.AssertPhrasePresent("your own file-editing tools", rendered);
        PromptTextAssertions.AssertPhrasePresent("never writes", rendered);
    }

    // ── The procedure names this repo's real steps, in order ───────────────────────────────────

    [Theory]
    // runId is resolved to an events path FIRST — see TheProcedureResolvesRunIdBeforeDiagnosing.
    [InlineData("get_run_status", "diagnose_run")]
    [InlineData("diagnose_run", "specEditProposals")]
    [InlineData("specEditProposals", "run_suite")]
    public void TheProcedureNamesStepsInTheOrderItMustPerformThem(string first, string second)
    {
        var rendered = Render();

        var firstAt = rendered.IndexOf(first, StringComparison.Ordinal);
        var secondAt = rendered.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstAt >= 0, $"'{first}' does not appear in the rendered prompt.");
        Assert.True(secondAt >= 0, $"'{second}' does not appear in the rendered prompt.");
        Assert.True(firstAt < secondAt, $"'{first}' must be mentioned before '{second}'.");
    }

    /// <summary>
    /// The <c>reason.kind</c> values the prompt lists are exactly the ones a host can actually
    /// observe — <see cref="VerdictReasonKinds.All"/> minus the reserved <c>compile</c>.
    /// </summary>
    /// <remarks>
    /// <b>The omission of <c>compile</c> is CORRECT, and this test exists to keep it correct.</b>
    /// That kind is reserved for a future <c>compile_spec</c> relay (upstream ask U3, structurally out
    /// of scope for this repository); no rule in <c>VerdictReasonClassifier</c> assigns it, and two
    /// existing tests hold that from different directions. Listing it here would tell a host to branch
    /// on a value this build can never emit.
    /// <para>
    /// Pinned as set equality rather than as a spot check so the prompt cannot silently fall behind:
    /// when the vocabulary gains a kind, this fails until the procedure names it (or until someone
    /// deliberately adds it to the reserved set below and says why).
    /// </para>
    /// </remarks>
    [Fact]
    public void TheReasonKindsTheProcedureLists_AreExactlyTheOnesAHostCanObserve()
    {
        var rendered = Render();

        var observable = VerdictReasonKinds.All
            .Where(kind => !string.Equals(kind, VerdictReasonKinds.Compile, StringComparison.Ordinal))
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();

        foreach (var kind in observable)
        {
            Assert.Contains($"`{kind}`", rendered, StringComparison.Ordinal);
        }

        Assert.DoesNotContain($"`{VerdictReasonKinds.Compile}`", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("classificationHints")]
    [InlineData("reason.kind")]
    [InlineData("specEditProposals")]
    [InlineData("get_run_artifacts")]
    [InlineData("get_run_events")]
    [InlineData("EnvironmentError")]
    [InlineData("Inconclusive")]
    public void TheProcedureStillNamesEveryStepItShould(string identifier) =>
        Assert.Contains(identifier, Render(), StringComparison.Ordinal);

    /// <summary>
    /// The prompt resolves <c>runId</c> to an events path before diagnosing — because neither
    /// <c>explain_run</c> nor <c>diagnose_run</c> accepts a <c>runId</c>.
    /// </summary>
    /// <remarks>
    /// <b>The story's AC says "call <c>explain_run</c> (or <c>diagnose_run</c> directly) for
    /// <c>runId</c>", and that cannot be followed literally</b> — MEASURED against the tool
    /// signatures: both take <c>eventsPath</c> only, while <c>get_run_status</c>,
    /// <c>get_run_events</c> and <c>get_run_artifacts</c> are the runId-native ones. A prompt that
    /// instructed <c>diagnose_run(runId: …)</c> would fail on its very first call, which is exactly
    /// the defect class that shipped twice in US-S5-02. This test pins the adaptation.
    /// </remarks>
    [Fact]
    public void TheProcedureResolvesRunIdBeforeDiagnosing()
    {
        var rendered = Render();

        Assert.Contains("get_run_status", rendered, StringComparison.Ordinal);
        Assert.Contains("eventsFilePath", rendered, StringComparison.Ordinal);
        Assert.Contains("eventsPath", rendered, StringComparison.Ordinal);

        // And it must NOT tell the host to pass a runId to either path-native tool.
        Assert.DoesNotContain("diagnose_run` with `runId", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("explain_run` with `runId", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReRunStep_IsBoundedToOnceAndSpellsOutTheArgumentItDependsOn()
    {
        var rendered = Render();

        // "re-run ONCE" is the AC's own bound — an unbounded heal loop is how a host burns a Docker
        // environment chasing a defect it was told not to fix. Asserted as the actual SENTENCE: a bare
        // AssertPhrasePresent("once") was near-vacuous, since "once" occurs in ordinary prose (a code
        // review's finding).
        PromptTextAssertions.AssertPhrasePresent("One re-run, not a loop", rendered);

        // B2's lesson: the procedure compares outcomes, so the re-run must be synchronous. `wait`
        // defaults to null (accepted) and `wait: false` is refused with VFX-E-1504 pre-U4 — spelling
        // the value the procedure DEPENDS on is what stops a host filling the gap with the wrong one.
        Assert.Contains("`wait: true`", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportStep_NamesAllFourRequiredParts()
    {
        var rendered = Render();

        foreach (var part in new[] { "root cause", "evidence", "confidence" })
        {
            PromptTextAssertions.AssertPhrasePresent(part, rendered);
        }

        PromptTextAssertions.AssertPhrasePresent("change made or recommended", rendered);
    }

    // ── Rendering hygiene ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryRendering_IsSubstantialAndFullySubstituted()
    {
        foreach (var rendered in AllRenderings())
        {
            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.True(rendered.Length > 1200, $"Rendered prompt is implausibly short ({rendered.Length} chars).");
            Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", rendered, StringComparison.Ordinal);
            Assert.Contains("run-42", rendered, StringComparison.Ordinal);
        }
    }
}
