using System.Text.RegularExpressions;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Diagnosis;

/// <summary>
/// Covers US-S4-03's <see cref="SpecEditProposalBuilder"/> — plan D2's Healer superset — driven
/// through the REAL pipeline: an inline JSON Lines fixture, parsed and diagnosed by
/// <see cref="ExplainRunOrchestrator"/>, then handed to the builder exactly as
/// <see cref="DiagnoseRunOrchestrator"/> hands it.
/// </summary>
/// <remarks>
/// Going through the orchestrator rather than hand-building a <c>Diagnosis</c> is what makes these
/// tests meaningful: the builder keys entirely off <c>reason.kind</c>, so a hand-built diagnosis
/// would let a test assert a classification US-S4-01's rule table would never actually produce —
/// and the Fail/EnvironmentError partition this story rests on would then be tested against a
/// fiction rather than against the real classifier.
/// </remarks>
public class SpecEditProposalBuilderTests
{
    // ── Gherkin 1: an unhealthy environment error → one environment-scoped proposal ─────────────

    [Fact]
    public async Task AnUnhealthyEnvironmentError_YieldsOneEnvironmentScopedProposal()
    {
        const string events = """
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"health gate timed out after 30000ms"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Equal(SpecEditScopes.Environment, proposal.Scope);

        // The rationale references the resource name AND the observed health-gate window.
        Assert.Contains("events", proposal.Rationale, StringComparison.Ordinal);
        Assert.Contains("30000ms", proposal.Rationale, StringComparison.Ordinal);

        // A YAML fragment, not a unified diff against a real file.
        Assert.Contains("environment:", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain("--- a/", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain("+++ b/", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain("@@", proposal.SuggestedEdit, StringComparison.Ordinal);

        // An environment-error record is not a step — see SpecEditProposal.StepId's remarks.
        Assert.Null(proposal.StepId);
    }

    [Fact]
    public async Task AnImagePullEnvironmentError_NamesTheSameImageItsHintDoes()
    {
        const string events = """
            {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"manifest for ghcr.io/acme/orders-api:latest not found"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Equal(SpecEditScopes.Environment, proposal.Scope);
        Assert.Contains("image: 'ghcr.io/acme/orders-api:latest'", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeedEnvironmentError_YieldsAnEnvironmentScopedProposalNamingTheSeedTarget()
    {
        const string events = """
            {"type":"environment-error","errorKind":"Seed","resourceName":"orders-db","detail":"relation orders does not exist"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Equal(SpecEditScopes.Environment, proposal.Scope);
        Assert.Contains("seed:", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("'orders-db':", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>errorKind</c> US-S4-01 declined to classify gets guidance text and no mechanical
    /// suggestion — the fail-closed default inherited, not re-decided here.
    /// </summary>
    [Fact]
    public async Task AnUnclassifiedEnvironmentError_YieldsNoProposalAtAll()
    {
        const string events = """
            {"type":"environment-error","errorKind":"SomeFutureEngineKind","resourceName":"events","detail":"never heard of it"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        Assert.Empty(await BuildAsync(events));
    }

    // ── Gherkin 2: a non-empty-observed timeout → BOTH a timeouts and a match proposal ──────────

    [Fact]
    public async Task ANonEmptyObservedTimeout_YieldsBothATimeoutsAndAMatchProposal()
    {
        const string events = """
            {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false,"seen":"order_id"}}
            {"type":"step-attempt","stepId":"expect-order-event","attempt":2,"tMs":300,"outcome":"FAIL","observation":{"matched":false,"seen":"order_id"}}
            {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300,"observation":{"reason":"retry-timeout","attempts":2}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var proposals = await BuildAsync(events);

        Assert.Equal(2, proposals.Count);
        Assert.Equal([SpecEditScopes.Timeouts, SpecEditScopes.Match], proposals.Select(p => p.Scope));
        Assert.All(proposals, p => Assert.Equal("expect-order-event", p.StepId));

        var timeouts = proposals[0];
        Assert.Contains("verifyMode: RETRY", timeouts.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("timeout:", timeouts.SuggestedEdit, StringComparison.Ordinal);

        var match = proposals[1];
        Assert.Contains("match:", match.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("key:", match.SuggestedEdit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The empty variant yields the <c>timeouts</c> proposal alone: nothing was observed, so there is
    /// no evidence the match criteria are at fault.
    /// </summary>
    [Fact]
    public async Task AnEmptyObservedTimeout_YieldsTheTimeoutsProposalOnly()
    {
        const string events = """
            {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL"}
            {"type":"step-attempt","stepId":"expect-order-event","attempt":2,"tMs":300,"outcome":"FAIL"}
            {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Equal(SpecEditScopes.Timeouts, proposal.Scope);
        Assert.DoesNotContain("could not be assessed", proposal.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timeout whose attempt timeline was TRIMMED AWAY by the response tiers still yields both
    /// proposals: the variant is a fact the classifier established from untrimmed data and published
    /// on <see cref="VerdictEvidence"/>, so response size cannot change what the run is advised.
    /// </summary>
    /// <remarks>
    /// <b>This test was inverted by review, and the inversion is the fix.</b> It previously asserted
    /// the opposite — that a trimmed timeline made the variant "unknown", withheld the match
    /// proposal, and appended "whether any value was observed could not be assessed here" to a
    /// rationale whose first sentence already read "Observed 6 value(s) but none matched". The
    /// builder was fabricating ignorance about a fact it was holding, and contradicting itself in one
    /// paragraph. The shape below is exactly what <c>BuildDiagnosisAtTier</c> produces at the floor
    /// tier: attempts emptied, <c>OmittedAttemptCount</c> recording how many went.
    /// </remarks>
    [Fact]
    public void ATimeoutWhoseAttemptTimelineWasTrimmedAway_StillYieldsBothProposals()
    {
        var step = new StepDiagnosis(
            "expect-order-event",
            nameof(RunVerdict.Inconclusive),
            DurationMs: 1300,
            AttemptCount: 6,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 6,
            Reason: new VerdictReason(VerdictReasonKinds.Timeout, "Observed 6 value(s) but none matched; the match key or capture path is probably wrong.")
            {
                Evidence = new VerdictEvidence(ObservedValues: true),
            });

        var proposals = SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step));

        Assert.Equal([SpecEditScopes.Timeouts, SpecEditScopes.Match], proposals.Select(p => p.Scope));
        Assert.All(proposals, p => Assert.DoesNotContain("could not be assessed", p.Rationale, StringComparison.Ordinal));
    }

    /// <summary>
    /// The evidence channel is what the builder branches on — proven by holding the HINT constant
    /// and flipping only the structured flag.
    /// </summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void TheMatchProposal_TracksTheEvidenceFlagAndNotTheHintWording(bool observedValues, int expectedCount)
    {
        const string identicalHint = "a hint whose wording says nothing either way";
        var step = new StepDiagnosis(
            "expect-order-event",
            nameof(RunVerdict.Inconclusive),
            DurationMs: 1300,
            AttemptCount: 6,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 0,
            Reason: new VerdictReason(VerdictReasonKinds.Timeout, identicalHint)
            {
                Evidence = new VerdictEvidence(ObservedValues: observedValues),
            });

        Assert.Equal(expectedCount, SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step)).Count);
    }

    // ── Gherkin 3: capture_unmet → exactly one capture-scoped proposal ──────────────────────────

    [Fact]
    public async Task ACaptureUnmetStep_YieldsExactlyOneCaptureScopedProposal()
    {
        const string events = """
            {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":50,"observation":{"expected":"orderId","got":null}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Equal(SpecEditScopes.Capture, proposal.Scope);
        Assert.Equal("seed-order", proposal.StepId);
        Assert.Contains("capture:", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("$.", proposal.SuggestedEdit, StringComparison.Ordinal);

        // The INFERRED shape publishes no capture name (the classifier declines to call its
        // `expected` value one), so the variable slot keeps its placeholder rather than emitting a
        // possibly-literal value as a YAML key.
        Assert.Contains("'<variable-name>':", proposal.SuggestedEdit, StringComparison.Ordinal);

        // ...and the placeholder is ANNOUNCED, because quoting makes it read like a real name in key
        // position — the same answer ResourceKey's unnamed-resource note gives (a peer-review nit).
        Assert.Contains(
            "# (The engine did not name the capture; fill in the one this step declares.)",
            proposal.SuggestedEdit,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The ENGINE's own capture-unmet observation (vouchfx-mcp#86) yields the same capture-scoped
    /// proposal — never a <c>timeouts</c>/<c>match</c> one — and its fragment names the capture the
    /// engine reported, taken from <see cref="VerdictEvidence.CaptureName"/>.
    /// </summary>
    /// <remarks>
    /// Before the classifier read that key this step classified as <c>timeout</c>, so this fixture
    /// produced TWO proposals in the wrong scopes. The scope assertion below is therefore the
    /// regression guard, and the fragment assertion is the new capability.
    /// </remarks>
    [Fact]
    public async Task TheEngineCaptureUnmetShape_YieldsTheCaptureProposal_NamingTheEnginesOwnCapture()
    {
        var proposal = Assert.Single(await BuildAsync(EngineCaptureUnmetEvents));

        Assert.Equal(SpecEditScopes.Capture, proposal.Scope);
        Assert.Equal("capture-present-and-missing", proposal.StepId);
        Assert.Contains("'missing': \"$.<path.to.the.value>\"", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain("<variable-name>", proposal.SuggestedEdit, StringComparison.Ordinal);

        // The unnamed-capture note is the placeholder's companion, so a NAMED capture must not carry
        // it — otherwise the fragment would disclaim the very name it just rendered.
        Assert.DoesNotContain("did not name the capture", proposal.SuggestedEdit, StringComparison.Ordinal);

        // ...and the rationale is the classifier's hint, so the two cannot describe different things.
        Assert.StartsWith(
            "Step capture-present-and-missing declared capture missing but its path matched nothing",
            proposal.Rationale,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The corpus row's <c>captureUnmet</c> field really is what feeds the YAML KEY slot — so
    /// <c>HealerScopeRegressionTests</c>' secret sweep, which injects into that field, genuinely
    /// covers this story's one engine-fed fragment slot.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the sweep alone cannot prove it</b> (a security review's finding). That
    /// row carries TWO injectable fields (<c>stepId</c> and <c>captureUnmet</c>), and the sweep
    /// asserts only that SOME injected reference reached agent-facing text — so it would stay green
    /// on the step id alone while the key slot went untouched. Mutating exactly the field the
    /// injector mutates, and asserting where the text lands, turns that coverage claim into a
    /// measured one: if the marker were ever absent from the fixture the replacement below is a
    /// no-op and this fails.
    /// </remarks>
    [Fact]
    public async Task TheCaptureKeySlot_IsFedByTheCaptureUnmetField_WhichIsWhatTheSecretSweepInjectsInto()
    {
        const string reference = "${secret:env/VOUCHFX_MCP_PROPOSAL_SENTINEL_NEVER_RESOLVED}";
        var injected = EngineCaptureUnmetEvents.Replace(
            "\"captureUnmet\":\"", $"\"captureUnmet\":\"{reference} ", StringComparison.Ordinal);
        Assert.NotEqual(EngineCaptureUnmetEvents, injected);

        var proposal = Assert.Single(await BuildAsync(injected));

        Assert.Equal(SpecEditScopes.Capture, proposal.Scope);
        Assert.Contains(
            $"'{reference} missing': \"$.<path.to.the.value>\"",
            proposal.SuggestedEdit,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture name carrying a YAML quote survives the WHOLE path — events line → parser →
    /// classifier → evidence → fragment — as a doubled quote inside a single-quoted key.
    /// </summary>
    /// <remarks>
    /// <b>This closes a seam between two half-tests</b> (a peer-review nit). The hostile-name
    /// theories hand the builder an evidence object directly, and the end-to-end tests use a benign
    /// name; neither proves that a quote SURVIVING a real parse still gets doubled. The failure this
    /// guards is silent and total: an undoubled <c>'</c> terminates the scalar, so the pasted
    /// fragment is a YAML syntax error rather than a wrong-but-valid edit.
    /// </remarks>
    [Fact]
    public async Task ACaptureNameCarryingAQuote_IsDoubled_AllTheWayFromTheEventsLine()
    {
        var events = EngineCaptureUnmetEvents.Replace(
            "\"captureUnmet\":\"missing\"", "\"captureUnmet\":\"cap'ture\"", StringComparison.Ordinal);
        Assert.NotEqual(EngineCaptureUnmetEvents, events);

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Contains("'cap''ture': \"$.<path.to.the.value>\"", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain("'cap'ture'", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The builder's own <c>Identifier</c> gate re-sanitises and re-caps a capture name the
    /// CLASSIFIER already capped — and that second pass returns the identical string, marker and all.
    /// </summary>
    /// <remarks>
    /// <b>Not obvious, which is why it is pinned</b> (a peer-review nit). The marker <c>…</c> is
    /// U+2026, so the builder's <see cref="TextSanitiser.SanitiseForDisplay"/> expands it to the six
    /// characters of its unicode escape, pushing the already-at-cap value OVER the cap and making
    /// the second <c>Cap</c> truncate again. It lands on the same string only because everything
    /// before the marker is printable ASCII (the classifier's own sanitise-then-cap guarantees it),
    /// so the re-cut falls at exactly the same index. Were that not so, a host would see a
    /// double-truncated key — or a raw <c>…</c> in YAML key position.
    /// </remarks>
    [Fact]
    public async Task AnAlreadyCappedCaptureName_SurvivesTheBuildersReSanitiseUnchanged()
    {
        var events = EngineCaptureUnmetEvents.Replace(
            "\"captureUnmet\":\"missing\"",
            $"\"captureUnmet\":\"{new string('n', 500)}\"",
            StringComparison.Ordinal);

        var diagnosis = await DiagnoseAsync(events);
        var capped = Assert.Single(diagnosis.NotableSteps).Reason?.Evidence?.CaptureName;

        // Precondition: the classifier really did cap it and mark the cut.
        Assert.Equal(VerdictReasonClassifier.MaxValueChars, capped?.Length);
        Assert.EndsWith("…", capped, StringComparison.Ordinal);

        var proposal = Assert.Single(SpecEditProposalBuilder.BuildProposals(diagnosis));

        // The IDENTICAL string in key position — not re-truncated, and not carrying an escaped marker.
        Assert.Contains($"'{capped}': \"$.<path.to.the.value>\"", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            TextSanitiser.SanitiseForDisplay("…"),
            proposal.SuggestedEdit,
            StringComparison.Ordinal);
    }

    // ── Gherkin 4: a partition signal never yields a proposal ───────────────────────────────────

    [Fact]
    public async Task APartitionSignal_NeverYieldsASpecEditProposal_AndTheGuidanceStillDescribesIt()
    {
        const string events = """
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"reason":"partition grace period exceeded for topic orders"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var diagnosis = await DiagnoseAsync(events);

        // The step IS classified — it is the classification the builder declines to act on.
        Assert.Equal(VerdictReasonKinds.Partition, Assert.Single(diagnosis.NotableSteps).Reason?.Kind);
        Assert.Empty(SpecEditProposalBuilder.BuildProposals(diagnosis));

        // ...and the existing Inconclusive guidance text is untouched by this story.
        Assert.NotEmpty(FailProposalBuilder.BuildEnvironmentGuidance(diagnosis));
    }

    // ── Gherkin 5: a Fail step never yields a spec-edit proposal ────────────────────────────────

    [Fact]
    public async Task AFailStep_YieldsNoSpecEditProposal_OnlyItsExistingReviewProposal()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal(VerdictReasonKinds.Assertion, Assert.Single(diagnosis.NotableSteps).Reason?.Kind);
        Assert.Empty(SpecEditProposalBuilder.BuildProposals(diagnosis));

        // The existing Fail-only review proposal is still produced, unchanged.
        var failProposal = Assert.Single(FailProposalBuilder.BuildProposals(diagnosis));
        Assert.Equal("check-balance", failProposal.StepId);
        Assert.Contains("--- a/", failProposal.Patch, StringComparison.Ordinal);
    }

    /// <summary>
    /// The partition is STRUCTURAL: even a hand-built Fail step carrying a kind US-S4-01 would never
    /// give it cannot reach a proposal branch, because the builder filters on the VERDICT first.
    /// </summary>
    [Fact]
    public void AFailStepCarryingAnEditableKind_IsStillRefusedByTheVerdictFilter()
    {
        foreach (var kind in new[]
                 {
                     VerdictReasonKinds.Timeout, VerdictReasonKinds.CaptureUnmet,
                     VerdictReasonKinds.Pull, VerdictReasonKinds.Unhealthy, VerdictReasonKinds.Seed,
                 })
        {
            var step = new StepDiagnosis(
                "check-balance",
                nameof(RunVerdict.Fail),
                DurationMs: 10,
                AttemptCount: 1,
                Observation: null,
                Attempts: [],
                OmittedAttemptCount: 0,
                Reason: new VerdictReason(kind, "a hint that should never be acted on for a Fail step"));

            Assert.Empty(SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step)));
        }
    }

    // ── Gherkin 6 / AC: scopes are a closed set, and nothing is ever applied ────────────────────

    /// <summary>
    /// US-S4-03's own acceptance criterion: enumerate every scope this builder can EVER emit and
    /// assert the set is exactly the four permitted values.
    /// </summary>
    /// <remarks>
    /// Derived from SOURCE, not from a fixture sweep (the pattern <c>SecretHygieneSourceGuardTests</c>
    /// uses, and the one US-S4-05 will extend): a fixture corpus can only show what the shapes it
    /// happens to contain produce, whereas this reads every <c>SpecEditScopes.X</c> the builder
    /// mentions. A fifth scope added to the builder fails here even if no test fixture reaches it.
    /// </remarks>
    [Fact]
    public void EveryScopeTheBuilderCanEmit_IsExactlyTheFourPermittedValues()
    {
        var builderPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Diagnosis", "SpecEditProposalBuilder.cs");
        Assert.True(File.Exists(builderPath), $"Expected the builder source at '{builderPath}'.");

        var emitted = Regex
            .Matches(SourceGuardScan.ExecutableSourceOf(builderPath), @"SpecEditScopes\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Capture", "Environment", "Match", "Timeouts"], emitted);

        // The enumeration above is only sound while every proposal gets its scope from that
        // vocabulary. A bare string literal would evade it entirely — SourceGuardScan BLANKS string
        // literals, so `new SpecEditProposal(id, "sneaky", …)` would show up as neither a
        // SpecEditScopes reference nor a literal. Counting construction sites against scope
        // references closes that: they must match, and the type must be constructed nowhere else in
        // src/ (which would be a second, unenumerated builder).
        var builderSource = SourceGuardScan.ExecutableSourceOf(builderPath);
        var constructionSites = Regex.Matches(builderSource, @"new SpecEditProposal\(").Count;
        var scopeReferences = Regex.Matches(builderSource, @"SpecEditScopes\.\w+").Count;
        Assert.Equal(scopeReferences, constructionSites);

        // Exactly one OTHER file may construct the type: DiagnoseRunOrchestrator's shrink ladder,
        // which rebuilds proposals to elide their bodies. That is not a second builder — it can only
        // PROPAGATE a scope it was handed, never mint one, which is asserted directly below rather
        // than assumed: its construction sites reference no SpecEditScopes member at all.
        var ladderPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Diagnosis", "DiagnoseRunOrchestrator.cs");
        var ladderSource = SourceGuardScan.ExecutableSourceOf(ladderPath);

        Assert.Contains("new SpecEditProposal(", ladderSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecEditScopes.", ladderSource, StringComparison.Ordinal);

        // ...and those two assertions alone are NOT enough (a review caught this claiming more than
        // it proved): `new SpecEditProposal(p.StepId, "expect", …)` satisfies both, because
        // SourceGuardScan blanks string literals — the very evasion the counting technique above
        // exists to close. So count the construction sites that PROPAGATE both identity fields and
        // require every site to be one of them.
        var ladderConstructions = Regex.Matches(ladderSource, @"new SpecEditProposal\(").Count;
        var ladderPropagations = Regex.Matches(ladderSource, @"new SpecEditProposal\(\s*p\.StepId,\s*p\.Scope,").Count;
        Assert.Equal(ladderConstructions, ladderPropagations);

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            if (string.Equals(file, builderPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, ladderPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.DoesNotContain(
                "new SpecEditProposal(",
                SourceGuardScan.ExecutableSourceOf(file),
                StringComparison.Ordinal);
        }

        // ...and those four names really are the whole vocabulary, so the source enumeration above
        // cannot be satisfied by a scope the model does not declare.
        Assert.Equal(
            ["capture", "environment", "match", "timeouts"],
            SpecEditScopes.All.OrderBy(scope => scope, StringComparer.Ordinal));
    }

    /// <summary>
    /// Across every fixture this class ships, no proposal ever carries a scope outside the closed set
    /// — the behavioural counterpart to the source enumeration above.
    /// </summary>
    [Fact]
    public async Task AcrossEveryFixture_NoProposalCarriesAScopeOutsideTheClosedSet()
    {
        foreach (var events in AllFixtures)
        {
            foreach (var proposal in await BuildAsync(events))
            {
                Assert.Contains(proposal.Scope, (IReadOnlySet<string>)SpecEditScopes.All);
            }
        }
    }

    /// <summary>
    /// The one edit this server must never propose: nothing it emits may weaken an assertion. Asserted
    /// over every fragment the builder can produce, by shape rather than by fixture.
    /// </summary>
    [Fact]
    public async Task NoSuggestedEdit_EverCarriesAnAssertionShapedKey()
    {
        string[] forbidden = ["expect:", "assert:", "value:", "expected:"];

        foreach (var events in AllFixtures)
        {
            foreach (var proposal in await BuildAsync(events))
            {
                foreach (var key in forbidden)
                {
                    Assert.DoesNotContain(key, proposal.SuggestedEdit, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    /// <summary>Every fragment is YAML advice, never a diff — asserted across the whole corpus.</summary>
    [Fact]
    public async Task NoSuggestedEdit_IsEverAUnifiedDiff()
    {
        foreach (var events in AllFixtures)
        {
            foreach (var proposal in await BuildAsync(events))
            {
                Assert.DoesNotContain("--- a/", proposal.SuggestedEdit, StringComparison.Ordinal);
                Assert.DoesNotContain("+++ b/", proposal.SuggestedEdit, StringComparison.Ordinal);
                Assert.DoesNotContain("@@", proposal.SuggestedEdit, StringComparison.Ordinal);
                Assert.StartsWith("# Review-only suggestion", proposal.SuggestedEdit, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Secret hygiene (plan §2.7 invariant 4): a <c>${secret:…}</c> reference the engine relayed into
    /// its own text is never resolved, and the proposal surface never invents one.
    /// </summary>
    [Fact]
    public async Task ASecretReferenceInEngineText_IsNeverResolvedIntoAProposal()
    {
        const string sentinelName = "VOUCHFX_MCP_PROPOSAL_SENTINEL_NEVER_RESOLVED";
        const string sentinelValue = "s3ntinel-resolved-secret-9d41fe07";
        var events =
            """{"type":"environment-error","errorKind":"Seed","resourceName":"orders-db","detail":"auth failed for ${secret:env/""" +
            sentinelName +
            """}"}""" + "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}""";

        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var proposal = Assert.Single(await BuildAsync(events));

            Assert.DoesNotContain(sentinelValue, proposal.Rationale, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelValue, proposal.SuggestedEdit, StringComparison.Ordinal);

            // The engine's own already-redacted text is relayed verbatim into the rationale, which is
            // where the hint put it — relaying is correct, resolving would be the violation.
            Assert.Contains("${secret:env/" + sentinelName + "}", proposal.Rationale, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task APassingRun_YieldsNoProposals()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        Assert.Empty(await BuildAsync(events));
    }

    // ── Adversarial identifiers, bounds, and the sentinel ──────────────────────────────────────

    /// <summary>
    /// The no-assertion-key sweep, extended to RENDERED fragments built from adversarial
    /// identifiers: a step literally named <c>expected</c> or <c>value</c> must not make a fragment
    /// appear to set an assertion key.
    /// </summary>
    /// <remarks>
    /// A security review's point: capping an identifier bounds its LENGTH, not its content, so the
    /// corpus sweep over benign fixtures could not see this. The property that saves it is
    /// structural — every identifier lands in a VALUE position or as a step id, never as a key —
    /// and this test is what pins it.
    /// </remarks>
    [Theory]
    [InlineData("expected")]
    [InlineData("value")]
    [InlineData("expect")]
    [InlineData("assert")]
    public void AnAdversariallyNamedStep_NeverRendersAnAssertionShapedKey(string hostileStepId)
    {
        foreach (var kind in new[] { VerdictReasonKinds.Timeout, VerdictReasonKinds.CaptureUnmet })
        {
            var step = new StepDiagnosis(
                hostileStepId,
                nameof(RunVerdict.Inconclusive),
                DurationMs: 10,
                AttemptCount: 1,
                Observation: null,
                Attempts: [],
                OmittedAttemptCount: 0,
                Reason: new VerdictReason(kind, "a hint") { Evidence = new VerdictEvidence(ObservedValues: true) });

            foreach (var proposal in SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step)))
            {
                // The identifier appears, but never followed by a colon at the start of a line —
                // i.e. never in KEY position, which is the only place it could weaken an assertion.
                Assert.DoesNotContain(
                    $"\n{hostileStepId}:",
                    proposal.SuggestedEdit,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotMatch($@"(?m)^\s*{Regex.Escape(hostileStepId)}\s*:", proposal.SuggestedEdit);
            }
        }
    }

    /// <summary>
    /// An identifier arriving at the parser's 2,000-character cap is bounded HERE, to
    /// <see cref="VerdictReasonClassifier.MaxValueChars"/>, before it reaches a fragment — twice per
    /// fragment, which is what made the unbounded version a ~42&#160;KB response hazard.
    /// </summary>
    [Fact]
    public void AnOversizedStepId_IsBoundedBeforeItReachesAFragment()
    {
        var hugeStepId = new string('s', 2_000);
        var step = new StepDiagnosis(
            hugeStepId,
            nameof(RunVerdict.Inconclusive),
            DurationMs: 10,
            AttemptCount: 1,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 0,
            Reason: new VerdictReason(VerdictReasonKinds.CaptureUnmet, "a hint"));

        var proposal = Assert.Single(SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step)));

        Assert.DoesNotContain(hugeStepId, proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains(new string('s', VerdictReasonClassifier.MaxValueChars - 1) + '…', proposal.SuggestedEdit, StringComparison.Ordinal);

        // Bounds the FRAGMENT only. The proposal's own StepId stays raw by design (it is the host's
        // correlation key — see SpecEditProposal.StepId), so this narrows the worst case rather than
        // closing it: ten proposals can still carry ~20 KB on that field, which US-S4-04's shrink
        // ladder is the place to absorb.
        Assert.True(
            proposal.SuggestedEdit.Length < 1_000,
            $"A single fragment measured {proposal.SuggestedEdit.Length} characters; ten of these must not dominate the budget.");
        Assert.Equal(hugeStepId, proposal.StepId);
    }

    /// <summary>
    /// The parser's <c>(unknown)</c> sentinel is prose, not an identifier — it must never land as a
    /// YAML key, because a fragment carrying one is advice that cannot be pasted.
    /// </summary>
    [Fact]
    public async Task AnUnnamedResource_YieldsAPlaceholderKeyAndSaysTheEngineDidNotNameIt()
    {
        const string events = """
            {"type":"environment-error","errorKind":"Seed","detail":"relation orders does not exist"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.DoesNotContain("(unknown)", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("'<resource-name>':", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("did not name the resource", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The image slot's fallback path: a <c>pull</c> error whose detail names no image falls back to
    /// the RESOURCE name — so a secret-shaped resource name reaches a fragment. It is relayed
    /// verbatim (the engine's own already-redacted text) and never resolved.
    /// </summary>
    [Fact]
    public async Task ASecretShapedResourceNameReachingTheImageSlot_IsRelayedVerbatimAndNeverResolved()
    {
        const string sentinelName = "VOUCHFX_MCP_IMAGE_SLOT_SENTINEL_NEVER_RESOLVED";
        const string sentinelValue = "s3ntinel-resolved-secret-77ab30c1";
        var events =
            """{"type":"environment-error","errorKind":"ImagePull","resourceName":"${secret:env/""" +
            sentinelName +
            """}","detail":"pull access denied"}""" + "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}""";

        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var proposal = Assert.Single(await BuildAsync(events));

            Assert.DoesNotContain(sentinelValue, proposal.SuggestedEdit, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelValue, proposal.Rationale, StringComparison.Ordinal);
            Assert.Contains("${secret:env/" + sentinelName + "}", proposal.SuggestedEdit, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    // ── MaxProposals boundary (S6) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The step loop stops at <see cref="SpecEditProposalBuilder.MaxProposals"/>, and the INNER
    /// guard genuinely fires: a step's paired <c>match</c> proposal is cut off while its
    /// <c>timeouts</c> proposal is kept, leaving the list at exactly the cap with an ODD number of
    /// entries from paired steps.
    /// </summary>
    /// <remarks>
    /// <b>The leading UNPAIRED step is load-bearing (a review finding).</b> With only paired steps
    /// the loop enters at counts 0, 2, 4, 6, 8 — always even, so the inner
    /// <c>proposals.Count &lt; MaxProposals</c> guard is never evaluated false and the test proved
    /// nothing about it. One unpaired step first shifts every subsequent entry odd (1, 3, 5, 7, 9),
    /// so the fifth paired step — <c>step-4</c> — enters at 9, adds its timeouts proposal to reach
    /// the cap, and has its match proposal refused by that guard. Starvation-by-arrival-order is
    /// convention-consistent with
    /// <c>FailProposalBuilder</c>'s own first-come cap; it is pinned here so it stays a known
    /// behaviour rather than a discovery.
    /// </remarks>
    [Fact]
    public void WhenTheCapBites_ItIsTheStepProposalsThatAreCutAndTheCountSaysHowMany()
    {
        var steps = Enumerable.Range(0, 8).Select(i => TimeoutStep($"step-{i}", observedValues: true)).ToList();

        var (proposals, omitted) = SpecEditProposalBuilder.BuildProposalsWithOmissions(DiagnosisWith(steps, []));

        // Eight paired steps would be sixteen proposals; the cap holds it to ten...
        Assert.Equal(SpecEditProposalBuilder.MaxProposals, proposals.Count);
        Assert.All(proposals, p => Assert.Contains(p.Scope, (IReadOnlySet<string>)SpecEditScopes.All));

        // ...and the bound is VISIBLE rather than silent: six were declined.
        Assert.Equal(6, omitted);

        // The truncation falls at the end of the list, so the LATER steps are the ones cut — first
        // come, as documented, and the same convention FailProposalBuilder has always used.
        Assert.Contains(proposals, p => p.StepId == "step-0");
        Assert.DoesNotContain(proposals, p => p.StepId == "step-7");
    }

    private static StepDiagnosis TimeoutStep(string stepId, bool observedValues) =>
        new(
            stepId,
            nameof(RunVerdict.Inconclusive),
            DurationMs: 10,
            AttemptCount: 2,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 0,
            Reason: new VerdictReason(VerdictReasonKinds.Timeout, $"hint for {stepId}")
            {
                Evidence = new VerdictEvidence(ObservedValues: observedValues),
            });

    /// <summary>
    /// At the exact odd parity where the cap would split a step's timeouts/match pair, the whole
    /// STEP is dropped instead — and both of its proposals are counted as omitted.
    /// </summary>
    /// <remarks>
    /// <b>This is the shape a review measured going wrong.</b> With a plain <c>Take(10)</c>, one
    /// environment proposal plus five paired steps left the fifth step holding its <c>timeouts</c>
    /// proposal — "raise the timeout, switch to RETRY" — while the sibling that said values WERE
    /// observed, so raising the timeout alone is unlikely to help, was the one cut. The surviving
    /// half actively misleads; half the advice is worse than none. Nine returned and two omitted is
    /// the correct answer here, not ten and one.
    /// </remarks>
    [Fact]
    public void WhenTheCapWouldSplitAStepsPair_TheWholeStepIsDroppedAndBothCount()
    {
        var errors = new List<EnvironmentErrorDiagnosis>
        {
            new(
                "Seed",
                "orders-db",
                "relation orders does not exist",
                new VerdictReason(VerdictReasonKinds.Seed, "Seeding failed on orders-db.")),
        };

        // Five paired steps: 1 + (5 x 2) = 11 proposals against a cap of 10.
        var steps = Enumerable.Range(0, 5).Select(i => TimeoutStep($"poll-{i}", observedValues: true)).ToList();

        var (proposals, omitted) = SpecEditProposalBuilder.BuildProposalsWithOmissions(
            DiagnosisWith(steps, errors));

        Assert.Equal(9, proposals.Count);
        Assert.Equal(2, omitted);

        // The dropped step is gone ENTIRELY — neither half of its pair survived.
        Assert.DoesNotContain(proposals, p => p.StepId == "poll-4");

        // ...and every step that IS present carries its whole pair.
        foreach (var stepId in proposals.Where(p => p.StepId is not null).Select(p => p.StepId!).Distinct())
        {
            var scopes = proposals.Where(p => p.StepId == stepId).Select(p => p.Scope).ToList();
            Assert.Equal([SpecEditScopes.Timeouts, SpecEditScopes.Match], scopes);
        }
    }

    /// <summary>
    /// ROOT CAUSE FIRST: environment-error proposals are built before step proposals, so a cascade
    /// of step proposals can never starve the one that names the actual cause.
    /// </summary>
    /// <remarks>
    /// <b>This test asserted the opposite until a peer review flipped the ordering.</b> It used to
    /// read "the steps filled the cap, so no environment proposal got in — first come, as
    /// documented", which described the behaviour honestly and was exactly the defect: one broken
    /// image cascades into a dozen timed-out steps, whose two-proposal fan-out filled the cap and
    /// silently dropped the image proposal — the only one a reader actually needed. Environment
    /// records are the root-cause surface AND are bounded by the diagnosis tier's own item cap, so
    /// putting them first cannot starve the steps to the same degree in return.
    /// </remarks>
    [Fact]
    public void EnvironmentErrorProposals_ComeFirst_AndAreNeverStarvedByAStepCascade()
    {
        var steps = Enumerable.Range(0, 10)
            .Select(i => new StepDiagnosis(
                $"step-{i}",
                nameof(RunVerdict.Inconclusive),
                DurationMs: 10,
                AttemptCount: 1,
                Observation: null,
                Attempts: [],
                OmittedAttemptCount: 0,
                Reason: new VerdictReason(VerdictReasonKinds.Timeout, $"hint {i}")))
            .ToList();

        var errors = Enumerable.Range(0, 5)
            .Select(i => new EnvironmentErrorDiagnosis(
                "Seed",
                $"orders-db-{i}",
                "relation orders does not exist",
                new VerdictReason(VerdictReasonKinds.Seed, $"Seeding failed on orders-db-{i}.")))
            .ToList();

        var (proposals, omitted) = SpecEditProposalBuilder.BuildProposalsWithOmissions(
            DiagnosisWith(steps, errors));

        Assert.Equal(SpecEditProposalBuilder.MaxProposals, proposals.Count);

        // Every environment proposal survived — five of them, and they are the first five entries.
        Assert.Equal(5, proposals.Count(p => p.Scope == SpecEditScopes.Environment));
        Assert.All(proposals.Take(5), p => Assert.Equal(SpecEditScopes.Environment, p.Scope));

        // The step cascade is what got cut, and the count says by how much: ten steps would have
        // produced ten timeouts proposals, five fit alongside the environment ones.
        Assert.Equal(5, proposals.Count(p => p.Scope == SpecEditScopes.Timeouts));
        Assert.Equal(5, omitted);
    }

    // ── YAML quoting: a spliced identifier round-trips when the fragment is applied ─────────────

    /// <summary>
    /// Every identifier slot in every fragment is a YAML SINGLE-QUOTED scalar, so an id carrying
    /// characters that are syntax in YAML survives being pasted into a suite.
    /// </summary>
    /// <remarks>
    /// The three shapes below are the ones a peer review named, and each breaks differently when
    /// spliced bare: <c>" #"</c> turns the rest of the line into a comment (silently, which is the
    /// worst of the three), <c>": "</c> turns a scalar into a nested mapping, and a leading <c>-</c>
    /// reads as a sequence entry. All are legal step ids as far as this server is concerned — it
    /// relays what the engine reported.
    /// </remarks>
    [Theory]
    [InlineData("poll order # not-a-comment")]
    [InlineData("poll: order")]
    [InlineData("-poll-order")]
    [InlineData("poll'order")]
    public void AnIdentifierCarryingYamlSyntax_IsQuotedInEveryFragmentSlot(string hostileStepId)
    {
        foreach (var kind in new[] { VerdictReasonKinds.Timeout, VerdictReasonKinds.CaptureUnmet })
        {
            var step = new StepDiagnosis(
                hostileStepId,
                nameof(RunVerdict.Inconclusive),
                DurationMs: 10,
                AttemptCount: 1,
                Observation: null,
                Attempts: [],
                OmittedAttemptCount: 0,
                // The capture kind gets the SAME hostile text as its capture NAME, so the `{2}` key
                // slot is exercised by these four shapes too — with ObservedValues alone (as an
                // earlier version had it) CaptureName stayed null and that slot always rendered its
                // placeholder, leaving the one KEY-position splice in the builder uncovered here (a
                // security review's finding).
                Reason: new VerdictReason(kind, "a hint")
                {
                    Evidence = kind == VerdictReasonKinds.CaptureUnmet
                        ? new VerdictEvidence(CaptureName: hostileStepId)
                        : new VerdictEvidence(ObservedValues: true),
                });

            foreach (var proposal in SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step)))
            {
                // The YAML slot carries the quoted form, with any embedded quote doubled.
                var quoted = "'" + hostileStepId.Replace("'", "''", StringComparison.Ordinal) + "'";
                Assert.Contains($"- id: {quoted}", proposal.SuggestedEdit, StringComparison.Ordinal);

                // ...and the id never appears BARE in a value position, which is what would break.
                Assert.DoesNotContain($"- id: {hostileStepId}\n", proposal.SuggestedEdit, StringComparison.Ordinal);

                if (kind != VerdictReasonKinds.CaptureUnmet)
                {
                    continue;
                }

                // KEY position, same rule: quoted, doubled, and on one line with its extractor path.
                Assert.Contains(
                    $"{quoted}: \"$.<path.to.the.value>\"",
                    proposal.SuggestedEdit,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"\n      {hostileStepId}:",
                    proposal.SuggestedEdit,
                    StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The CAPTURE-NAME slot is a YAML KEY fed from an OBSERVATION, and it is quoted like every
    /// other identifier slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A distinct adversarial surface from the step-id theory above, which is why it is its own
    /// test</b> (a code review's point). Every other value this builder splices is a LABEL the engine
    /// minted — a step id, a resource name, an image reference. The capture name (vouchfx-mcp#86)
    /// comes from an <c>observation</c>, and it lands in KEY position, where the failure modes are
    /// worse than in a value: <c>": "</c> turns one key into a nested mapping, <c>" #"</c> comments
    /// away the rest of the line INCLUDING the extractor path, and an apostrophe would terminate the
    /// scalar early.
    /// </para>
    /// <para>
    /// <c>expect</c> is in the list for a different reason: it is the one name that, spliced BARE,
    /// would render an assertion-shaped key into a fragment — the single edit this server must never
    /// propose. Quoting is what keeps it a capture variable called "expect" rather than an
    /// <c>expect:</c> block, and the last assertion checks that directly rather than trusting the
    /// corpus sweep to have covered this shape.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("cap: ture")]
    [InlineData("cap # ture")]
    [InlineData("cap'ture")]
    [InlineData("-capture")]
    [InlineData("expect")]
    public void AHostileCaptureName_IsQuotedInTheCaptureKeySlot(string hostileCaptureName)
    {
        var step = new StepDiagnosis(
            "seed-order",
            nameof(RunVerdict.Inconclusive),
            DurationMs: 10,
            AttemptCount: 1,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 0,
            Reason: new VerdictReason(VerdictReasonKinds.CaptureUnmet, "a hint")
            {
                Evidence = new VerdictEvidence(CaptureName: hostileCaptureName),
            });

        var proposal = Assert.Single(SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step)));

        // Single-quoted, with any embedded quote doubled — the one escape a YAML single-quoted
        // scalar has — and the key and its extractor path stay on ONE line.
        var quoted = "'" + hostileCaptureName.Replace("'", "''", StringComparison.Ordinal) + "'";
        Assert.Contains(
            $"{quoted}: \"$.<path.to.the.value>\"",
            proposal.SuggestedEdit,
            StringComparison.Ordinal);

        // ...and never bare in key position, which is what would break on paste.
        Assert.DoesNotContain(
            $"\n      {hostileCaptureName}:",
            proposal.SuggestedEdit,
            StringComparison.Ordinal);

        // The assertion-shaped-key rule holds for this slot too: 'expect' is a quoted capture
        // variable, never an `expect:` block.
        Assert.DoesNotContain("\nexpect:", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain(" expect:", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prose slots stay UNQUOTED: a <c>#</c> comment cannot have its meaning changed by its
    /// content, and "Step 'poll-01' ran out of time" is what a human wants to read.
    /// </summary>
    [Fact]
    public async Task TheCommentProse_NamesTheIdentifierWithoutTheYamlQuoting()
    {
        const string events = """
            {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":50,"observation":{"expected":"orderId","got":null}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        // "has a capture that matched nothing", not "captured nothing": the measured shape has the
        // step capturing one value successfully while a second capture goes unmet, so the older
        // wording was false of exactly the case this fragment is for (a peer-review finding).
        Assert.Contains(
            "# Step 'seed-order' has a capture that matched nothing",
            proposal.SuggestedEdit,
            StringComparison.Ordinal);
        Assert.Contains("- id: 'seed-order'", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    // ── The health fragment names both possible targets ─────────────────────────────────────────

    /// <summary>
    /// The health fragment emits a <c>dependencies</c> target but its prose names the SERVICE
    /// possibility too — the achievable half of a fix whose real resolution waits on upstream ask
    /// U1.
    /// </summary>
    /// <remarks>
    /// An <c>environment-error</c> record carries a resource NAME and nothing that says which block
    /// declared it, while the composed schema's only <c>healthCheck</c> key lives under
    /// <c>$defs/service</c> — so for a service-shaped failure the emitted YAML names the wrong
    /// section. It cannot be resolved from the event stream; what it CAN stop doing is pointing
    /// silently.
    /// </remarks>
    [Fact]
    public async Task TheHealthFragment_NamesTheServiceAlternativeRatherThanPointingSilently()
    {
        const string events = """
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"orders-db","detail":"health gate timed out after 30000ms"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var proposal = Assert.Single(await BuildAsync(events));

        Assert.Contains("dependencies:", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("is a SERVICE rather than a managed dependency", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("healthCheck under environment.services", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("this run's events do not say which", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    // ── Bounded composition, without a second cap (peer-review finding) ─────────────────────────

    /// <summary>
    /// A rationale built from an ALREADY-TRUNCATED hint carries exactly one truncation marker — the
    /// hint's own — never a stacked pair.
    /// </summary>
    /// <remarks>
    /// The defect this pins: capping a rationale that already contains a capped hint would cut it
    /// again and append a SECOND marker, yielding "……" — nonsense to a reader and a sign of two
    /// bounds fighting. The builder now caps nothing: a hint is bounded by its own type, the suffix
    /// is a fixed literal, so the sum is bounded by construction.
    /// </remarks>
    [Fact]
    public void ARationaleBuiltFromATruncatedHint_CarriesExactlyOneTruncationMarker()
    {
        // Normalised by VerdictReason itself to 299 characters plus the marker.
        var truncatedHint = new VerdictReason(VerdictReasonKinds.Timeout, new string('x', 5_000)).Hint;
        Assert.EndsWith("…", truncatedHint, StringComparison.Ordinal);

        var step = new StepDiagnosis(
            "expect-order-event",
            nameof(RunVerdict.Inconclusive),
            DurationMs: 1300,
            AttemptCount: 6,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 6,
            Reason: new VerdictReason(VerdictReasonKinds.Timeout, truncatedHint)
            {
                // Both proposals, so the SUFFIXED rationale is exercised too — that is the one a
                // second cap would have re-cut into a stacked marker.
                Evidence = new VerdictEvidence(ObservedValues: true),
            });

        var proposals = SpecEditProposalBuilder.BuildProposals(DiagnosisWith(step));

        foreach (var proposal in proposals)
        {
            Assert.DoesNotContain("……", proposal.Rationale, StringComparison.Ordinal);
            Assert.Equal(1, proposal.Rationale.Count(c => c == '…'));
        }

        // The timeouts rationale IS the hint, relayed whole — no second cut, so the hint's own single
        // marker is the only one.
        Assert.Equal(truncatedHint, proposals[0].Rationale);

        // The match rationale appends a suffix AFTER the marker, which is the case that would have
        // produced "……" had the composition been re-capped.
        Assert.StartsWith(truncatedHint, proposals[1].Rationale, StringComparison.Ordinal);
        Assert.EndsWith("unlikely to help.", proposals[1].Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ceiling <see cref="SpecEditProposalBuilder.MaxRationaleChars"/> documents is MEASURED
    /// here, on the worst case (a maximal 300-character hint plus the longest suffix), rather than
    /// trusted from the arithmetic in its remarks.
    /// </summary>
    [Fact]
    public void EveryRationale_StaysWithinItsProvenCeilingWithoutBeingRecapped()
    {
        var maximalHint = new VerdictReason(VerdictReasonKinds.Timeout, new string('x', 5_000)).Hint;
        Assert.Equal(VerdictReasonClassifier.MaxHintChars, maximalHint.Length);

        var worstCase = new StepDiagnosis(
            "expect-order-event",
            nameof(RunVerdict.Inconclusive),
            DurationMs: 1300,
            AttemptCount: 6,
            Observation: null,
            Attempts: [],
            OmittedAttemptCount: 6,
            Reason: new VerdictReason(VerdictReasonKinds.Timeout, maximalHint)
            {
                // The MATCH rationale is the longest the builder can produce (hint + the longest
                // suffix), so the worst case needs both proposals.
                Evidence = new VerdictEvidence(ObservedValues: true),
            });

        var measured = SpecEditProposalBuilder.BuildProposals(DiagnosisWith(worstCase))
            .Max(p => p.Rationale.Length);

        // The EXACT figure, pinned rather than computed by hand in a comment: an earlier version of
        // the ceiling's remarks stated an arithmetic sum that was already wrong by five characters.
        Assert.Equal(396, measured);
        Assert.True(
            measured <= SpecEditProposalBuilder.MaxRationaleChars,
            $"Worst-case rationale measured {measured} characters against the "
            + $"{SpecEditProposalBuilder.MaxRationaleChars}-character documented ceiling.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every events fixture this class drives the builder with — the corpus the sweeps above enumerate.</summary>
    internal static readonly string[] AllFixtures =
    [
        """
        {"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"health gate timed out after 30000ms"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """,
        """
        {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"manifest for ghcr.io/acme/orders-api:latest not found"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """,
        """
        {"type":"environment-error","errorKind":"Seed","resourceName":"orders-db","detail":"relation orders does not exist"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """,
        """
        {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false,"seen":"order_id"}}
        {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300,"observation":{"reason":"retry-timeout","attempts":1}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """,
        """
        {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """,
        """
        {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":50,"observation":{"expected":"orderId","got":null}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """,
        """
        {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"reason":"partition grace period exceeded for topic orders"}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """,
        """
        {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
        """,

        // The engine's own capture-unmet shape (vouchfx-mcp#86) — in the corpus so every sweep above
        // (scope vocabulary, no assertion-shaped key, never a diff, no secret resolution) covers the
        // ONE fragment slot this story made engine-fed. That the SECRET sweep reaches that slot
        // specifically — rather than settling for this row's step id — rests on `captureUnmet` being
        // in HealerScopeRegressionTests' injector field list, and is measured by
        // TheCaptureKeySlot_IsFedByTheCaptureUnmetField_WhichIsWhatTheSecretSweepInjectsInto.
        EngineCaptureUnmetEvents,
    ];

    /// <summary>
    /// The measured engine capture-unmet events (see
    /// <c>VerdictReasonClassifierTests.EngineCaptureUnmetFixture</c> for the provenance) trimmed to
    /// the two lines this class needs.
    /// </summary>
    private const string EngineCaptureUnmetEvents = """
        {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-15T17:30:14.7235166+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","stepId":"capture-present-and-missing","verdict":"INCONCLUSIVE","durationMs":13,"captured":[{"name":"hostname","path":"$.hostname","matched":true},{"name":"missing","path":"$.doesNotExist","matched":false}],"observation":{"captureUnmet":"missing"}}
        {"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-09-15T17:30:14.8009995+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","scenarioId":"issue-86 probe: capture whose JSONPath matches nothing","verdict":"INCONCLUSIVE","counts":{"pass":3,"fail":0,"envError":0,"inconclusive":1}}
        """;

    private static async Task<IReadOnlyList<SpecEditProposal>> BuildAsync(string events) =>
        SpecEditProposalBuilder.BuildProposals(await DiagnoseAsync(events));

    private static async Task<Vouchfx.Mcp.Diagnosis.Diagnosis> DiagnoseAsync(string events)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spec-edit-proposal-test-{Guid.NewGuid():N}.jsonl");
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

    /// <summary>A minimal diagnosis carrying the given steps and environment errors.</summary>
    private static Vouchfx.Mcp.Diagnosis.Diagnosis DiagnosisWith(
        IReadOnlyList<StepDiagnosis> steps,
        IReadOnlyList<EnvironmentErrorDiagnosis> environmentErrors) =>
        new(
            Verdict: nameof(RunVerdict.Inconclusive),
            CategoryMeaning: "(test)",
            Summary: "(test)",
            TotalStepCount: steps.Count,
            PassedStepCount: 0,
            NotableSteps: steps,
            OmittedNotableStepCount: 0,
            EnvironmentErrors: environmentErrors,
            OmittedEnvironmentErrorCount: 0,
            EventsFilePath: "(test)",
            EventsTruncated: false,
            ResponseTruncated: false,
            ClassificationHints: []);

    /// <summary>A minimal diagnosis carrying one hand-built step — for the shapes no events file can produce cheaply.</summary>
    private static Vouchfx.Mcp.Diagnosis.Diagnosis DiagnosisWith(StepDiagnosis step) =>
        new(
            Verdict: nameof(RunVerdict.Inconclusive),
            CategoryMeaning: "(test)",
            Summary: "(test)",
            TotalStepCount: 1,
            PassedStepCount: 0,
            NotableSteps: [step],
            OmittedNotableStepCount: 0,
            EnvironmentErrors: [],
            OmittedEnvironmentErrorCount: 0,
            EventsFilePath: "(test)",
            EventsTruncated: false,
            ResponseTruncated: false,
            ClassificationHints: step.Reason is null ? [] : [step.Reason.Hint]);
}
