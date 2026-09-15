using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Diagnosis;

/// <summary>
/// Covers <see cref="VerdictReasonClassifier"/> — US-S4-01's <c>reason.kind</c> rule table — driven
/// by ONE event-stream fixture per kind, parsed with the PRODUCTION
/// <see cref="SuiteEventParser"/> and classified directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fixture mechanism: inline JSON Lines string literals handed to
/// <see cref="SuiteEventParser.Parse(string, Action{string}?)"/>, exactly as
/// <c>Run/SuiteEventParserTests</c> already does</b> — deliberately NOT
/// <c>ExplainRunOrchestratorTests</c>'s temp-file variant. The classifier is a pure function of
/// already-parsed material (US-S4-01's last acceptance criterion: "no new I/O, no new engine event
/// type parsed, no CLI spawn"), so a test that wrote a file and resolved a path would exercise
/// <see cref="ExplainRunOrchestrator"/>'s plumbing rather than this rule table. Going through the
/// real parser rather than hand-constructing <see cref="StepOutcome"/>/
/// <see cref="EnvironmentErrorSummary"/> records is what makes these fixtures EVENT-STREAM fixtures
/// as the story's test convention requires: the observation text the rules key on is then the same
/// sanitised, capped raw JSON the parser really produces, not a string a test invented.
/// </para>
/// <para>
/// <b>Every fixture is registered in <see cref="Corpus"/></b>, which is what the "compile is never
/// assigned" and "every emitted kind is in the vocabulary" sweeps enumerate — a new fixture added
/// without registering it there would silently escape both sweeps, so the corpus is itself asserted
/// to cover every kind the sprint ships.
/// </para>
/// </remarks>
public class VerdictReasonClassifierTests
{
    // ── Fixtures: exactly one event stream per reason.kind ───────────────────────────────────────

    /// <summary>Gherkin 1: an <c>ImagePull</c> environment error whose detail names the image.</summary>
    private const string PullFixture = """
        {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"manifest for ghcr.io/acme/orders-api:latest not found"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """;

    /// <summary>A health-gate environment error whose detail carries the configured timeout.</summary>
    private const string UnhealthyFixture = """
        {"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"health gate timed out after 30000ms"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """;

    /// <summary>A seed-stage environment error naming the seed target and the underlying error.</summary>
    private const string SeedFixture = """
        {"type":"environment-error","errorKind":"Seed","resourceName":"orders-db","detail":"relation 'orders' does not exist"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """;

    /// <summary>
    /// Gherkin 2: RETRY exhausted with a non-empty observation on EVERY attempt. The step's own
    /// observation is the shape a real engine writes for an exhausted RETRY
    /// (<c>{"reason":"retry-timeout","attempts":N}</c>, measured in
    /// <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>) — it carries no expected/observed pair
    /// and no partition signal, so the step falls through to the timeout rule as intended.
    /// </summary>
    private const string TimeoutObservedFixture = """
        {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false,"key":"orderId","seen":"order_id"}}
        {"type":"step-attempt","stepId":"expect-order-event","attempt":2,"tMs":300,"outcome":"FAIL","observation":{"matched":false,"key":"orderId","seen":"order_id"}}
        {"type":"step-attempt","stepId":"expect-order-event","attempt":3,"tMs":900,"outcome":"FAIL","observation":{"matched":false,"key":"orderId","seen":"order_id"}}
        {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300,"observation":{"reason":"retry-timeout","attempts":3}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """;

    /// <summary>Gherkin 3: the same exhausted RETRY, with no observation on any attempt at all.</summary>
    private const string TimeoutUnobservedFixture = """
        {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL"}
        {"type":"step-attempt","stepId":"expect-order-event","attempt":2,"tMs":300,"outcome":"FAIL"}
        {"type":"step-attempt","stepId":"expect-order-event","attempt":3,"tMs":900,"outcome":"FAIL"}
        {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300,"observation":{"reason":"retry-timeout","attempts":3}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """;

    /// <summary>
    /// The repo's own capture-unmet SHAPE — an expected name paired with an observed value of
    /// literal <c>null</c> — on a step that did NOT poll.
    /// </summary>
    /// <remarks>
    /// <b>Provenance, corrected after a code review.</b> The shape is taken from
    /// <c>Run/GetStepTimelineOrchestratorTests.cs:218</c> and
    /// <c>RealGetStepTimelineMcpTests.cs:100</c>, but in BOTH of those it depicts an ordinary
    /// mid-RETRY poll miss — the first is attempt 3 of a poll that PASSES on attempt 4 — NOT a
    /// capture that resolved to nothing. An earlier version of this comment claimed those fixtures
    /// supported the shape as capture-unmet evidence, which they do not. The rule therefore requires
    /// the step not to have polled as well, and this fixture records exactly one attempt to sit on
    /// the permitted side of that gate;
    /// <see cref="AStepThatDemonstrablyPolled_FallsThroughToTimeout_EvenCarryingTheCaptureUnmetShape"/>
    /// pins the other side.
    /// </remarks>
    private const string CaptureUnmetFixture = """
        {"type":"step-attempt","stepId":"seed-order","attempt":1,"tMs":50,"outcome":"FAIL","observation":{"expected":"orderId","got":null}}
        {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":50,"observation":{"expected":"orderId","got":null}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """;

    /// <summary>
    /// The ENGINE's own capture-unmet shape, MEASURED — <c>observation: {"captureUnmet":"&lt;name&gt;"}</c>
    /// on a step whose <c>expect</c> HELD and whose other capture matched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Copied verbatim from a real run</b> (2026-09-15, maintainer host, pinned engine
    /// v1.0.0-rc.5, Docker 29.6.1): an <c>http.rest</c> step against a <c>traefik/whoami</c> service
    /// declaring two captures — <c>hostname: "$.hostname"</c> (matches) and
    /// <c>missing: "$.doesNotExist"</c> (matches nothing) — with <c>expect: {status: 200}</c> that
    /// held. The engine made THAT step <c>INCONCLUSIVE</c> (never Fail, never Pass), ran every later
    /// step anyway, substituted the unmet placeholder in them, and resolved the scenario
    /// <c>INCONCLUSIVE</c> (<c>pass=3 fail=0 envError=0 inconclusive=1</c>, <c>vouchfx run</c> exit
    /// 0). Its own terminology for this is "upstream capture unmet" (§12.1).
    /// </para>
    /// <para>
    /// <b>This is the shape vouchfx-mcp#86 exists for.</b> Before it, the rule table keyed capture
    /// unmet ONLY on <see cref="CaptureUnmetFixture"/>'s expected/observed-null shape — never
    /// measured against the engine — so this observation found no <c>expected</c> key, fell through
    /// the partition rule, and classified as <c>timeout</c> ("No values observed at all; the producer
    /// path…") on a step that did not time out. That also pushed <c>SpecEditProposalBuilder</c>
    /// towards <c>timeouts</c>/<c>match</c> proposals instead of the capture one.
    /// </para>
    /// <para>
    /// The lines are the run's own <c>step-completed</c>/<c>scenario-completed</c> records, unedited
    /// — including the <c>captured[]</c> array and the <c>substitutions[]</c> provenance this parser
    /// does not read, which is exactly why they are kept: a future story that starts reading them
    /// has the measured shape here rather than a fixture someone invented.
    /// </para>
    /// </remarks>
    private const string EngineCaptureUnmetFixture = """
        {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-15T17:30:14.7235166+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","stepId":"capture-present-and-missing","verdict":"INCONCLUSIVE","durationMs":13,"captured":[{"name":"hostname","path":"$.hostname","matched":true},{"name":"missing","path":"$.doesNotExist","matched":false}],"observation":{"captureUnmet":"missing"}}
        {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-15T17:30:14.7235166+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","stepId":"consume-missing","verdict":"PASS","durationMs":11,"substitutions":[{"placeholder":"missing","originStepId":"capture-present-and-missing","secretDerived":false}],"observation":{"status":200,"expected":200}}
        {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-15T17:30:14.7235166+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","stepId":"consume-present","verdict":"PASS","durationMs":7,"substitutions":[{"placeholder":"hostname","originStepId":"capture-present-and-missing","secretDerived":false}],"observation":{"status":200,"expected":200}}
        {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-15T17:30:14.7235166+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","stepId":"independent-tail","verdict":"PASS","durationMs":5,"observation":{"status":200,"expected":200}}
        {"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-09-15T17:30:14.8009995+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","scenarioId":"issue-86 probe: capture whose JSONPath matches nothing","verdict":"INCONCLUSIVE","counts":{"pass":3,"fail":0,"envError":0,"inconclusive":1}}
        """;

    /// <summary>The hint the engine shape produces, snapshot-tested character for character like every other hint.</summary>
    private const string EngineCaptureUnmetHint =
        "Step capture-present-and-missing declared capture missing but its path matched nothing in " +
        "the step's result; check the capture path or the upstream producer.";

    /// <summary>A step whose observation names a partition that outlasted its grace period.</summary>
    private const string PartitionFixture = """
        {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"reason":"partition grace period exceeded for topic orders","topic":"orders"}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
        """;

    /// <summary>Gherkin 4: a Fail step carrying both an expected and an observed value.</summary>
    private const string AssertionFixture = """
        {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
        """;

    /// <summary>Gherkin 5: an <c>errorKind</c> outside this table's recognised set.</summary>
    private const string UnrecognisedKindFixture = """
        {"type":"environment-error","errorKind":"SomeFutureEngineKind","resourceName":"events","detail":"something this build has never heard of"}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """;

