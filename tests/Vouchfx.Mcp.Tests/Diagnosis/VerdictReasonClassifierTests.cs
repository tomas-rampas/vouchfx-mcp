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
    private static readonly (string Name, string Events, string? ExpectedKind)[] Corpus =
    [
        (nameof(PullFixture), PullFixture, VerdictReasonKinds.Pull),
        (nameof(UnhealthyFixture), UnhealthyFixture, VerdictReasonKinds.Unhealthy),
        (nameof(SeedFixture), SeedFixture, VerdictReasonKinds.Seed),
        (nameof(TimeoutObservedFixture), TimeoutObservedFixture, VerdictReasonKinds.Timeout),
        (nameof(TimeoutUnobservedFixture), TimeoutUnobservedFixture, VerdictReasonKinds.Timeout),
        (nameof(CaptureUnmetFixture), CaptureUnmetFixture, VerdictReasonKinds.CaptureUnmet),
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
            "Step seed-order never captured orderId: the capture path resolved to nothing.",
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
        Assert.Single(SuiteEventParser.Parse(CaptureUnmetFixture).AttemptsByStepId["seed-order"]);
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
    /// The one case that still relays the whole blob: an observation capped mid-document at parse
    /// time is not parseable JSON, so the sentence cannot be isolated. Dropping the classification
    /// would lose a real signal, so the raw engine text is relayed instead.
    /// </summary>
    [Fact]
    public void APartitionSignalInUnparseableObservationText_RelaysTheRawTextAsTheHint()
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
        Assert.Equal(VerdictReasonKinds.Partition, reason.Kind);
        Assert.Equal("""{"reason":"partition grace period exceeded for topic ord""", reason.Hint);
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
    /// <b>The two permitted shapes are recognised structurally, not by a hardcoded line.</b> An
    /// earlier version matched the vocabulary set's full line text verbatim, so merely re-wrapping
    /// the <c>All</c> initialiser would have failed this test with a message about assigning
    /// <c>compile</c> — a misleading failure for a formatting change. A vocabulary entry is now
    /// recognised as either a LISTING (the line names several kinds alongside <c>Compile</c>) or a
    /// bare one-per-line entry, both of which survive reformatting, while any line that could
    /// actually assign the kind names it alone in executable code.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCompileKind_IsReferencedNowhereInTheClassifierSourceExceptItsOwnDeclaration()
    {
        var classifierPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Diagnosis", "VerdictReasonClassifier.cs");
        Assert.True(File.Exists(classifierPath), $"Expected the classifier source at '{classifierPath}'.");

        var referencingLines = SourceGuardScan.ExecutableSourceOf(classifierPath)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\bCompile\b"))
            .ToList();

        foreach (var line in referencingLines)
        {
            var isDeclaration = line.StartsWith("public const string Compile", StringComparison.Ordinal);

            // A LISTING: the line enumerates the vocabulary, so it names other kinds too.
            var otherKindsNamed = OtherKindIdentifiers.Count(
                kind => System.Text.RegularExpressions.Regex.IsMatch(line, $@"\b{kind}\b"));

            // ...or the same listing wrapped one entry per line.
            var isBareListingEntry = System.Text.RegularExpressions.Regex.IsMatch(line, @"^Compile\s*[,\]\}]*;?$");

            Assert.True(
                isDeclaration || otherKindsNamed >= 2 || isBareListingEntry,
                $"'{line}' references VerdictReasonKinds.Compile outside its declaration and the vocabulary set. "
                + "No rule in this sprint may assign 'compile' (US-S4-01: it exists for spec §8.3 vocabulary "
                + "completeness only). If a future story genuinely wires it up, widen this guard deliberately.");
        }

        // Both permitted references must still be present — otherwise the sweep above passes because
        // the entry was DELETED, which is a different regression this test should also catch. A FLOOR
        // rather than an exact count, so re-wrapping the initialiser cannot fail this either.
        Assert.True(
            referencingLines.Count >= 2,
            $"Expected the declaration and at least one vocabulary reference, found {referencingLines.Count}.");
    }

    /// <summary>Every kind identifier except <c>Compile</c> — the guard above's "this line is a listing" signal.</summary>
    private static readonly string[] OtherKindIdentifiers =
        ["Pull", "Unhealthy", "Seed", "Timeout", "CaptureUnmet", "Partition", "Assertion"];

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
            "Step seed-order never captured orderId: the capture path resolved to nothing.",
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
        // characters the expected value alone would have consumed the whole 300-character hint.
        Assert.Equal($"Expected {new string('e', VerdictReasonClassifier.MaxValueChars)}, actual 95.00.", reason.Hint);
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
        Assert.Contains(
            $"Step {new string('s', VerdictReasonClassifier.MaxValueChars)} never captured orderId",
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
