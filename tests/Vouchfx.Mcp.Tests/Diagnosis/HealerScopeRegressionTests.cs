using System.Text.RegularExpressions;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Diagnosis;

/// <summary>
/// US-S4-05: the named, CI-enforced regression guard for the taxonomy discipline Sprint 4's rule
/// table and Healer superset could regress — swept across every <c>reason.kind</c> fixture the
/// sprint ships, rather than against a convenience set written to pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class drives the REAL corpora</b> —
/// <see cref="VerdictReasonClassifierTests.Corpus"/> (US-S4-01's one fixture per kind) and
/// <see cref="SpecEditProposalBuilderTests.AllFixtures"/> (US-S4-03's proposal fixtures) — which is
/// the point of the story. A guard over its own fresh fixtures proves only that the fixtures it
/// chose behave; a guard over the corpus the sprint actually ships fails the moment a NEW fixture
/// added for some other reason violates an invariant. The two members were widened from
/// <see langword="private"/> to <see langword="internal"/> for exactly this, and for nothing else.
/// </para>
/// <para>
/// <b>It aggregates rather than duplicates.</b> Three narrower guards already exist and stay where
/// they are — <c>SpecEditProposalBuilderTests.EveryScopeTheBuilderCanEmit_IsExactlyTheFourPermittedValues</c>
/// (source enumeration of the scope vocabulary plus the ladder's propagation-only counts),
/// <c>…NoSuggestedEdit_EverCarriesAnAssertionShapedKey</c> (fixture sweep), and
/// <c>…AnAdversariallyNamedStep_NeverRendersAnAssertionShapedKey</c> (hostile identifiers in key
/// position). What this class adds is the CORPUS-WIDE dimension the acceptance criteria name, plus
/// the source-level assertion that the builder's own fragment templates contain no
/// assertion-shaped key to begin with.
/// </para>
/// </remarks>
public class HealerScopeRegressionTests
{
    /// <summary>
    /// Every events fixture Sprint 4 ships, from both stories' own corpora — the sweep surface for
    /// this whole class.
    /// </summary>
    public static TheoryData<string, string> SprintFixtures
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var (name, events, _) in VerdictReasonClassifierTests.Corpus)
            {
                data.Add($"classifier:{name}", events);
            }

            for (var i = 0; i < SpecEditProposalBuilderTests.AllFixtures.Length; i++)
            {
                data.Add($"proposals:{i}", SpecEditProposalBuilderTests.AllFixtures[i]);
            }

            // The CO-OCCURRENCE row, and it is load-bearing (a review found the headline sweep could
            // not witness the case it exists to guard). Across the two corpora above, Fail steps and
            // spec-edit proposals never appear in the SAME run: the classifier fixtures are
            // single-shape by design, and the proposal fixtures carry no Fail step with evidence. So
            // "no spec-edit proposal names a Fail step" was passing over rows where one population or
            // the other was empty — true, but vacuous. This row is US-S4-04's maximal fan-out: five
            // Fail steps and five Inconclusive ones interleaved, plus classified environment errors,
            // producing BOTH proposal kinds at once.
            data.Add("mixed:honest-fan-out", DiagnoseRunOrchestratorTests.BuildHonestFanOutEvents(
                observationChars: 200, stepIdChars: 12));

            return data;
        }
    }

    /// <summary>
    /// The vacuity floor: the corpus really does exercise both proposal kinds and all four scopes,
    /// so the sweeps above cannot all pass over empty sets.
    /// </summary>
    /// <remarks>
    /// Every sweep in this class is of the form "nothing in the corpus violates X". Such a test is
    /// green when nothing exists, which is why this one asserts the populations are non-empty in the
    /// first place — the counterpart every negative guard needs and few have.
    /// </remarks>
    [Fact]
    public async Task TheCorpus_ActuallyExercisesBothProposalKindsAndAllFourScopes()
    {
        var failProposals = 0;
        var specEditProposals = 0;
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var failSteps = 0;

        foreach (var row in SprintFixtures)
        {
            var diagnosis = await DiagnoseAsync((string)row[1]);

            failSteps += diagnosis.NotableSteps.Count(s => s.Verdict == nameof(RunVerdict.Fail));
            failProposals += FailProposalBuilder.BuildProposals(diagnosis).Count;

            foreach (var proposal in SpecEditProposalBuilder.BuildProposals(diagnosis))
            {
                specEditProposals++;
                scopes.Add(proposal.Scope);
            }
        }

        Assert.True(failSteps > 0, "The corpus contains no Fail step at all; the Fail sweeps would be vacuous.");
        Assert.True(failProposals > 0, "The corpus produces no FailProposal at all.");
        Assert.True(specEditProposals > 0, "The corpus produces no SpecEditProposal at all; every scope sweep would be vacuous.");
        Assert.Equal(SpecEditScopes.All.OrderBy(s => s, StringComparer.Ordinal), scopes.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// The co-occurrence the headline sweep needs: ONE run carrying Fail steps and a full spec-edit
    /// fan-out at the same time, with no proposal naming a Fail step.
    /// </summary>
    [Fact]
    public async Task OnARunCarryingBothPopulations_NoSpecEditProposalNamesAFailStep()
    {
        var diagnosis = await DiagnoseAsync(
            DiagnoseRunOrchestratorTests.BuildHonestFanOutEvents(observationChars: 200, stepIdChars: 12));

        var failStepIds = diagnosis.NotableSteps
            .Where(s => s.Verdict == nameof(RunVerdict.Fail))
            .Select(s => s.StepId)
            .ToHashSet(StringComparer.Ordinal);
        var proposals = SpecEditProposalBuilder.BuildProposals(diagnosis);

        // Both populations are genuinely present — this is the row the disjoint corpora could not
        // provide.
        Assert.Equal(5, failStepIds.Count);
        Assert.True(proposals.Count >= 10, $"Expected a full spec-edit fan-out, got {proposals.Count}.");

        Assert.DoesNotContain(proposals, p => p.StepId is not null && failStepIds.Contains(p.StepId));
    }

    // ── AC 1a: a Fail step never yields a spec-edit proposal, anywhere in the corpus ────────────

    /// <summary>
    /// Across every fixture the sprint ships: no step whose own verdict is <c>Fail</c> is ever named
    /// by a <see cref="SpecEditProposal"/>. An assertion is never weakened to make a run green.
    /// </summary>
    [Theory]
    [MemberData(nameof(SprintFixtures))]
    public async Task NoFailVerdictStep_EverProducesASpecEditProposal(string name, string events)
    {
        var diagnosis = await DiagnoseAsync(events);

        var failStepIds = diagnosis.NotableSteps
            .Where(s => s.Verdict == nameof(RunVerdict.Fail))
            .Select(s => s.StepId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var proposal in SpecEditProposalBuilder.BuildProposals(diagnosis))
        {
            Assert.False(
                proposal.StepId is not null && failStepIds.Contains(proposal.StepId),
                $"Fixture {name}: spec-edit proposal (scope '{proposal.Scope}') names Fail step "
                + $"'{proposal.StepId}'. Only a FailProposal may ever concern a Fail step.");
        }
    }

    /// <summary>
    /// The complementary half: a Fail step with usable evidence still gets its EXISTING review
    /// proposal. The partition is "one list or the other", never "neither".
    /// </summary>
    [Theory]
    [MemberData(nameof(SprintFixtures))]
    public async Task EveryFailStepWithEvidence_StillGetsItsExistingReviewProposal(string name, string events)
    {
        var diagnosis = await DiagnoseAsync(events);

        var failStepsWithEvidence = diagnosis.NotableSteps
            .Where(s => s.Verdict == nameof(RunVerdict.Fail) && !string.IsNullOrWhiteSpace(s.Observation))
            .Select(s => s.StepId)
            .ToList();

        var proposedFor = FailProposalBuilder.BuildProposals(diagnosis)
            .Select(p => p.StepId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stepId in failStepsWithEvidence)
        {
            Assert.True(
                proposedFor.Contains(stepId),
                $"Fixture {name}: Fail step '{stepId}' carries observation evidence but got no review proposal.");
        }
    }

    // ── AC 1b: the scope set is closed, corpus-wide ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SprintFixtures))]
    public async Task NoProposalScope_IsEverOutsideTheClosedSet(string name, string events)
    {
        var diagnosis = await DiagnoseAsync(events);

        foreach (var proposal in SpecEditProposalBuilder.BuildProposals(diagnosis))
        {
            Assert.True(
                SpecEditScopes.All.Contains(proposal.Scope),
                $"Fixture {name}: scope '{proposal.Scope}' is outside {{environment, timeouts, match, capture}}.");
        }
    }

    // ── AC 1c: no assertion-shaped key, at the SOURCE as well as in rendered output ─────────────

    /// <summary>
    /// The derive-from-source half (the pattern <c>SecretHygieneSourceGuardTests</c> uses): the
    /// builder's own YAML fragment templates contain no assertion-shaped key AT ALL, so no input can
    /// render one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reads the raw source with COMMENTS stripped and literals KEPT</b> — the inverse of
    /// <see cref="SourceGuardScan.ExecutableSourceOf"/>, which blanks literals. The fragments ARE
    /// string literals, so they are the one thing this guard must be able to see.
    /// </para>
    /// <para>
    /// Comments have to go, and that is a measured lesson rather than a precaution: the first version
    /// scanned the raw file and failed immediately on the builder's own remarks, which say in prose
    /// that no template carries <c>expect</c>, <c>assert</c> or <c>match.value</c>. Documentation
    /// describing the invariant must not be mistaken for a violation of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBuildersFragmentTemplates_ContainNoAssertionShapedKey()
    {
        var builderPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Diagnosis", "SpecEditProposalBuilder.cs");
        var source = StripCommentsKeepingLiterals(File.ReadAllText(builderPath));

        // A YAML key in key position: start of line (after indentation), then the word, then a colon.
        foreach (var forbidden in new[] { "expect", "expected", "assert", "value" })
        {
            var match = Regex.Match(source, $@"(?m)^\s*{forbidden}\s*:");
            Assert.False(
                match.Success,
                $"SpecEditProposalBuilder's source carries an assertion-shaped key in key position: "
                + $"'{match.Value.Trim()}'. The four permitted scopes never set one — a spec edit may "
                + "raise a timeout, change an image, fix a match key or a capture path, never weaken "
                + "what a step asserts.");
        }

        // ...and never the dotted form the acceptance criterion names explicitly.
        Assert.DoesNotContain("match.value", source, StringComparison.OrdinalIgnoreCase);

        // The stripping kept every template WHOLE — otherwise the sweep above would be scanning a
        // file with its fragments partly removed and would pass for the wrong reason. Three marker
        // substrings were not enough (a review's point): a `//` appearing inside a template would
        // truncate it at that point while leaving the markers intact. So each raw-string literal in
        // the raw file is required to survive verbatim into the stripped text.
        var rawSource = File.ReadAllText(builderPath);
        var rawLiterals = Regex.Matches(rawSource, "\"\"\".*?\"\"\"", RegexOptions.Singleline)
            .Select(m => m.Value)
            .ToList();

        Assert.True(rawLiterals.Count >= 6, $"Expected the builder's six fragment templates, found {rawLiterals.Count} raw literals.");
        foreach (var literal in rawLiterals)
        {
            Assert.Contains(literal, source, StringComparison.Ordinal);
        }
    }

    /// <summary>The rendered half, corpus-wide: no fragment any fixture produces carries such a key either.</summary>
    [Theory]
    [MemberData(nameof(SprintFixtures))]
    public async Task NoRenderedFragment_EverCarriesAnAssertionShapedKey(string name, string events)
    {
        var diagnosis = await DiagnoseAsync(events);

        foreach (var proposal in SpecEditProposalBuilder.BuildProposals(diagnosis))
        {
            foreach (var forbidden in new[] { "expect", "expected", "assert", "value" })
            {
                Assert.False(
                    Regex.IsMatch(proposal.SuggestedEdit, $@"(?m)^\s*{forbidden}\s*:"),
                    $"Fixture {name}: a '{proposal.Scope}' fragment sets '{forbidden}:'.");
            }

            // Nor is any fragment a diff against a file this server was never given.
            Assert.DoesNotContain("--- a/", proposal.SuggestedEdit, StringComparison.Ordinal);
        }
    }

    // ── AC 1d: guidance and spec edits are complementary, not duplicative ───────────────────────

    /// <summary>
    /// <c>BuildEnvironmentGuidance</c> is unchanged in SHAPE — infrastructure prose, never YAML —
    /// and coexists with a spec-edit proposal for the SAME error record: one explains the
    /// infrastructure, the other proposes the suite edit, and neither replaces the other.
    /// </summary>
    [Fact]
    public async Task GuidanceAndSpecEditProposals_CoexistOnOneRecordWithoutReplacingEachOther()
    {
        // The resource is named 'orders-db', not 'events': a review found that 'events' collides with
        // this codebase's own "events file" prose, so the resource-named-in-guidance assertion below
        // could have been satisfied by a sentence that never mentioned the resource at all.
        const string events = """
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"orders-db","detail":"health gate timed out after 30000ms"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var diagnosis = await DiagnoseAsync(events);
        var guidance = FailProposalBuilder.BuildEnvironmentGuidance(diagnosis);
        var proposal = Assert.Single(SpecEditProposalBuilder.BuildProposals(diagnosis));

        // BOTH are produced for the one record — the superset, not a substitution.
        Assert.NotEmpty(guidance);
        Assert.Equal(SpecEditScopes.Environment, proposal.Scope);

        // Guidance keeps its shape: prose about the infrastructure, naming the resource, with no
        // YAML and no diff anywhere in it.
        Assert.Contains(guidance, line => line.Contains("orders-db", StringComparison.Ordinal));
        Assert.Contains(guidance, line => line.Contains("not a test defect", StringComparison.OrdinalIgnoreCase));
        Assert.All(guidance, line => Assert.DoesNotContain("--- a/", line, StringComparison.Ordinal));
        Assert.All(guidance, line => Assert.DoesNotContain("environment:", line, StringComparison.Ordinal));

        // ...and the proposal is the other half: a YAML fragment, not a restatement of the checklist.
        Assert.Contains("environment:", proposal.SuggestedEdit, StringComparison.Ordinal);

        // COMPLEMENTARY, asserted in both directions. The earlier one-way check
        // (DoesNotContain(fragment, guidanceLines)) was tautological — a multi-line YAML fragment can
        // never equal a single guidance line, so it could not fail. These compare CONTENT: no
        // guidance line is reproduced inside the fragment, and no fragment line inside the guidance.
        var fragmentLines = proposal.SuggestedEdit
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 12)
            .ToList();

        foreach (var line in guidance)
        {
            Assert.DoesNotContain(line, proposal.SuggestedEdit, StringComparison.Ordinal);
        }

        foreach (var fragmentLine in fragmentLines)
        {
            Assert.DoesNotContain(guidance, g => g.Contains(fragmentLine, StringComparison.Ordinal));
        }
    }

    // ── AC 2: the classification is ADDITIVE — it never changes a verdict ───────────────────────

    /// <summary>
    /// The same run, with and without material the rule table can classify, resolves to the SAME
    /// verdict. The classification is additive: it explains a verdict, it never participates in
    /// choosing one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each pair below drives the identical code path <c>ExplainRunOrchestrator</c>'s own
    /// verdict-elevation tests drive — those tests are left untouched, as the acceptance criterion
    /// requires; this adds fixture VARIANTS through the same path rather than editing their
    /// assertions.
    /// </para>
    /// <para>
    /// <b>Only the CLASSIFIABLE side is asserted to carry a kind, and the asymmetry is deliberate.</b>
    /// A first draft also asserted the "bare" side carried none, and that was wrong on two rows —
    /// correctly so: an <c>environment-error</c> RECORD is always DESCRIBED (US-S4-02's reading of
    /// "classified", with a null kind for an unrecognised <c>errorKind</c>), and an
    /// <c>Inconclusive</c> step with no attempts at all is still a <c>timeout</c>, empty variant.
    /// "Carries no classification" is therefore not a property a fixture can be relied on to have,
    /// and asserting it would have pinned an accident rather than a contract. What this test needs is
    /// only that the classified side genuinely IS classified — otherwise it would compare two
    /// unclassified runs and prove nothing.
    /// </para>
    /// <para>
    /// <c>Pass</c> is the one row with <paramref name="expectsClassifiedKind"/> false: a passing step
    /// is never notable, so there is nothing for the rule table to classify — which is itself the
    /// invariant that a Pass run stays a Pass run.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Pass", """{"type":"step-completed","stepId":"s","verdict":"PASS","durationMs":5}""", """{"type":"step-completed","stepId":"s","verdict":"PASS","durationMs":5,"observation":{"expected":"UP","actual":"UP"}}""", false)]
    [InlineData("Fail", """{"type":"step-completed","stepId":"s","verdict":"FAIL","durationMs":5}""", """{"type":"step-completed","stepId":"s","verdict":"FAIL","durationMs":5,"observation":{"expected":"A","actual":"B"}}""", true)]
    [InlineData("Inconclusive", """{"type":"step-completed","stepId":"s","verdict":"INCONCLUSIVE","durationMs":5}""", """{"type":"step-completed","stepId":"s","verdict":"INCONCLUSIVE","durationMs":5,"observation":{"reason":"partition grace period exceeded"}}""", true)]
    [InlineData("EnvironmentError", """{"type":"environment-error","errorKind":"Provision","resourceName":"db","detail":"gone"}""", """{"type":"environment-error","errorKind":"HealthGate","resourceName":"db","detail":"health gate timed out after 30000ms"}""", true)]
    public async Task AddingClassifiableMaterial_NeverChangesWhichVerdictARunResolvesTo(
        string expectedVerdict, string bare, string classifiable, bool expectsClassifiedKind)
    {
        var withoutClassification = await DiagnoseAsync(bare);
        var withClassification = await DiagnoseAsync(classifiable);

        // THE assertion: the verdict is what it always was, on both.
        Assert.Equal(expectedVerdict, withoutClassification.Verdict);
        Assert.Equal(expectedVerdict, withClassification.Verdict);

        // Precondition, so this is not a comparison of two unclassified runs.
        var classifiedKinds = withClassification.NotableSteps.Select(s => s.Reason?.Kind)
            .Concat(withClassification.EnvironmentErrors.Select(e => e.Reason?.Kind))
            .Where(kind => kind is not null)
            .ToList();

        Assert.Equal(expectsClassifiedKind, classifiedKinds.Count > 0);
    }

    /// <summary>
    /// The unknown-token contract, re-asserted here because Sprint 4's rule table reads the same
    /// verdict strings: an unrecognised wire token still parses to <see langword="null"/> and never
    /// throws, and a step carrying one is never classified.
    /// </summary>
    [Fact]
    public void AnUnknownWireToken_StillParsesToNull_AndIsNeverClassified()
    {
        Assert.Null(RunVerdictExtensions.ParseWireToken("SOMETHING_NEW"));
        Assert.Null(RunVerdictExtensions.ParseWireToken(null));

        var summary = SuiteEventParser.Parse(string.Empty);
        var step = new StepOutcome(
            "s", "SomeFutureVerdict", 10, 1, """{"expected":"A","actual":"B","reason":"partition"}""");

        Assert.Null(VerdictReasonClassifier.ClassifyStep(step, summary));
    }

    // ── Ledger (c): no secret reference is ever RESOLVED, on either surface ─────────────────────

    /// <summary>
    /// Invariant 4, swept corpus-wide across BOTH surfaces this sprint mints: a <c>${secret:…}</c>
    /// reference the engine relayed is never resolved into a hint, a rationale, or a fragment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// US-S4-01 pinned this for hints and US-S4-03 for fragments, each on one fixture. This is the
    /// sweep the sprint's exit checklist asks for, with the named environment variable actually SET
    /// so a resolving implementation would have something to resolve to.
    /// </para>
    /// <para>
    /// <b>Coverage is PROVEN per row, not claimed.</b> A first version injected into
    /// <c>"detail"</c>/<c>"note"</c> only and described itself as "every fixture, every surface" — it
    /// reached 7 of 19 rows, and the twelve it missed included the corpus's own
    /// <c>SecretReferenceObservationFixture</c>, which was passing without carrying a reference at
    /// all. The injector below covers every free-text shape the corpora use, and each row ASSERTS
    /// that injection took effect and that the reference actually reached a swept surface — the same
    /// prove-the-case-was-reached discipline the stage-4 window search uses. A row with no injection
    /// point fails loudly rather than passing quietly.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(SprintFixtures))]
    public async Task NoSecretReference_IsEverResolvedIntoAnyHintRationaleOrFragment(string name, string events)
    {
        const string sentinelName = "VOUCHFX_MCP_S4_REGRESSION_SENTINEL_NEVER_RESOLVED";
        const string sentinelValue = "s3ntinel-resolved-secret-6b02fa19";
        const string reference = "${secret:env/" + sentinelName + "}";

        var (withSecret, injectionPoints) = InjectSecretReference(events, reference);
        Assert.True(
            injectionPoints > 0,
            $"Fixture {name}: no free-text field to carry a secret reference, so this row cannot "
            + "exercise the sweep. Extend InjectSecretReference to cover its shape rather than "
            + "leaving the row silently green.");

        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var diagnosis = await DiagnoseAsync(withSecret);

            var surfaces = new List<string>(diagnosis.ClassificationHints) { diagnosis.Summary };
            surfaces.AddRange(diagnosis.NotableSteps.Select(s => s.Reason?.Hint).OfType<string>());
            surfaces.AddRange(diagnosis.EnvironmentErrors.Select(e => e.Reason?.Hint).OfType<string>());
            surfaces.AddRange(diagnosis.NotableSteps.Select(s => s.Observation).OfType<string>());
            surfaces.AddRange(diagnosis.EnvironmentErrors.Select(e => e.Detail).OfType<string>());

            foreach (var proposal in SpecEditProposalBuilder.BuildProposals(diagnosis))
            {
                surfaces.Add(proposal.Rationale);
                surfaces.Add(proposal.SuggestedEdit);
            }

            foreach (var proposal in FailProposalBuilder.BuildProposals(diagnosis))
            {
                surfaces.Add(proposal.Rationale);
                surfaces.Add(proposal.Patch);
            }

            surfaces.AddRange(FailProposalBuilder.BuildEnvironmentGuidance(diagnosis));

            // Proof the case was reached: the reference really did flow from the events file into
            // agent-facing text on this row, so the "never resolved" assertion below has something
            // to be about.
            Assert.Contains(surfaces, text => text.Contains(reference, StringComparison.Ordinal));

            // ...and proof it was reached on the SAME code paths: injecting the reference must not
            // reclassify the row, or the sweep would silently stop covering whichever hint builder
            // the original classification used. A review found exactly that — injecting into
            // `errorKind` pushed every environment-error row onto the unrecognised-kind path, so
            // BuildPullReason/BuildUnhealthyReason/BuildSeedHint went uncovered while the test stayed
            // green. This assertion is what makes the injector's field list falsifiable rather than
            // merely commented.
            var original = await DiagnoseAsync(events);
            Assert.Equal(KindsOf(original), KindsOf(diagnosis));

            foreach (var text in surfaces)
            {
                Assert.False(
                    text.Contains(sentinelValue, StringComparison.Ordinal),
                    $"Fixture {name}: a secret reference was RESOLVED into agent-facing text. The engine is "
                    + "the sole redaction authority; this server relays a reference and never resolves one.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    /// <summary>Every <c>reason.kind</c> a diagnosis carries, in a stable order — the fingerprint the sweep compares before and after injection.</summary>
    private static string[] KindsOf(Vouchfx.Mcp.Diagnosis.Diagnosis diagnosis) =>
        diagnosis.NotableSteps.Select(s => s.Reason?.Kind ?? "(none)")
            .Concat(diagnosis.EnvironmentErrors.Select(e => e.Reason?.Kind ?? "(none)"))
            .ToArray();

    /// <summary>
    /// Prefixes a <c>${secret:…}</c> reference onto every free-text JSON field the sprint's fixtures
    /// use, returning the mutated events and how many injection points were found.
    /// </summary>
    /// <remarks>
    /// The key list is derived from the corpora's own shapes rather than guessed: environment errors
    /// carry <c>detail</c>/<c>resourceName</c>, step observations carry <c>expected</c>/
    /// <c>actual</c>/<c>got</c>/<c>reason</c>/<c>note</c>/<c>seen</c>/<c>key</c>. Prefixing keeps the
    /// JSON valid and the surrounding fixture semantics intact — the row still classifies as whatever
    /// it classified before, which is what makes this a sweep of the REAL corpus rather than a
    /// substitute one.
    /// </remarks>
    private static (string Events, int InjectionPoints) InjectSecretReference(string events, string reference)
    {
        // PRIMARY fields: every one is a free-text VALUE that no rule dispatches on by equality, so
        // prefixing a reference leaves the row classifying exactly as it did — which is what makes
        // this a sweep of the real corpus rather than of a mutated one. Checked per field:
        // `detail`/`note`/`seen`/`key` are never dispatched on at all; `expected`/`actual` are tested
        // for PRESENCE (assertion) or for an explicit null (capture-unmet), not for content;
        // `reason` is only ever substring-scanned for the partition signal, which a prefix cannot
        // hide; `stepId` is an identifier that is relayed, never matched.
        //
        // `stepId` earns its place by MEASUREMENT, not tidiness: one corpus row (an Inconclusive step
        // with no observation at all) carries no other free-text field, and the per-row proof above
        // failed on it until this was added — exactly what that proof exists to surface.
        string[] primaryFields =
            ["detail", "resourceName", "stepId", "expected", "actual", "reason", "note", "seen", "key"];

        // FALLBACK only, and the distinction is load-bearing (a review finding). `errorKind` IS
        // dispatched on by set membership — PullErrorKinds/UnhealthyErrorKinds/SeedErrorKinds are
        // exact-match sets — so prefixing it turns every environment-error row into the
        // unrecognised-kind path and the sweep would stop exercising BuildPullReason /
        // BuildUnhealthyReason / BuildSeedHint at all: precisely the identifier-splicing hint
        // builders it most needs to cover. It stays available for a hypothetical future fixture whose
        // ONLY caller-influenced text is an error kind, and is used only when nothing above matched.
        string[] fallbackFields = ["errorKind"];
        var (injected, points) = InjectInto(events, primaryFields, reference);

        return points > 0
            ? (injected, points)
            : InjectInto(events, fallbackFields, reference);
    }

    private static (string Events, int InjectionPoints) InjectInto(string events, string[] fields, string reference)
    {
        var injected = events;
        var points = 0;

        foreach (var field in fields)
        {
            var marker = $"\"{field}\":\"";
            points += Regex.Matches(injected, Regex.Escape(marker)).Count;
            injected = injected.Replace(marker, marker + reference + " ", StringComparison.Ordinal);
        }

        return (injected, points);
    }

    // ── Ledger (a): the repo-wide environment-variable guard ───────────────────────────────────

    /// <summary>
    /// Every environment-variable API this guard recognises — <c>Get</c>/<c>Set</c>/<c>Expand</c>,
    /// singular and PLURAL.
    /// </summary>
    /// <remarks>
    /// <b>The plural and the <c>Expand</c> form are not padding.</b> A first version matched only
    /// <c>GetEnvironmentVariable(</c>/<c>SetEnvironmentVariable(</c>, and three reviewers
    /// independently found it fail-open: <c>Environment.GetEnvironmentVariables()</c> returns the
    /// WHOLE block (strictly more secret material than one named read), and
    /// <c>Environment.ExpandEnvironmentVariables("%PATH%")</c> reads variables through a string —
    /// both planted, both passing. The captured verb group is what splits writers from readers;
    /// <c>Expand</c> is classed as a READ because that is what it does.
    /// </remarks>
    private const string EnvironmentApiPattern = @"\b(Get|Set|Expand)EnvironmentVariables?\s*\(";

    /// <summary>
    /// A plant test for the pattern above: the shapes a review found slipping through must MATCH it,
    /// and their verbs must classify correctly.
    /// </summary>
    /// <remarks>
    /// Guards over source text are only as good as their pattern, and a pattern is the one part of a
    /// guard that no fixture exercises — so it is asserted directly here rather than trusted.
    /// </remarks>
    [Theory]
    [InlineData("Environment.GetEnvironmentVariable(\"PATH\")", true, "Get")]
    [InlineData("Environment.GetEnvironmentVariables()", true, "Get")]
    [InlineData("Environment.SetEnvironmentVariable(\"X\", \"y\")", true, "Set")]
    [InlineData("Environment.ExpandEnvironmentVariables(\"%PATH%\")", true, "Expand")]
    [InlineData("Environment . GetEnvironmentVariable (\"PATH\")", true, "Get")]
    [InlineData("var environmentVariableName = \"PATH\";", false, "")]
    public void TheEnvironmentApiPattern_MatchesEveryShapeItMustCatch(string source, bool expectedMatch, string expectedVerb)
    {
        var match = Regex.Match(source, EnvironmentApiPattern);

        Assert.Equal(expectedMatch, match.Success);
        if (expectedMatch)
        {
            Assert.Equal(expectedVerb, match.Groups[1].Value);
        }
    }

    /// <summary>
    /// No file in <c>src/</c> may WRITE a process environment variable, and only the CLI path
    /// resolver may READ one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes.</b> <c>SecretHygieneSourceGuardTests</c> already forbids curating a
    /// child process's environment — but only at the PROCESS-SPAWN SITES it enumerates. Sprint 4
    /// added surfaces that build agent-facing TEXT and spawn nothing at all (the rule table's hints,
    /// the Healer's rationales and YAML fragments), and a future rule that read
    /// <c>Environment.GetEnvironmentVariable</c> to "helpfully" resolve a <c>${secret:env/X}</c>
    /// reference into a proposal would have been caught by no guard whatever: it is not a spawn
    /// site. This one is repo-wide over <see cref="SourceGuardScan.SourceFilesInSrc"/>.
    /// </para>
    /// <para>
    /// <b>Fail-closed by exact equality, not by a substring allowance.</b> The reader set is asserted
    /// to be EXACTLY one file, so a second reader anywhere in <c>src/</c> fails this test until
    /// someone widens the list deliberately — the same shape
    /// <c>SecretHygieneSourceGuardTests.ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet</c> uses.
    /// The one permitted reader resolves <c>PATH</c>/<c>PATHEXT</c> to find the <c>vouchfx</c>
    /// executable, which is process discovery, not secret material.
    /// </para>
    /// </remarks>
    [Fact]
    public void EnvironmentVariableAccessInSrc_IsWriteFreeAndReadOnlyFromTheCliPathResolver()
    {
        var writers = new List<string>();
        var readers = new List<string>();

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            var executable = SourceGuardScan.ExecutableSourceOf(file);
            var relative = SourceGuardScan.ToRepoRelativeForwardSlashPath(file);

            foreach (Match match in Regex.Matches(executable, EnvironmentApiPattern))
            {
                // Partition on the VERB the pattern captured: only Set writes.
                if (string.Equals(match.Groups[1].Value, "Set", StringComparison.Ordinal))
                {
                    writers.Add(relative);
                }
                else
                {
                    readers.Add(relative);
                }
            }
        }

        writers = writers.Distinct(StringComparer.Ordinal).ToList();
        readers = readers.Distinct(StringComparer.Ordinal).ToList();

        Assert.True(
            writers.Count == 0,
            $"These files WRITE a process environment variable: [{string.Join(", ", writers)}]. This server "
            + "never sets one — the engine is the sole authority over secret/environment content, and a child "
            + "inherits this process's environment implicitly.");

        Assert.Equal(["src/Vouchfx.Mcp/Cli/VouchfxCliPathResolver.cs"], readers.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// Removes <c>//</c> and <c>/* … */</c> comments, keeping every string literal — the inverse of
    /// <see cref="SourceGuardScan.ExecutableSourceOf"/>, for the one guard that has to read the
    /// literals themselves.
    /// </summary>
    /// <remarks>
    /// Deliberately simple, and safe for THIS file: a <c>//</c> sequence inside one of the builder's
    /// own fragment templates would break it, so the templates are asserted below to be intact after
    /// stripping. (They use YAML <c>#</c> comments and single-slash paths, never <c>//</c>.)
    /// </remarks>
    private static string StripCommentsKeepingLiterals(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//[^\n]*", string.Empty);
    }

    private static async Task<Vouchfx.Mcp.Diagnosis.Diagnosis> DiagnoseAsync(string events)
    {
        var path = Path.Combine(Path.GetTempPath(), $"healer-scope-regression-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(path, events);
        try
        {
            var outcome = await new ExplainRunOrchestrator(new InMemoryRunRegistry())
                .ExplainAsync(path, CancellationToken.None);
            return Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome).Diagnosis;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