    /// <summary>
    /// A step whose OWN verdict is <c>EnvironmentError</c> — the second half of the rule table's
    /// EnvironmentError/Inconclusive branch, which no other fixture reaches (every other
    /// environment-shaped fixture here carries an <c>environment-error</c> EVENT instead, which is a
    /// different surface).
    /// </summary>
    private const string EnvironmentErrorStepFixture = """
        {"type":"step-completed","stepId":"seed-order","verdict":"ENV_ERROR","durationMs":40,"observation":{"expected":"orderId","got":null}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
        """;

    /// <summary>
    /// Invariant 4 (secret hygiene) at the surface this story mints: an observation carrying an
    /// UNRESOLVED <c>${secret:…}</c> reference, which a hint relays exactly as the engine wrote it.
    /// </summary>
    /// <remarks>
    /// The reference is the engine's own already-safe text — the engine is the sole redaction
    /// authority — so relaying it is correct and RESOLVING it would be the violation. Pinned here
    /// rather than deferred to a later story because this story is where the hints are minted; the
    /// sentinel-name/sentinel-value pattern follows <c>RealRunRegistryMcpTests</c>'s.
    /// </remarks>
    private const string SecretReferenceObservationFixture = """
        {"type":"step-completed","stepId":"check-token","verdict":"FAIL","durationMs":30,"observation":{"expected":"${secret:env/VOUCHFX_MCP_CLASSIFIER_SENTINEL_NEVER_RESOLVED}","actual":"[REDACTED]"}}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
        """;

    /// <summary>The environment variable <see cref="SecretReferenceObservationFixture"/>'s reference names — never read by this table.</summary>
    private const string SecretSentinelName = "VOUCHFX_MCP_CLASSIFIER_SENTINEL_NEVER_RESOLVED";

    /// <summary>The value that must never appear in a hint.</summary>
    private const string SecretSentinelValue = "s3ntinel-resolved-secret-4c17be92";

    /// <summary>
    /// Every fixture this story ships, with the kind it is expected to produce (<see langword="null"/>
    /// for the deliberately-unclassified one) — the corpus Gherkin 6's compile sweep enumerates.
    /// </summary>
    internal static readonly (string Name, string Events, string? ExpectedKind)[] Corpus =
    [
        (nameof(PullFixture), PullFixture, VerdictReasonKinds.Pull),
        (nameof(UnhealthyFixture), UnhealthyFixture, VerdictReasonKinds.Unhealthy),
        (nameof(SeedFixture), SeedFixture, VerdictReasonKinds.Seed),
        (nameof(TimeoutObservedFixture), TimeoutObservedFixture, VerdictReasonKinds.Timeout),
        (nameof(TimeoutUnobservedFixture), TimeoutUnobservedFixture, VerdictReasonKinds.Timeout),
        (nameof(CaptureUnmetFixture), CaptureUnmetFixture, VerdictReasonKinds.CaptureUnmet),
        (nameof(EngineCaptureUnmetFixture), EngineCaptureUnmetFixture, VerdictReasonKinds.CaptureUnmet),
        (nameof(PartitionFixture), PartitionFixture, VerdictReasonKinds.Partition),
        (nameof(AssertionFixture), AssertionFixture, VerdictReasonKinds.Assertion),
        (nameof(UnrecognisedKindFixture), UnrecognisedKindFixture, null),
        (nameof(EnvironmentErrorStepFixture), EnvironmentErrorStepFixture, VerdictReasonKinds.CaptureUnmet),
        (nameof(SecretReferenceObservationFixture), SecretReferenceObservationFixture, VerdictReasonKinds.Assertion),
    ];

    // ── Gherkin 1: an image-pull environment error classifies as pull ────────────────────────────

    [Fact]
    public void ImagePullEnvironmentError_ClassifiesAsPull_AndItsHintNamesTheImage()
    {
        var reason = Assert.Single(ClassifyEnvironmentErrors(PullFixture));

        Assert.Equal(VerdictReasonKinds.Pull, reason.Kind);
        Assert.Equal("Image tag likely wrong or registry auth missing: ghcr.io/acme/orders-api:latest", reason.Hint);
    }

    [Fact]
    public void ImagePullEnvironmentErrorWithNoImageInItsDetail_FallsBackToTheResourceName()
    {
        const string events = """{"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"pull access denied"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal(VerdictReasonKinds.Pull, reason.Kind);
        Assert.Equal("Image tag likely wrong or registry auth missing: orders-api", reason.Hint);
    }

    /// <summary>
    /// The AC's second image-pull signature ("or a message containing 'manifest unknown'"): an
    /// otherwise-unrecognised kind whose detail carries the registry's own manifest wording is still
    /// pull. This is an ENUMERATED signature, not a guess — see the classifier's own remarks for why
    /// it does not weaken the fail-closed default the test below pins.
    /// </summary>
    [Fact]
    public void ManifestUnknownDetail_ClassifiesAsPull_EvenWhenTheErrorKindIsNotRecognised()
    {
        const string events = """{"type":"environment-error","errorKind":"Provision","resourceName":"orders-api","detail":"manifest unknown: ghcr.io/acme/orders-api:v9 not found"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal(VerdictReasonKinds.Pull, reason.Kind);
        Assert.Contains("ghcr.io/acme/orders-api:v9", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RECOGNISED kind wins over the message signature: a health-gate failure whose detail happens
    /// to quote a registry error is an unhealthy resource, not a pull failure.
    /// </summary>
    /// <remarks>
    /// A security review found the signature tested in the FIRST branch, so any recognised kind could
    /// be overridden by a substring of caller-influenced detail text — and the classifier's own
    /// docstring already claimed the ordering this test now enforces.
    /// </remarks>
    [Theory]
    [InlineData("HealthGate", VerdictReasonKinds.Unhealthy)]
    [InlineData("Seed", VerdictReasonKinds.Seed)]
    [InlineData("ImagePull", VerdictReasonKinds.Pull)]
    public void ARecognisedErrorKind_OutranksTheManifestUnknownMessageSignature(string errorKind, string expectedKind)
    {
        var events = $$"""{"type":"environment-error","errorKind":"{{errorKind}}","resourceName":"events","detail":"waiting on a container whose last event was manifest unknown"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal(expectedKind, reason.Kind);
    }

    // ── unhealthy ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HealthGateEnvironmentError_ClassifiesAsUnhealthy_AndItsHintNamesResourceAndTimeout()
    {
        var reason = Assert.Single(ClassifyEnvironmentErrors(UnhealthyFixture));

        Assert.Equal(VerdictReasonKinds.Unhealthy, reason.Kind);
        Assert.Equal("Resource events never became healthy within 30000ms; check its logs.", reason.Hint);
    }

    [Theory]
    [InlineData("HealthGate")]
    [InlineData("Unhealthy")]
    [InlineData("WaitFor")]
    public void EveryHealthGateShapedErrorKind_ClassifiesAsUnhealthy(string errorKind)
    {
        var events = $$"""{"type":"environment-error","errorKind":"{{errorKind}}","resourceName":"events","detail":"never came up"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal(VerdictReasonKinds.Unhealthy, reason.Kind);
    }

    [Fact]
    public void UnhealthyWithNoTimeoutInItsDetail_OmitsTheTimeoutClauseRatherThanInventingOne()
    {
        const string events = """{"type":"environment-error","errorKind":"Unhealthy","resourceName":"events","detail":"container is restarting"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal("Resource events never became healthy; check its logs.", reason.Hint);
    }

    // ── seed ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedEnvironmentError_ClassifiesAsSeed_AndItsHintNamesTargetAndError()
    {
        var reason = Assert.Single(ClassifyEnvironmentErrors(SeedFixture));

        Assert.Equal(VerdictReasonKinds.Seed, reason.Kind);
        Assert.Equal("Seeding failed on orders-db: relation 'orders' does not exist.", reason.Hint);
    }

    /// <summary>
    /// A detail ending in a colon has the colon REPLACED by the stop — the deliberate choice among
    /// three bad options (keep it dangling; append after it, giving "refused:."; or drop it).
    /// </summary>
    /// <remarks>
    /// A code review pointed out the earlier "colon is not terminal" change was untested and, taken
    /// literally, produced the worst of the three. A colon is a dangling connective: engine detail
    /// text that ends in one was going to continue and did not.
    /// </remarks>
    [Theory]
    [InlineData("connection refused:", "Seeding failed on orders-db: connection refused.")]
    [InlineData("connection refused", "Seeding failed on orders-db: connection refused.")]
    [InlineData("connection refused.", "Seeding failed on orders-db: connection refused.")]
    [InlineData("connection refused!", "Seeding failed on orders-db: connection refused!")]
    public void ADetailsOwnTerminalPunctuation_DecidesHowTheSeedHintEnds(string detail, string expectedHint)
    {
        var events = $$"""{"type":"environment-error","errorKind":"Seed","resourceName":"orders-db","detail":"{{detail}}"}""";

        Assert.Equal(expectedHint, Assert.Single(ClassifyEnvironmentErrors(events)).Hint);
    }

    [Fact]
    public void SeedEnvironmentErrorWithNoDetail_StillNamesTheTarget()
    {
        const string events = """{"type":"environment-error","errorKind":"Seed","resourceName":"orders-db"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal("Seeding failed on orders-db.", reason.Hint);
    }

    // ── Gherkin 5: an unrecognised errorKind is fail-closed, never guessed ───────────────────────

    [Fact]
    public void UnrecognisedErrorKind_LeavesTheKindNull_ButStillDescribesTheRawKindAndDetail()
    {
        var reason = Assert.Single(ClassifyEnvironmentErrors(UnrecognisedKindFixture));

        Assert.Null(reason.Kind);
        Assert.Equal(
            "Resource events reported SomeFutureEngineKind: something this build has never heard of.",
            reason.Hint);
    }

    /// <summary>
    /// <c>Provision</c> is a REAL kind the engine emits (it appears in this repo's existing
    /// <c>explain_run</c> fixtures) and it is deliberately NOT in the recognised set — the AC names
    /// image-pull, health-gate and seed shapes only. Pinned so that widening the table to it becomes
    /// a deliberate edit rather than an accident.
    /// </summary>
    [Fact]
    public void ProvisionErrorKind_IsDeliberatelyUnrecognised()
    {
        const string events = """{"type":"environment-error","errorKind":"Provision","resourceName":"docker-daemon","detail":"Cannot connect to the Docker daemon"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Null(reason.Kind);
    }

    // ── Gherkin 2 and 3: the two timeout variants ────────────────────────────────────────────────

    [Fact]
    public void InconclusiveStepWithObservedValues_ClassifiesAsTimeout_NonEmptyVariant()
    {
        var reason = SingleClassifiedStep(TimeoutObservedFixture);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.Equal(
            "Observed 3 value(s) but none matched; the match key or capture path is probably wrong.",
            reason.Hint);
    }

    [Fact]
    public void InconclusiveStepWithNoObservedValues_ClassifiesAsTimeout_EmptyVariant()
    {
        var reason = SingleClassifiedStep(TimeoutUnobservedFixture);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.Equal(
            "No values observed at all; the producer path, target name, or serialization is the likely cause.",
            reason.Hint);
    }

    /// <summary>
    /// Both variants share the one <c>kind</c> value — the story's own wording ("two variants sharing
    /// the one kind value but different hint text"), asserted directly so a future split into two
    /// kinds fails here rather than silently widening the union a host branches on.
    /// </summary>
    [Fact]
    public void BothTimeoutVariants_ShareTheOneKindAndDifferOnlyInHintText()
    {
        var observed = SingleClassifiedStep(TimeoutObservedFixture);
        var unobserved = SingleClassifiedStep(TimeoutUnobservedFixture);

        Assert.Equal(observed.Kind, unobserved.Kind);
        Assert.NotEqual(observed.Hint, unobserved.Hint);
    }

    [Fact]
    public void InconclusiveStepWithNoAttemptsAtAll_ClassifiesAsTheEmptyTimeoutVariant()
    {
        const string events = """
            {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":30000}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.StartsWith("No values observed at all", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count in the non-empty variant's hint is the number of attempts that actually carried an
    /// observation, not the attempt count — a partially-observed RETRY is the case that tells them
    /// apart.
    /// </summary>
    [Fact]
    public void TheNonEmptyTimeoutHintCounts_OnlyAttemptsThatCarriedAnObservation()
    {
        const string events = """
            {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL"}
            {"type":"step-attempt","stepId":"expect-order-event","attempt":2,"tMs":300,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-attempt","stepId":"expect-order-event","attempt":3,"tMs":900,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(
            "Observed 2 value(s) but none matched; the match key or capture path is probably wrong.",
            reason.Hint);
    }

    // ── capture_unmet ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepWithTheCaptureUnmetSignature_ClassifiesAsCaptureUnmet_AndItsHintNamesStepAndCapture()
    {
        var reason = SingleClassifiedStep(CaptureUnmetFixture);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal(
            "Step seed-order expected orderId but observed nothing; check the capture path or the upstream producer.",
            reason.Hint);
    }

    /// <summary>
    /// The capture-unmet SHAPE on a step that demonstrably POLLED is an ordinary mid-RETRY miss, not
    /// a capture that resolved to nothing — so it classifies as <c>timeout</c>, not
    /// <c>capture_unmet</c>.
    /// </summary>
    /// <remarks>
    /// <b>This test was inverted by a code review, and the inversion is the fix.</b> It previously
    /// asserted that capture_unmet OUTRANKS timeout on exactly this input — pinning as intended the
    /// behaviour the review found to be a defect. Both fixtures the signature was drawn from
    /// (<c>Run/GetStepTimelineOrchestratorTests.cs:218</c> is attempt 3 of a poll that PASSES on
    /// attempt 4) depict this shape as a poll miss, and because capture_unmet outranked timeout the
    /// misclassification also cost US-S4-03 its <c>timeouts</c>/<c>match</c> proposals for the
    /// commonest Inconclusive shape there is.
    /// </remarks>
    [Fact]
    public void AStepThatDemonstrablyPolled_FallsThroughToTimeout_EvenCarryingTheCaptureUnmetShape()
    {
        const string events = """
            {"type":"step-attempt","stepId":"seed-order","attempt":1,"tMs":50,"outcome":"FAIL","observation":{"expected":"orderId","got":null}}
            {"type":"step-attempt","stepId":"seed-order","attempt":2,"tMs":60,"outcome":"FAIL","observation":{"expected":"orderId","got":null}}
            {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":110,"observation":{"expected":"orderId","got":null}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.Equal(
            "Observed 2 value(s) but none matched; the match key or capture path is probably wrong.",
            reason.Hint);
    }

    /// <summary>
    /// The boundary the attempt gate draws: ONE recorded attempt is a single try, not a poll, so the
    /// capture-unmet shape still means what it says there.
    /// </summary>
    [Fact]
    public void AStepWithASingleAttempt_StillClassifiesAsCaptureUnmet()
    {
        var reason = SingleClassifiedStep(CaptureUnmetFixture);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal(1, SuiteEventParser.Parse(CaptureUnmetFixture).Steps.Single().AttemptCount);
    }

    /// <summary>
    /// The gate reads <see cref="StepOutcome.AttemptCount"/> — the ENGINE's own highest attempt
    /// number — not the length of the parsed attempt list.
    /// </summary>
    /// <remarks>
    /// <b>Peer review found the list undercounts in two real cases</b>, both of which would have
    /// re-opened the misclassification the gate closes: <c>AttemptsByStepId</c> is keyed by step id
    /// alone, so a multi-suite run merges same-named steps across suites, and a malformed
    /// <c>step-attempt</c> line is dropped by the parser entirely. This fixture is the second case —
    /// the attempt events for attempts 1 and 2 are malformed JSON and vanish, leaving an EMPTY
    /// attempt list beside a <c>step-completed</c> event whose own attempt bookkeeping still says the
    /// step polled three times.
    /// </remarks>
    [Fact]
    public void AStepWhoseAttemptEventsWereLost_IsStillGatedOutByTheEnginesOwnAttemptCount()
    {
        var summary = SuiteEventParser.Parse(string.Empty);
        var step = new StepOutcome(
            "seed-order",
            nameof(RunVerdict.Inconclusive),
            110,
            AttemptCount: 3,
            """{"expected":"orderId","got":null}""");

        var reason = VerdictReasonClassifier.ClassifyStep(step, summary);

        Assert.NotNull(reason);
        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.NotEqual(VerdictReasonKinds.CaptureUnmet, reason.Kind);
    }

    /// <summary>
    /// The <c>expected</c> key does not always name a CAPTURE — <c>{"expected":"UP","actual":null}</c>
    /// carries a literal expected VALUE — so the hint is worded to be true either way.
    /// </summary>
    /// <remarks>
    /// Peer review found the earlier wording ("never captured UP") confidently misdescribed exactly
    /// this shape. The rule cannot tell the two apart from the event alone, so the hint stops
    /// claiming to.
    /// </remarks>
    [Fact]
    public void TheCaptureUnmetHint_DoesNotCallTheExpectedValueACaptureName()
    {
        const string events = """
            {"type":"step-completed","stepId":"probe-status","verdict":"INCONCLUSIVE","durationMs":12,"observation":{"expected":"UP","actual":null}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal(
            "Step probe-status expected UP but observed nothing; check the capture path or the upstream producer.",
            reason.Hint);
        Assert.DoesNotContain("captured UP", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>An IMMEDIATE step records no attempts at all — the other side of the same gate.</summary>
    [Fact]
    public void AnImmediateStepWithNoAttempts_ClassifiesAsCaptureUnmet()
    {
        const string events = """
            {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":12,"observation":{"expected":"orderId","got":null}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, SingleClassifiedStep(events).Kind);
    }

    // ── capture_unmet: the ENGINE's own measured shape (vouchfx-mcp#86) ─────────────────────────

    /// <summary>
    /// The measured engine shape classifies as <c>capture_unmet</c>, names the CAPTURE in its hint,
    /// and publishes that name on the evidence channel.
    /// </summary>
    /// <remarks>
    /// Before vouchfx-mcp#86 this exact observation classified as <c>timeout</c> with "No values
    /// observed at all; the producer path, target name, or serialization is the likely cause." — a
    /// wrong kind for a step that did not time out. The <c>DoesNotContain</c> below pins that
    /// specific regression rather than only the positive case.
    /// </remarks>
    [Fact]
    public void TheEngineCaptureUnmetObservation_ClassifiesAsCaptureUnmet_NamesTheCapture_AndPublishesIt()
    {
        var reason = SingleClassifiedStep(EngineCaptureUnmetFixture);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal(EngineCaptureUnmetHint, reason.Hint);
        Assert.Equal("missing", reason.Evidence?.CaptureName);

        // The fact and the sentence come from ONE extraction, like every other evidence field.
        Assert.Contains(reason.Evidence!.CaptureName!, reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("No values observed at all", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ATTEMPT GATE does not apply to the engine shape: a step that demonstrably POLLED and then
    /// reported <c>captureUnmet</c> is still capture-unmet.
    /// </summary>
    /// <remarks>
    /// The gate exists because the SECONDARY expected/observed-null shape is INFERENCE — that shape
    /// on a polling step is an ordinary mid-RETRY miss. <c>captureUnmet</c> is the engine STATING
    /// which capture went unmet, so there is nothing to infer and no reason a RETRY step should be
    /// told it timed out instead. A step id of five attempts is used here precisely because it is
    /// well past <c>MaxAttemptsForCaptureUnmet</c>.
    /// </remarks>
    [Fact]
    public void TheEngineCaptureUnmetShape_IsNotGatedByTheAttemptCount()
    {
        const string events = """
            {"type":"step-attempt","stepId":"poll-then-capture","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-attempt","stepId":"poll-then-capture","attempt":2,"tMs":200,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-attempt","stepId":"poll-then-capture","attempt":3,"tMs":300,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-attempt","stepId":"poll-then-capture","attempt":4,"tMs":400,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-attempt","stepId":"poll-then-capture","attempt":5,"tMs":500,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-completed","stepId":"poll-then-capture","verdict":"INCONCLUSIVE","durationMs":1500,"observation":{"captureUnmet":"orderId"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        // Precondition: the engine's own attempt bookkeeping really does say this step polled.
        Assert.Equal(5, SuiteEventParser.Parse(events).Steps.Single().AttemptCount);

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal("orderId", reason.Evidence?.CaptureName);
    }

    /// <summary>
    /// A <c>captureUnmet</c> value that is not a non-empty STRING is not the engine naming a capture
    /// — the rule declines it and the step falls through exactly as it did before this key existed.
    /// </summary>
    /// <remarks>
    /// The value's CONTRACT is a capture name. A number, an object, an array, a JSON <c>null</c> or
    /// an empty string is a shape this build cannot read as one, and the fail-closed answer is to
    /// classify nothing from it rather than to render "declared capture 3" or a hint with a hole in
    /// it. An Inconclusive step then reaches the timeout rule, which is less specific but never
    /// wrong — the same accepted cost <c>FindPartitionText</c> states for a truncated observation.
    /// </remarks>
    [Theory]
    [InlineData("3")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("""{"name":"missing"}""")]
    [InlineData("""["missing"]""")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void ACaptureUnmetValueThatIsNotACaptureName_FallsThroughAsBefore(string jsonValue)
    {
        // Concatenated rather than interpolated: the value is followed by two closing braces, which
        // no number of '$' characters lets a raw interpolated literal spell unambiguously.
        var events =
            """{"type":"step-completed","stepId":"probe","verdict":"INCONCLUSIVE","durationMs":10,"observation":{"captureUnmet":"""
            + jsonValue + "}}\n"
            + """{"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}""";

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.Null(reason.Evidence?.CaptureName);
    }

    /// <summary>
    /// The key is read at the TOP LEVEL of the observation only — a nested one falls through.
    /// </summary>
    /// <remarks>
    /// <b>Deliberate, and the reason is the same one that made the partition rule reject a KEY-only
    /// match.</b> An observation is arbitrary JSON that can ECHO a system-under-test response body —
    /// and, since <c>explain_run</c>/<c>diagnose_run</c> take an <c>eventsPath</c>, the whole file
    /// may be one a caller was HANDED rather than one this server produced. A nested search would
    /// let such a body name a "capture" this server then reports as unmet, with a suite-edit
    /// proposal built from it. The measured engine shape puts
    /// <c>captureUnmet</c> at the top level, which is the engine's own statement, so nothing is lost
    /// today — and if a future engine nests it, the step falls through to <c>timeout</c> (less
    /// specific, never wrong) rather than this rule being loosened over untrusted payload text.
    /// </remarks>
    [Fact]
    public void ANestedCaptureUnmetKey_IsNotTheEnginesStatement_AndFallsThrough()
    {
        const string events = """
            {"type":"step-completed","stepId":"echo-body","verdict":"INCONCLUSIVE","durationMs":10,"observation":{"responseBody":{"captureUnmet":"attacker-chosen"}}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.DoesNotContain("attacker-chosen", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hostile capture name — control characters, and 500 characters of it — is sanitised and
    /// capped like every other value spliced into a hint.
    /// </summary>
    [Fact]
    public void AHostileCaptureName_IsSanitisedAndCapped_AndTheHintStaysWithinItsBound()
    {
        var hostile = "A\\u0001B" + new string('n', 500);
        var events = $$$"""
            {"type":"step-completed","stepId":"probe","verdict":"INCONCLUSIVE","durationMs":10,"observation":{"captureUnmet":"{{{hostile}}}"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.DoesNotContain(reason.Hint, c => c < 0x20);
        Assert.True(
            reason.Hint.Length <= VerdictReasonClassifier.MaxHintChars,
            $"Hint was {reason.Hint.Length} characters.");

        // The escape survives as its six printable characters (sanitised AFTER the JSON decode), and
        // the published name is capped at the value bound, not the hint bound.
        Assert.Equal(VerdictReasonClassifier.MaxValueChars, reason.Evidence?.CaptureName?.Length);
        // 0x01 is what the fixture's JSON escape decodes to; the sanitiser re-escapes it.
        var escapedControl = TextSanitiser.SanitiseForDisplay(((char)1).ToString());
        Assert.StartsWith("A" + escapedControl + "Bn", reason.Evidence!.CaptureName!, StringComparison.Ordinal);
        Assert.EndsWith("…", reason.Evidence.CaptureName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The DERIVED worst case: two at-cap values in one sentence truncate the HINT, and the capture
    /// name still arrives whole on the evidence channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves matter, and they are different guarantees</b> (a peer-review nit asked for the
    /// arithmetic to be pinned rather than reasoned about). The hint's fixed wording is 123
    /// characters (MEASURED from the literal: "Step " 5 + " declared capture " 18 + the 100-character
    /// tail — an earlier version of this remark said 174/414, a hand-derived sum that was wrong by 51,
    /// which is exactly why the bound lives in the assertion below rather than here); two values at
    /// <see cref="VerdictReasonClassifier.MaxValueChars"/> take it to 363,
    /// so <c>VerdictReason</c> truncates to <see cref="VerdictReasonClassifier.MaxHintChars"/> with a
    /// visible marker — the SENTENCE is lossy at this extreme, by design.
    /// </para>
    /// <para>
    /// The EVIDENCE is not. <c>SpecEditProposalBuilder</c> writes the capture name into a YAML key a
    /// host may apply, so a name silently shortened by the hint's budget would be an edit naming a
    /// capture that does not exist. This pins that the published name is whole and clipped by
    /// nothing but its own value cap — which is the entire reason the builder reads the evidence
    /// rather than the sentence.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoAtCapValues_TruncateTheHintButNeverThePublishedCaptureName()
    {
        var stepId = new string('s', VerdictReasonClassifier.MaxValueChars);
        var captureName = new string('c', VerdictReasonClassifier.MaxValueChars);
        var events = $$$"""
            {"type":"step-completed","stepId":"{{{stepId}}}","verdict":"INCONCLUSIVE","durationMs":10,"observation":{"captureUnmet":"{{{captureName}}}"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);

        // The sentence IS clipped here — at the bound, and visibly.
        Assert.Equal(VerdictReasonClassifier.MaxHintChars, reason.Hint.Length);
        Assert.EndsWith("…", reason.Hint, StringComparison.Ordinal);

        // The published name is NOT: whole, unmarked, and at its own cap rather than the hint's.
        Assert.Equal(captureName, reason.Evidence?.CaptureName);
        Assert.DoesNotContain("…", reason.Evidence!.CaptureName!, StringComparison.Ordinal);

        // ...and it still fits inside the truncated sentence at these lengths, which is what makes
        // the hint usable at the extreme rather than merely bounded.
        Assert.Contains(captureName, reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Precedence: the engine's <c>captureUnmet</c> statement outranks a partition sentence sitting
    /// elsewhere in the SAME observation.
    /// </summary>
    [Fact]
    public void TheEngineCaptureUnmetKey_OutranksAPartitionSentenceInTheSameObservation()
    {
        const string events = """
            {"type":"step-completed","stepId":"consume-then-capture","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"captureUnmet":"orderId","reason":"partition grace period exceeded for topic orders"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal("orderId", reason.Evidence?.CaptureName);
    }

    /// <summary>
    /// The SECONDARY (expected/observed-null) shape is untouched by vouchfx-mcp#86 — same kind, same
    /// hint, same attempt gate — and it publishes NO capture name.
    /// </summary>
    /// <remarks>
    /// The omission is the point: that shape's <c>expected</c> value may be a capture variable or a
    /// literal expected value, and the hint is worded to be true either way
    /// (<see cref="TheCaptureUnmetHint_DoesNotCallTheExpectedValueACaptureName"/>). Publishing it as
    /// a capture NAME would assert the reading the hint refuses to make, and would hand
    /// <c>SpecEditProposalBuilder</c> a literal value to emit as a YAML capture key.
    /// </remarks>
    [Fact]
    public void TheSecondaryCaptureUnmetShape_IsUnchanged_AndPublishesNoCaptureName()
    {
        var reason = SingleClassifiedStep(CaptureUnmetFixture);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal(
            "Step seed-order expected orderId but observed nothing; check the capture path or the upstream producer.",
            reason.Hint);
        Assert.Null(reason.Evidence?.CaptureName);
    }

    // ── partition ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepNamingAPartitionSignal_ClassifiesAsPartition_AndRelaysTheEngineTextVerbatim()
    {
        var reason = SingleClassifiedStep(PartitionFixture);

        Assert.Equal(VerdictReasonKinds.Partition, reason.Kind);

        // The engine's OWN sentence, relayed with nothing added, removed, or rephrased around it.
        Assert.Equal("partition grace period exceeded for topic orders", reason.Hint);
    }

    [Fact]
    public void PartitionOutranksTimeout_WhenAnInconclusiveStepCarriesBothSignals()
    {
        const string events = """
            {"type":"step-attempt","stepId":"consume-events","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false}}
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"reason":"partition grace period exceeded for topic orders","topic":"orders"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        Assert.Equal(VerdictReasonKinds.Partition, SingleClassifiedStep(events).Kind);
    }

    /// <summary>
    /// A signal that appears only in a JSON KEY is not the engine SAYING "partition" — it is an
    /// ordinary Kafka-shaped poll observation naming its partition field. It classifies as
    /// <c>timeout</c>.
    /// </summary>
    /// <remarks>
    /// <b>Inverted by a code review, and the inversion is the fix.</b> This test previously asserted
    /// that such an observation classified as <c>partition</c> (relaying the whole JSON blob as the
    /// hint) — pinning as intended the behaviour the review found to be a MAJOR defect: under
    /// US-S4-03 <c>partition</c> yields guidance text only, so a poll observation misread this way
    /// silently loses the <c>timeouts</c>/<c>match</c> spec-edit proposals it should have produced.
    /// </remarks>
    [Fact]
    public void APartitionSignalOnlyInAKey_IsNotPartition_AndFallsThroughToTimeout()
    {
        const string events = """
            {"type":"step-attempt","stepId":"consume-events","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false,"partition":3,"offset":112}}
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"matched":false,"partition":3,"offset":112}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.DoesNotContain("partition", reason.Hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A signal in a NUMBER-valued field is not a sentence either.</summary>
    [Fact]
    public void APartitionSignalOnlyInANumericValue_IsNotPartition()
    {
        const string events = """
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"partition":7}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        Assert.Equal(VerdictReasonKinds.Timeout, SingleClassifiedStep(events).Kind);
    }

    /// <summary>
    /// An observation that will not parse as JSON classifies as NOTHING — an Inconclusive step falls
    /// through to <c>timeout</c> rather than being called a partition on the strength of a fragment.
    /// </summary>
    /// <remarks>
    /// <b>Inverted by peer review, and the inversion closes a hole the key-position fix had left
    /// open.</b> This test previously asserted the raw text was relayed as the hint. But
    /// <c>SuiteEventParser</c> caps an observation at 10,000 characters MID-DOCUMENT, so EVERY
    /// over-cap observation is unparseable — which meant a large Kafka-shaped poll observation
    /// (<c>{"matched":false,"partition":3,…}</c> plus a big payload) classified as <c>partition</c>
    /// on its KEY again, by the fallback, with a JSON fragment as its "hint": the exact false
    /// positive the string-value rule removed, plus a plain-text-contract violation and
    /// system-under-test payload text in a hint. The accepted cost is stated in
    /// <c>FindPartitionText</c>'s remarks: a genuine partition sentence inside a truncated
    /// observation is lost to <c>timeout</c>, which is less specific but never wrong.
    /// </remarks>
    [Fact]
    public void APartitionSignalInUnparseableObservationText_IsNotPartition_AndFallsThroughToTimeout()
    {
        var summary = SuiteEventParser.Parse(string.Empty);
        var step = new StepOutcome(
            "consume-events",
            nameof(RunVerdict.Inconclusive),
            45000,
            1,
            """{"reason":"partition grace period exceeded for topic ord""");

        var reason = VerdictReasonClassifier.ClassifyStep(step, summary);

        Assert.NotNull(reason);
        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.DoesNotContain("{", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The concrete shape peer review named: a poll observation big enough to be truncated at parse
    /// time, carrying "partition" only as a KEY. It must not classify as a partition.
    /// </summary>
    [Fact]
    public void ALargeKafkaShapedPollObservation_TruncatedAtParseTime_IsNeverPartition()
    {
        var payload = new string('z', 12_000);
        var events = $$$"""
            {"type":"step-attempt","stepId":"consume-events","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false,"partition":3,"offset":112,"payload":"{{{payload}}}"}}
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"matched":false,"partition":3,"offset":112,"payload":"{{{payload}}}"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var summary = SuiteEventParser.Parse(events);
        var step = summary.Steps.Single();

        // Precondition: the parser really did truncate it, so this exercises the JsonException path.
        Assert.NotNull(step.Observation);
        Assert.Equal(10_000, step.Observation.Length);

        var reason = VerdictReasonClassifier.ClassifyStep(step, summary);

        Assert.NotNull(reason);
        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
    }

    /// <summary>
    /// The precedence the type's own docs promise, pinned: capture-unmet evidence outranks a
    /// partition signal when a single step's observation carries both.
    /// </summary>
    [Fact]
    public void CaptureUnmetOutranksPartition_WhenOneObservationCarriesBothSignals()
    {
        const string events = """
            {"type":"step-completed","stepId":"seed-order","verdict":"INCONCLUSIVE","durationMs":50,"observation":{"expected":"orderId","got":null,"reason":"partition grace period exceeded"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, SingleClassifiedStep(events).Kind);
    }

    // ── The timeout variants' machine-branchable discriminator (US-S4-03's signal) ───────────────

    /// <summary>
    /// <see cref="VerdictEvidence.ObservedValues"/> — the flag US-S4-03's proposal builder branches
    /// on — agrees with the hint variant the rule table actually emitted.
    /// </summary>
    /// <remarks>
    /// <b>Asserted through the SHIPPED seam.</b> Both tests here previously called an
    /// <c>AnyAttemptCarriedAnObservation</c> helper, which the evidence-channel fix left with no
    /// production caller at all; a review had them re-pointed at the published evidence and the
    /// helper deleted, because a test against a test-only helper asserts that the helper works, not
    /// that the shipped path does. Asserting the flag and the sentence TOGETHER is the point: they
    /// come from one computation, so a change that reworded one without the other fails here rather
    /// than silently making advisory prose load-bearing.
    /// </remarks>
    [Theory]
    [InlineData(nameof(TimeoutObservedFixture), true)]
    [InlineData(nameof(TimeoutUnobservedFixture), false)]
    public void TheTimeoutEvidenceFlag_AgreesWithTheVariantTheRuleTableEmitted(string fixtureName, bool expected)
    {
        var events = Corpus.Single(f => f.Name == fixtureName).Events;
        var summary = SuiteEventParser.Parse(events);
        var step = summary.Steps.Single(s => s.Verdict != nameof(RunVerdict.Pass));

        var reason = VerdictReasonClassifier.ClassifyStep(step, summary);

        Assert.NotNull(reason);
        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.Equal(expected, reason.Evidence?.ObservedValues);
        Assert.Equal(expected, reason.Hint.StartsWith("Observed", StringComparison.Ordinal));
    }

    /// <summary>A step with no attempts at all observed nothing — the flag says so, and so does the hint.</summary>
    [Fact]
    public void TheTimeoutEvidenceFlag_IsFalseForAStepWithNoAttemptsAtAll()
    {
        var summary = SuiteEventParser.Parse(string.Empty);
        var step = new StepOutcome("expect-order-event", nameof(RunVerdict.Inconclusive), 30_000, 1);

        var reason = VerdictReasonClassifier.ClassifyStep(step, summary);

        Assert.NotNull(reason);
        Assert.Equal(VerdictReasonKinds.Timeout, reason.Kind);
        Assert.False(reason.Evidence?.ObservedValues);
        Assert.StartsWith("No values observed at all", reason.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partition sentence padded with more leading whitespace than the hint cap allows still
    /// classifies, with the TRIMMED sentence as its hint — it does not throw.
    /// </summary>
    /// <remarks>
    /// <b>The bug this pins was real and narrow.</b> The string-scalar relay capped BEFORE trimming
    /// while its sibling raw-text fallback trimmed first, so 300+ leading spaces followed by
    /// "partition" satisfied the signal check, capped to 300 spaces (0x20 is printable ASCII, so
    /// sanitisation leaves it), and <c>VerdictReason</c>'s own non-empty guard then threw
    /// <see cref="ArgumentException"/> straight out of <c>ClassifyStep</c> — turning one malformed
    /// observation into a failed tool call on an already-failing run, against the parser's governing
    /// "one bad line never makes a good run's result unusable" philosophy.
    /// </remarks>
    [Fact]
    public void AWhitespacePaddedPartitionSentence_IsTrimmedBeforeCapping_AndNeverThrows()
    {
        var padded = new string(' ', 400) + "partition grace period exceeded for topic orders";
        var events = $$$"""
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"reason":"{{{padded}}}"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Partition, reason.Kind);
        Assert.Equal("partition grace period exceeded for topic orders", reason.Hint);
    }

    /// <summary>
    /// A partition sentence longer than a value-shaped fragment is bounded by the HINT cap (300), not
    /// the value cap (120) — it IS the whole hint rather than a value spliced into one. A spec review
    /// flagged the asymmetry when the precise relay used the tighter bound while the coarse raw-text
    /// fallback used the looser one.
    /// </summary>
    [Fact]
    public void ALongPartitionSentence_IsBoundedByTheHintCapNotTheValueCap()
    {
        var sentence = "partition grace period exceeded: " + new string('x', 200);
        var events = $$$"""
            {"type":"step-completed","stepId":"consume-events","verdict":"INCONCLUSIVE","durationMs":45000,"observation":{"reason":"{{{sentence}}}"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Partition, reason.Kind);
        Assert.Equal(sentence, reason.Hint);
        Assert.True(reason.Hint.Length > VerdictReasonClassifier.MaxValueChars);
    }

    // ── Gherkin 4: assertion, and ONLY assertion, on a Fail step ────────────────────────────────

    [Fact]
    public void FailStepWithExpectedAndActual_ClassifiesAsAssertion()
    {
        var reason = SingleClassifiedStep(AssertionFixture);

        Assert.Equal(VerdictReasonKinds.Assertion, reason.Kind);
        Assert.Equal("Expected 120.00, actual 95.00.", reason.Hint);
    }

    /// <summary>
    /// The engine's real nested observation shape
    /// (<c>{"exists":{"expected":true,"actual":false}}</c>, measured in
    /// <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>) — the evidence search has to reach it, or
    /// the assertion rule would be dead on the shape the engine actually writes.
    /// </summary>
    [Fact]
    public void FailStepWithANestedExpectedActualPair_StillClassifiesAsAssertion()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"exists":{"expected":true,"actual":false}}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Assertion, reason.Kind);
        Assert.Equal("Expected true, actual false.", reason.Hint);
    }

    [Fact]
    public void FailStepWithNoObservationAtAll_IsLeftUnclassified()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        Assert.Null(ClassifyNotableSteps(events).Single());
    }

    /// <summary>
    /// Gherkin 4's second clause, at its sharpest: a Fail step carrying the capture-unmet signature
    /// (which on an Inconclusive step WOULD classify) gets no kind at all rather than
    /// <c>capture_unmet</c> — assertion is the only kind a Fail step can ever receive, and the rule
    /// table enforces that by branching on the verdict, not by hoping the other rules never match.
    /// </summary>
    [Fact]
    public void FailStepCarryingANonAssertionSignal_IsNeverGivenThatOtherKind()
    {
        const string events = """
            {"type":"step-completed","stepId":"seed-order","verdict":"FAIL","durationMs":50,"observation":{"expected":"orderId","got":null,"reason":"partition grace period exceeded"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        Assert.Null(ClassifyNotableSteps(events).Single());
    }

    [Fact]
    public void AcrossEveryFixture_AssertionIsTheOnlyKindEverAssignedToAFailStep()
    {
        foreach (var (name, events, _) in Corpus)
        {
            var summary = SuiteEventParser.Parse(events);
            foreach (var step in summary.Steps.Where(s => s.Verdict == nameof(RunVerdict.Fail)))
            {
                if (VerdictReasonClassifier.ClassifyStep(step, summary)?.Kind is { } kind)
                {
                    Assert.True(
                        kind == VerdictReasonKinds.Assertion,
                        $"Fixture {name}: Fail step '{step.StepId}' was classified '{kind}'; only 'assertion' is permitted on a Fail step.");
                }
            }
        }
    }

    [Fact]
    public void AcrossEveryFixture_NoNonFailStepOrEnvironmentErrorIsEverClassifiedAsAssertion()
    {
        foreach (var (name, events, _) in Corpus)
        {
            var summary = SuiteEventParser.Parse(events);

            foreach (var step in summary.Steps.Where(s => s.Verdict != nameof(RunVerdict.Fail)))
            {
                Assert.True(
                    VerdictReasonClassifier.ClassifyStep(step, summary)?.Kind != VerdictReasonKinds.Assertion,
                    $"Fixture {name}: non-Fail step '{step.StepId}' was classified 'assertion'.");
            }

            foreach (var error in summary.EnvironmentErrors)
            {
                Assert.NotEqual(
                    VerdictReasonKinds.Assertion,
                    VerdictReasonClassifier.ClassifyEnvironmentError(error).Kind);
            }
        }
    }

    // ── Gherkin 6: compile is never assigned by today's rule set ────────────────────────────────

    /// <summary>
    /// The story's dedicated negative test: <c>compile</c> is a rule-table ENTRY (spec §8.3
    /// vocabulary completeness, forward compatibility with a future <c>compile_spec</c> relay) that
    /// no rule this sprint implements ever assigns. Swept across the whole fixture corpus so a
    /// future change that accidentally wired it up is caught here.
    /// </summary>
    [Fact]
    public void NoFixtureInTheCorpus_EverProducesTheCompileKind()
    {
        // Set membership asserted through the set's OWN Contains: a FrozenSet implements both
        // ISet<T> and IReadOnlySet<T>, which makes Assert.Contains's two overloads ambiguous.
        Assert.Contains(VerdictReasonKinds.Compile, (IReadOnlySet<string>)VerdictReasonKinds.All);

        foreach (var kind in EveryKindTheCorpusProduces())
        {
            Assert.NotEqual(VerdictReasonKinds.Compile, kind);
        }
    }

    [Fact]
    public void EveryKindTheCorpusProduces_IsInTheDocumentedVocabulary()
    {
        foreach (var kind in EveryKindTheCorpusProduces())
        {
            Assert.Contains(kind, (IReadOnlySet<string>)VerdictReasonKinds.All);
        }
    }

    /// <summary>
    /// The fixture sweep above is EVADABLE — a new rule assigning <c>compile</c> on a shape no
    /// fixture happens to carry would pass it. This is the structural half: the token appears in
    /// <c>VerdictReasonClassifier.cs</c>'s executable source ONLY in its own declaration and in the
    /// vocabulary set, never in a rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derives its evidence from the real source file exactly as <c>SecretHygieneSourceGuardTests</c>
    /// does (via <see cref="SourceGuardScan"/>), and reads the COMMENT- AND STRING-STRIPPED source, so
    /// prose mentioning the kind — of which the file has plenty — cannot make it pass or fail.
    /// </para>
    /// <para>
    /// <b>The guard got SIMPLER when the vocabulary moved.</b> While <c>VerdictReasonKinds</c> lived
    /// in the classifier's own file this test had to distinguish "the declaration and the vocabulary
    /// set" from "a rule", by shape. Since the DTOs moved to <c>DiagnosisModels.cs</c> (a peer-review
    /// nit — they belong with the other diagnosis models), the RULE file may contain no reference to
    /// the kind at all, which is both a stronger assertion and one no reformatting can perturb. The
    /// declaration's continued existence is asserted separately against the models file, so the guard
    /// cannot pass merely because the entry was deleted.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCompileKind_IsReferencedNowhereInTheRuleTablesSource()
    {
        var classifierPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Diagnosis", "VerdictReasonClassifier.cs");
        Assert.True(File.Exists(classifierPath), $"Expected the classifier source at '{classifierPath}'.");

        var referencingLines = SourceGuardScan.ExecutableSourceOf(classifierPath)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\bCompile\b"))
            .ToList();

        Assert.True(
            referencingLines.Count == 0,
            $"VerdictReasonClassifier.cs references VerdictReasonKinds.Compile in executable code: "
            + $"[{string.Join(" | ", referencingLines)}]. No rule in this sprint may assign 'compile' "
            + "(US-S4-01: it exists for spec §8.3 vocabulary completeness only). If a future story "
            + "genuinely wires it up, widen this guard deliberately.");
    }

    /// <summary>
    /// ...and the entry still EXISTS, in the models file the vocabulary now lives in — otherwise the
    /// guard above would pass vacuously the moment someone deleted it.
    /// </summary>
    [Fact]
    public void TheCompileKind_IsStillDeclaredInTheDiagnosisModels()
    {
        var modelsPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Diagnosis", "DiagnosisModels.cs");
        var executable = SourceGuardScan.ExecutableSourceOf(modelsPath);

        Assert.Contains("public const string Compile", executable, StringComparison.Ordinal);
        Assert.Contains(VerdictReasonKinds.Compile, (IReadOnlySet<string>)VerdictReasonKinds.All);
    }

    /// <summary>
    /// The corpus is only a meaningful sweep if it actually covers the vocabulary: every kind except
    /// <c>compile</c> (which by design nothing produces) must be produced by at least one fixture.
    /// </summary>
    [Fact]
    public void TheFixtureCorpus_CoversEveryKindExceptCompile()
    {
        var produced = EveryKindTheCorpusProduces().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            VerdictReasonKinds.All.Where(k => k != VerdictReasonKinds.Compile).OrderBy(k => k, StringComparer.Ordinal),
            produced.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>Each fixture produces exactly the kind it was registered for — the snapshot of the table itself.</summary>
    [Fact]
    public void EveryFixture_ProducesExactlyTheKindItIsRegisteredFor()
    {
        foreach (var (name, events, expectedKind) in Corpus)
        {
            var kinds = ClassifyEverything(events).Select(r => r.Kind).Distinct().ToList();

            Assert.True(
                kinds.Count == 1 && kinds[0] == expectedKind,
                $"Fixture {name}: expected exactly the kind '{expectedKind ?? "(null)"}', got [{string.Join(", ", kinds.Select(k => k ?? "(null)"))}].");
        }
    }

    // ── Purity, hygiene, and bounds ─────────────────────────────────────────────────────────────

    [Fact]
    public void APassingStep_IsNeverClassified()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50,"observation":{"expected":"UP","actual":"UP"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        var summary = SuiteEventParser.Parse(events);

        Assert.Null(VerdictReasonClassifier.ClassifyStep(summary.Steps.Single(), summary));
    }

    [Fact]
    public void AnUnparseableObservation_LeavesTheStepUnclassifiedRatherThanThrowing()
    {
        // A truncated observation (SuiteEventParser caps at 10,000 characters, mid-JSON) is the real
        // shape behind this: the EVENT parses, but its observation text is no longer valid JSON.
        var summary = SuiteEventParser.Parse(string.Empty);
        var step = new StepOutcome("check-balance", nameof(RunVerdict.Fail), 10, 1, """{"expected":"SHIP""");

        Assert.Null(VerdictReasonClassifier.ClassifyStep(step, summary));
    }

    /// <summary>
    /// A JSON string value can carry an ESCAPED control character that survives
    /// <see cref="SuiteEventParser"/>'s sanitisation as six printable characters and only becomes a
    /// real control character when this classifier DECODES the JSON. Every extracted value is
    /// therefore re-sanitised — asserted here rather than assumed.
    /// </summary>
    [Fact]
    public void AnEscapedControlCharacterInTheEvidence_IsReSanitisedBeforeItReachesTheHint()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":10,"observation":{"expected":"A\u0001B","actual":"C"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.DoesNotContain(reason.Hint, c => c < 0x20);
        Assert.Equal(@"Expected A\u0001B, actual C.", reason.Hint);
    }

    // ── The EnvironmentError-verdict STEP branch (distinct from an environment-error EVENT) ──────

    [Fact]
    public void AStepWhoseOwnVerdictIsEnvironmentError_IsClassifiedByTheSameStepRules()
    {
        var reason = SingleClassifiedStep(EnvironmentErrorStepFixture);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Equal(
            "Step seed-order expected orderId but observed nothing; check the capture path or the upstream producer.",
            reason.Hint);
    }

    /// <summary>
    /// The tail of that branch: an EnvironmentError step matching no rule is left unclassified — the
    /// timeout rule is Inconclusive-only, so it must NOT catch this step.
    /// </summary>
    [Fact]
    public void AnEnvironmentErrorStepMatchingNoRule_IsLeftUnclassified_AndNeverFallsIntoTimeout()
    {
        const string events = """
            {"type":"step-attempt","stepId":"provision-db","attempt":1,"tMs":10,"outcome":"FAIL"}
            {"type":"step-completed","stepId":"provision-db","verdict":"ENV_ERROR","durationMs":10,"observation":{"note":"container exited"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        Assert.Null(ClassifyNotableSteps(events).Single());
    }

    /// <summary>
    /// A verdict string this build does not recognise is the taxonomy's "we don't know" state
    /// (<c>RunVerdict</c>'s unknown-token contract) — never classified as anything. Constructed
    /// directly, because <see cref="SuiteEventParser"/> would drop the step rather than surface an
    /// unrecognised verdict.
    /// </summary>
    [Fact]
    public void AStepCarryingAnUnrecognisedVerdictString_IsNeverClassified()
    {
        var summary = SuiteEventParser.Parse(string.Empty);
        var step = new StepOutcome(
            "some-step", "SomeFutureVerdict", 10, 1, """{"expected":"A","actual":"B","reason":"partition"}""");

        Assert.Null(VerdictReasonClassifier.ClassifyStep(step, summary));
    }

    // ── Bounds, hygiene, and determinism ────────────────────────────────────────────────────────

    /// <summary>
    /// Every hint the corpus produces — not just the one long-detail case below — is non-empty and
    /// within the bound US-S4-02's floor tier depends on.
    /// </summary>
    /// <remarks>
    /// Since the invariant moved into <see cref="VerdictReason"/> itself, an over-long or empty hint
    /// is unconstructible rather than merely unproduced, so this sweep can no longer FAIL on a
    /// hint's size — what it now guards is that no rule builds a hint the record has to truncate or
    /// reject (which surfaces as a thrown <see cref="ArgumentException"/> during classification, the
    /// shape the whitespace-padded partition case above documents). Read it as a regression guard on
    /// the rules, not as the enforcement of the bound.
    /// </remarks>
    [Fact]
    public void AcrossEveryFixture_EveryHintIsNonEmptyAndWithinTheHintCap()
    {
        foreach (var (name, events, _) in Corpus)
        {
            foreach (var reason in ClassifyEverything(events))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(reason.Hint),
                    $"Fixture {name}: produced a reason with an empty hint (kind '{reason.Kind ?? "(null)"}').");
                Assert.True(
                    reason.Hint.Length <= VerdictReasonClassifier.MaxHintChars,
                    $"Fixture {name}: hint for kind '{reason.Kind ?? "(null)"}' was {reason.Hint.Length} characters.");
            }
        }
    }

    /// <summary>
    /// A single VALUE spliced into a hint is capped at <see cref="VerdictReasonClassifier.MaxValueChars"/>
    /// BEFORE the whole-hint bound applies — so one long value cannot crowd the rest of the sentence
    /// out of the hint entirely.
    /// </summary>
    [Fact]
    public void ASingleOversizedValue_IsCappedAtTheValueBoundNotJustTheHintBound()
    {
        var hugeExpected = new string('e', 500);
        var events = $$$"""
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":10,"observation":{"expected":"{{{hugeExpected}}}","actual":"95.00"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        var reason = SingleClassifiedStep(events);

        // The tail of the sentence survives precisely BECAUSE the value was capped first: at 500
        // characters the expected value alone would have consumed the whole 300-character hint. The
        // cut is MARKED (the last kept character is the ellipsis, inside the same bound), so a reader
        // can tell a clipped value from a short one.
        var cappedValue = new string('e', VerdictReasonClassifier.MaxValueChars - 1) + '…';
        Assert.Equal($"Expected {cappedValue}, actual 95.00.", reason.Hint);
        Assert.Equal(VerdictReasonClassifier.MaxValueChars, cappedValue.Length);
    }

    /// <summary>
    /// The value cap bounds what REACHES the hint, so it is applied AFTER sanitisation — which can
    /// expand one non-ASCII character into a six-character <c>\uXXXX</c> escape.
    /// </summary>
    /// <remarks>
    /// A security review found the order reversed: capping first bounded the INPUT, so a 120-character
    /// Cyrillic value rendered 720 characters into the hint and crowded out the sentence around it —
    /// exactly what <c>MaxValueChars</c>' remarks promise cannot happen.
    /// </remarks>
    [Fact]
    public void ANonAsciiValue_IsCappedAfterSanitisationNotBefore()
    {
        // 40 Cyrillic characters — well under the 120-character cap as INPUT, six times over it once
        // sanitised.
        var cyrillic = string.Concat(Enumerable.Repeat("Ж", 40));
        var events = $$$"""
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":10,"observation":{"expected":"{{{cyrillic}}}","actual":"B"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.Assertion, reason.Kind);

        // Sanitised FIRST — each Ж is now the six printable characters of its escape, which is
        // exactly why capping has to happen afterwards.
        Assert.StartsWith("Expected " + TextSanitiser.SanitiseForDisplay("Ж"), reason.Hint, StringComparison.Ordinal);
        Assert.EndsWith(", actual B.", reason.Hint, StringComparison.Ordinal);

        // The rendered value is bounded by MaxValueChars, not 6x it — the whole point.
        var renderedValue = reason.Hint["Expected ".Length..^", actual B.".Length];
        Assert.Equal(VerdictReasonClassifier.MaxValueChars, renderedValue.Length);
    }

    // ── The health-gate timeout figure needs a keyword to vouch for it ──────────────────────────

    /// <summary>
    /// A millisecond figure is only presented as the configured window when a timeout-shaped word
    /// sits near it — otherwise an incidental measurement would be relayed as the health gate.
    /// </summary>
    /// <remarks>
    /// Peer review's point: a wrong REAL number is worse than a missing one, because it looks
    /// authoritative and US-S4-03 will carry it into a proposal's rationale.
    /// </remarks>
    /// <remarks>
    /// The fourth row is the one a code review caught the guard getting WRONG: with a bare
    /// <c>"after"</c> in the keyword list, "probe returned 502 after 15ms" matched and rendered
    /// "never became healthy within 15ms" — a real number from an unrelated measurement, presented
    /// as the configured gate, while the source comment claimed that case was excluded. Dropping
    /// <c>"after"</c> costs nothing: every legitimate phrasing above still matches on a word that
    /// actually names a deadline.
    /// </remarks>
    [Theory]
    [InlineData("health gate timed out after 30000ms", "30000")]
    [InlineData("did not become ready within 30000 ms", "30000")]
    [InlineData("gave up waiting; window was 45000ms", "45000")]
    [InlineData("probe returned 502 after 15ms, gave up", null)]
    [InlineData("container restarted 3 times; last probe took 250ms of CPU", null)]
    public void AMillisecondFigure_IsOnlyRelayedWhenATimeoutKeywordVouchesForIt(string detail, string? expectedMs)
    {
        var events = $$"""{"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"{{detail}}"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal(
            expectedMs is null
                ? "Resource events never became healthy; check its logs."
                : $"Resource events never became healthy within {expectedMs}ms; check its logs.",
            reason.Hint);
    }

    /// <summary>
    /// An implausible digit run disqualifies THAT figure and the scan continues — an earlier version
    /// returned null on it, so one junk number hid a real one later in the same message.
    /// </summary>
    [Fact]
    public void AnImplausiblyLongDigitRun_DoesNotAbortTheScanForALaterRealFigure()
    {
        var events = $$"""{"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"correlation 1234567890123456789012ms; timed out after 30000ms"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.Equal("Resource events never became healthy within 30000ms; check its logs.", reason.Hint);
    }

    /// <summary>
    /// A step id is a value like any other — capped before it reaches a hint. (A security review
    /// found it was the one splice site that was not: the parser caps a step id at 2,000 characters,
    /// which alone exceeds the whole hint budget.)
    /// </summary>
    [Fact]
    public void AnOversizedStepId_IsCappedBeforeItReachesAHint()
    {
        var hugeStepId = new string('s', 1_500);
        var events = $$$"""
            {"type":"step-completed","stepId":"{{{hugeStepId}}}","verdict":"INCONCLUSIVE","durationMs":10,"observation":{"expected":"orderId","got":null}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var reason = SingleClassifiedStep(events);

        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.StartsWith(
            $"Step {new string('s', VerdictReasonClassifier.MaxValueChars - 1)}… expected orderId",
            reason.Hint,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Invariant 4: an unresolved <c>${secret:…}</c> reference in an observation is relayed into the
    /// hint EXACTLY as the engine wrote it, and is never resolved — even when the environment
    /// variable it names is really set in this process.
    /// </summary>
    /// <remarks>
    /// Setting the sentinel variable is what makes the second assertion non-vacuous: a table that
    /// resolved references would have something to resolve TO. The classifier never reads the
    /// environment at all, which is the property this pins observably (the repo-wide source guard for
    /// environment access is <c>SecretHygieneSourceGuardTests</c>' own scope).
    /// </remarks>
    [Fact]
    public void ASecretReferenceInAnObservation_IsRelayedVerbatimAndNeverResolved()
    {
        var previous = Environment.GetEnvironmentVariable(SecretSentinelName);
        Environment.SetEnvironmentVariable(SecretSentinelName, SecretSentinelValue);
        try
        {
            var reason = SingleClassifiedStep(SecretReferenceObservationFixture);

            Assert.Equal(VerdictReasonKinds.Assertion, reason.Kind);
            Assert.Equal(
                "Expected ${secret:env/" + SecretSentinelName + "}, actual [REDACTED].",
                reason.Hint);
            Assert.DoesNotContain(SecretSentinelValue, reason.Hint, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretSentinelName, previous);
        }
    }

    // ── VerdictReason enforces its own contract (not merely its factory) ────────────────────────

    /// <summary>
    /// The bound belongs to the TYPE: a consumer constructing a reason directly — as US-S4-02's
    /// orchestrator will — cannot smuggle an oversized hint past the floor tier's budget guarantee.
    /// </summary>
    [Fact]
    public void VerdictReason_CapsAnOversizedHintOnEveryConstructionPath()
    {
        var oversized = new string('h', 5_000);

        var direct = new VerdictReason(VerdictReasonKinds.Timeout, oversized);
        Assert.Equal(VerdictReasonClassifier.MaxHintChars, direct.Hint.Length);

        var copied = direct with { Hint = oversized };
        Assert.Equal(VerdictReasonClassifier.MaxHintChars, copied.Hint.Length);

        // The cut is MARKED, not silent — asserted on both paths because length alone would stay
        // green if NormaliseHint reverted to a bare hint[..MaxHintChars] (a peer-review finding).
        Assert.EndsWith("…", direct.Hint, StringComparison.Ordinal);
        Assert.EndsWith("…", copied.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="VerdictReason.Evidence"/> is INTERNAL: it exists so US-S4-03's proposal builder can
    /// branch on facts rather than on prose, and it must never reach the wire — US-S4-02's tier byte
    /// baselines and every <c>Real*</c> golden depend on the serialised shape being unchanged.
    /// </summary>
    [Fact]
    public void EvidenceIsInternalOnly_AndNeverChangesTheSerialisedShape()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var bare = new VerdictReason(VerdictReasonKinds.Timeout, "a hint");
        var withEvidence = bare with
        {
            // EVERY field, deliberately: the guard is that no evidence member reaches the wire, so it
            // has to be constructed with all of them populated or a newly-added one escapes it.
            Evidence = new VerdictEvidence(
                ObservedValues: true,
                ImageReference: "ghcr.io/acme/x:1",
                HealthWindowMs: "30000",
                CaptureName: "missing"),
        };

        Assert.NotNull(withEvidence.Evidence);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(bare, options),
            System.Text.Json.JsonSerializer.Serialize(withEvidence, options));
        Assert.DoesNotContain(
            "evidence",
            System.Text.Json.JsonSerializer.Serialize(withEvidence, options),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The classifier is the single writer of that channel — every rule that has a fact to publish does.</summary>
    [Fact]
    public void TheClassifier_PublishesTheFactsItsRulesEstablished()
    {
        var timeoutObserved = SingleClassifiedStep(TimeoutObservedFixture);
        Assert.True(timeoutObserved.Evidence?.ObservedValues);

        var timeoutUnobserved = SingleClassifiedStep(TimeoutUnobservedFixture);
        Assert.False(timeoutUnobserved.Evidence?.ObservedValues);

        var pull = Assert.Single(ClassifyEnvironmentErrors(PullFixture));
        Assert.Equal("ghcr.io/acme/orders-api:latest", pull.Evidence?.ImageReference);

        var unhealthy = Assert.Single(ClassifyEnvironmentErrors(UnhealthyFixture));
        Assert.Equal("30000", unhealthy.Evidence?.HealthWindowMs);

        // ...and the facts agree with the sentences built from them.
        Assert.Contains(pull.Evidence!.ImageReference!, pull.Hint, StringComparison.Ordinal);
        Assert.Contains(unhealthy.Evidence!.HealthWindowMs!, unhealthy.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VerdictReason_RefusesAnEmptyHint(string hint) =>
        Assert.Throws<ArgumentException>(() => new VerdictReason(VerdictReasonKinds.Timeout, hint));

    [Fact]
    public void VerdictReason_RefusesANullHint() =>
        Assert.Throws<ArgumentNullException>(() => new VerdictReason(VerdictReasonKinds.Timeout, null!));

    [Fact]
    public void EveryHintIsBounded_EvenWhenTheSourceTextIsNot()
    {
        // errorKind/resourceName/detail are each capped at 2,000 characters at PARSE time, so a hint
        // built by concatenating them would still be ~4KB — carried on EVERY tier, including the
        // floor tier that exists to shed exactly this kind of text.
        var events = $$"""{"type":"environment-error","errorKind":"Seed","resourceName":"{{new string('r', 3_000)}}","detail":"{{new string('d', 3_000)}}"}""";

        var reason = Assert.Single(ClassifyEnvironmentErrors(events));

        Assert.True(
            reason.Hint.Length <= VerdictReasonClassifier.MaxHintChars,
            $"Expected a hint of at most {VerdictReasonClassifier.MaxHintChars} characters, got {reason.Hint.Length}.");
    }

    [Fact]
    public void ClassificationIsDeterministic_TheSameInputAlwaysYieldsTheSameHint()
    {
        foreach (var (_, events, _) in Corpus)
        {
            var first = ClassifyEverything(events).Select(r => (r.Kind, r.Hint)).ToList();
            var second = ClassifyEverything(events).Select(r => (r.Kind, r.Hint)).ToList();

            Assert.Equal(first, second);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every kind the corpus produces, across both surfaces, with unclassified entries dropped.</summary>
    private static IEnumerable<string> EveryKindTheCorpusProduces() =>
        Corpus.SelectMany(fixture => ClassifyEverything(fixture.Events))
            .Select(reason => reason.Kind)
            .Where(kind => kind is not null)
            .Select(kind => kind!);

    /// <summary>Classifies every notable step AND every environment-error record in one events stream.</summary>
    private static List<VerdictReason> ClassifyEverything(string events)
    {
        var summary = SuiteEventParser.Parse(events);

        var reasons = summary.Steps
            .Where(step => step.Verdict != nameof(RunVerdict.Pass))
            .Select(step => VerdictReasonClassifier.ClassifyStep(step, summary))
            .Where(reason => reason is not null)
            .Select(reason => reason!)
            .ToList();

        reasons.AddRange(summary.EnvironmentErrors.Select(VerdictReasonClassifier.ClassifyEnvironmentError));
        return reasons;
    }

    /// <summary>
    /// Every notable (non-<c>Pass</c>) step's reason, in file order — <see langword="null"/> entries
    /// preserved, so a test can assert a step was deliberately left unclassified.
    /// </summary>
    private static List<VerdictReason?> ClassifyNotableSteps(string events)
    {
        var summary = SuiteEventParser.Parse(events);
        return summary.Steps
            .Where(step => step.Verdict != nameof(RunVerdict.Pass))
            .Select(step => VerdictReasonClassifier.ClassifyStep(step, summary))
            .ToList();
    }

    /// <summary>The one notable step in a fixture, asserted classified — the hint-snapshot tests' entry point.</summary>
    private static VerdictReason SingleClassifiedStep(string events)
    {
        var reason = Assert.Single(ClassifyNotableSteps(events));
        Assert.NotNull(reason);
        return reason;
    }

    private static List<VerdictReason> ClassifyEnvironmentErrors(string events) =>
        SuiteEventParser.Parse(events).EnvironmentErrors
            .Select(VerdictReasonClassifier.ClassifyEnvironmentError)
            .ToList();
}
