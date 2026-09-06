using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// <see cref="GetRunArtifactsOrchestrator"/>'s unit tests (US-S3-07): the story's three Gherkin
/// scenarios, the argument vocabulary (<c>kind</c>/<c>container</c>/<c>tailLines</c>), the honest
/// inventory of what the registry and event stream actually yield, and the response bound the type's
/// own remarks quote.
/// </summary>
/// <remarks>
/// Driven against the orchestrator directly, for the reason its siblings' unit tests give: a
/// pathological 500-resource fixture proves nothing extra by travelling over JSON-RPC, and costs a
/// server per case. <c>RealGetRunArtifactsMcpTests</c> owns the wire-facing goldens — in particular the
/// claims that are ONLY about serialisation (an omitted <c>html</c>, an explicit null <c>health</c>),
/// which no in-process assertion can settle.
/// </remarks>
public class GetRunArtifactsOrchestratorTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    /// <summary>Mirrors the size probe every sibling tool uses, so a measured figure here is comparable.</summary>
    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A run whose environment failed in two places — the shape US-S3-07's second Gherkin scenario
    /// describes, expressed in the only form this server can actually source it from.
    /// </summary>
    private const string TwoFailedResources =
        """
        {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"pull access denied"}
        {"type":"environment-error","errorKind":"Provision","resourceName":"orders-db","detail":"container exited unexpectedly"}
        {"type":"scenario-completed","scenarioId":"place-order","verdict":"ENV_ERROR"}
        """;

    /// <summary>The two resource ids <see cref="TwoFailedResources"/> names, in file order.</summary>
    private static readonly string[] TwoFailedResourceIds = ["orders-api", "orders-db"];

    // ── Gherkin 1: logs are reported as unavailable, not silently empty ──────────────────────────

    /// <summary>
    /// The story's FIRST Gherkin scenario: "the result's logs array is empty … the result's partial
    /// field is true". Asserted together with the third clause's in-payload half — a <c>gaps</c> entry
    /// naming U4 — since the tool description's half is a wire-level fact
    /// (<c>RealGetRunArtifactsMcpTests</c> holds it).
    /// </summary>
    [Fact]
    public async Task Logs_ComeBackAsAnEmptyArrayWithPartialTrue_RatherThanAnErrorOrFabricatedLines()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Logs));

        Assert.NotNull(result.Logs);
        Assert.Empty(result.Logs);
        Assert.True(result.Partial);

        var gap = Assert.Single(result.Gaps, g => g.Field == "logs");
        Assert.Equal("U4", gap.Awaits);
        Assert.Contains("no container log access", gap.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The complement of the scenario above, and the point of stance (b): asking for logs is a
    /// SUCCESS, never a refusal — a host must be able to call this tool today without special-casing
    /// an error it will stop receiving when U4 lands.
    /// </summary>
    [Fact]
    public async Task AskingForLogs_IsNeverAnError()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, RunArtifactKind.Logs), CancellationToken.None);

        Assert.IsType<GetRunArtifactsOutcome.Found>(outcome);
    }

    // ── Gherkin 2: environment identifiers, without live health ─────────────────────────────────

    /// <summary>
    /// The story's SECOND Gherkin scenario, in the form this server can honestly deliver it: the run
    /// named <c>orders-api</c> and <c>orders-db</c>, both come back with no health populated, and
    /// <c>partial</c> is true.
    /// </summary>
    /// <remarks>
    /// <b>The scenario's premise is adjusted, and the adjustment is the finding.</b> It says "a run
    /// whose REGISTRY ENTRY recorded service ids … and dependency ids …" — <see cref="RunRegistryEntry"/>
    /// records no such thing, and <c>IRunRegistry</c>'s remarks make that a hard boundary rather than a
    /// gap to be filled. The run's <c>environment-error</c> events are the only place either identifier
    /// appears, so that is where they are read from, and neither can be classified as a service or a
    /// dependency from what the event carries. See <c>GetRunArtifactsModels.cs</c>'s header.
    /// </remarks>
    [Fact]
    public async Task Environment_ReportsTheIdentifiersTheRunsEventsNamed_WithNoHealthPopulated()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));

        var environment = result.Environment;
        Assert.NotNull(environment);

        Assert.Equal(TwoFailedResourceIds, environment.Resources.Select(r => r.Id).ToArray());
        Assert.All(environment.Resources, resource =>
        {
            Assert.Null(resource.Health);
            Assert.Equal(RunEnvironmentResourceRole.Unclassified, resource.Role);
            Assert.Equal(RunEnvironmentResourceSource.EnvironmentErrorEvent, resource.Source);
            Assert.Equal(1, resource.Occurrences);
        });

        Assert.Equal("ImagePull", environment.Resources[0].ErrorKind);
        Assert.Equal("pull access denied", environment.Resources[0].Detail);
        Assert.True(result.Partial);
    }

    /// <summary>
    /// The classification refusal, asserted as the property it is: nothing this build produces ever
    /// lands in either spec array, because nothing it reads can tell a service from a dependency.
    /// </summary>
    [Fact]
    public async Task Environment_LeavesBothSpecArraysEmpty_BecauseTheStreamClassifiesNothing()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));
        var environment = result.Environment;
        Assert.NotNull(environment);

        Assert.Empty(environment.Services);
        Assert.Empty(environment.Dependencies);
        Assert.NotEmpty(environment.Resources);

        // And both absences are explained at the field they concern, rather than left to be inferred
        // from two empty arrays.
        Assert.Contains(result.Gaps, g => g.Field == "environment.services" && g.Awaits == "U4");
        Assert.Contains(result.Gaps, g => g.Field == "environment.dependencies" && g.Awaits == "U4");
        Assert.Contains(result.Gaps, g => g.Field == "environment.resources[].health" && g.Awaits == "U4");
    }

    /// <summary>
    /// <b>A run in which nothing went wrong reports NO environment resources</b>, and that is a
    /// successful answer rather than a failure. The measured reason: an environment identifier reaches
    /// this server only on an <c>environment-error</c> event, so a healthy run has none to report.
    /// </summary>
    [Fact]
    public async Task AHealthyRun_ReportsNoEnvironmentResourcesAtAll_AndStillSucceeds()
    {
        var (orchestrator, runId) = Given(
            """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":42}
            {"type":"scenario-completed","scenarioId":"place-order","verdict":"PASS"}
            """);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));
        var environment = result.Environment;
        Assert.NotNull(environment);

        Assert.Empty(environment.Resources);
        Assert.False(environment.Truncated);
        Assert.Equal(0, environment.OmittedResourceCount);
        Assert.True(result.Partial);
    }

    /// <summary>
    /// Repeated failures of one resource fold into ONE entry that counts them and keeps the FIRST
    /// reason — see <c>BuildEnvironment</c> for why first-wins rather than last-wins.
    /// </summary>
    [Fact]
    public async Task ARepeatedlyFailingResource_FoldsIntoOneEntryCountingItsEventsAndKeepingTheFirstReason()
    {
        var (orchestrator, runId) = Given(
            """
            {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-db","detail":"pull access denied"}
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"orders-db","detail":"timed out waiting for healthy"}
            {"type":"environment-error","errorKind":"Provision","resourceName":"orders-db","detail":"container exited unexpectedly"}
            """);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));

        var environment = result.Environment;
        Assert.NotNull(environment);

        var resource = Assert.Single(environment.Resources);
        Assert.Equal("orders-db", resource.Id);
        Assert.Equal(3, resource.Occurrences);
        Assert.Equal("ImagePull", resource.ErrorKind);
        Assert.Equal("pull access denied", resource.Detail);
    }

    // ── Gherkin 3: tailLines is validated even though nothing tails it ──────────────────────────

    /// <summary>
    /// The story's THIRD Gherkin scenario: "the host calls get_run_artifacts with tailLines 10000 …
    /// the VfxError states the maximum tailLines is 5000".
    /// </summary>
    [Fact]
    public async Task TailLinesAboveTheMaximum_IsRefusedWithAMessageNamingTheMaximum()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, TailLines: 10_000), CancellationToken.None);

        var refusal = Assert.IsType<GetRunArtifactsOutcome.InvalidArgument>(outcome);
        Assert.Contains("5000", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5_001)]
    public async Task TailLinesOutsideTheRange_IsRefusedRatherThanClamped(int tailLines)
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, TailLines: tailLines), CancellationToken.None);

        Assert.IsType<GetRunArtifactsOutcome.InvalidArgument>(outcome);
    }

    /// <summary>
    /// The boundary values are ACCEPTED, so the refusal above is a bound rather than a rejection of the
    /// parameter — and the effective value is echoed, which is what makes "validated, not silently
    /// ignored" (AC-003) observable to a host.
    /// </summary>
    [Theory]
    [InlineData(null, GetRunArtifactsOrchestrator.DefaultTailLines)]
    [InlineData(1, 1)]
    [InlineData(GetRunArtifactsOrchestrator.MaxTailLines, GetRunArtifactsOrchestrator.MaxTailLines)]
    public async Task TailLinesInsideTheRange_IsAcceptedAndEchoedAsTheEffectiveValue(int? requested, int expected)
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, TailLines: requested));

        Assert.Equal(expected, result.TailLines);
    }

    // ── container: accepted, validated, echoed, and it selects nothing ──────────────────────────

    [Fact]
    public async Task Container_IsEchoedBackSanitised_AndSelectsNothing()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, Container: "orders-api\u001b[2J"));

        // Escaped on the way out, exactly as every other caller-supplied echo in this server is.
        Assert.Equal("orders-api\\u001b[2J", result.Container);

        // And it changed nothing about what came back: the logs list is empty whatever container was
        // named, which is the honest shape until U4 lands.
        Assert.NotNull(result.Logs);
        Assert.Empty(result.Logs);
    }

    [Fact]
    public async Task AnOverLongContainer_IsRefused()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, Container: new string('c', GetRunArtifactsOrchestrator.MaxContainerChars + 1)),
            CancellationToken.None);

        Assert.IsType<GetRunArtifactsOutcome.InvalidArgument>(outcome);
    }

    [Fact]
    public async Task NoContainer_EchoesNull()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId));

        Assert.Null(result.Container);
    }

    // ── kind: the section selector ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An unselected section is OMITTED rather than emptied, so a host can tell "you did not ask for
    /// this" from "there is nothing here" — which matters precisely because two of the three sections
    /// are legitimately empty today.
    /// </summary>
    [Theory]
    [InlineData(RunArtifactKind.Reports, true, false, false)]
    [InlineData(RunArtifactKind.Logs, false, true, false)]
    [InlineData(RunArtifactKind.Environment, false, false, true)]
    [InlineData(RunArtifactKind.All, true, true, true)]
    [InlineData(null, true, true, true)]
    public async Task Kind_SelectsExactlyTheSectionsItNames(
        string? kind, bool reports, bool logs, bool environment)
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, kind));

        Assert.Equal(reports, result.Reports is not null);
        Assert.Equal(logs, result.Logs is not null);
        Assert.Equal(environment, result.Environment is not null);
        Assert.Equal(kind ?? RunArtifactKind.All, result.Kind);
    }

    /// <summary>
    /// The vocabulary is this server's own closed set, so it is matched leniently and echoed
    /// canonically — see <c>ValidateArguments</c> for why that differs from <c>get_run_events</c>'
    /// exact matching of engine-decided event types.
    /// </summary>
    [Theory]
    [InlineData("REPORTS")]
    [InlineData(" reports ")]
    [InlineData("Reports")]
    public async Task Kind_IsMatchedCaseInsensitivelyAndEchoedCanonically(string kind)
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, kind));

        Assert.Equal(RunArtifactKind.Reports, result.Kind);
    }

    /// <summary>A blank carries no intent to misread, so it means "omitted" rather than "error".</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankKind_IsTreatedAsOmitted(string kind)
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, kind));

        Assert.Equal(RunArtifactKind.All, result.Kind);
    }

    [Fact]
    public async Task AnUnknownKind_IsRefusedWithABoundedEchoAndTheAcceptedValues()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, "screenshots\u001b[2J"), CancellationToken.None);

        var refusal = Assert.IsType<GetRunArtifactsOutcome.InvalidArgument>(outcome);
        Assert.Contains("'environment'", refusal.Message, StringComparison.Ordinal);

        // Echoed, and escaped on the way into the message — the same rule every caller-supplied value
        // in this server follows.
        Assert.Contains("screenshots\\u001b", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsurdlyLongKind_IsRefusedBeforeItIsEchoed()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, new string('k', 100_000)), CancellationToken.None);

        var refusal = Assert.IsType<GetRunArtifactsOutcome.InvalidArgument>(outcome);
        Assert.True(refusal.Message.Length < 500);
    }

    // ── runId: the shared rules ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingRunId_IsRefused(string? runId)
    {
        var (orchestrator, _) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(new GetRunArtifactsRequest(runId), CancellationToken.None);

        Assert.IsType<GetRunArtifactsOutcome.InvalidArgument>(outcome);
    }

    [Fact]
    public async Task AnUnknownRunId_IsRunNotFound_WithTheSharedWording()
    {
        var (orchestrator, _) = Given(TwoFailedResources);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest("run-0000000000000000000000000000cafe"), CancellationToken.None);

        var notFound = Assert.IsType<GetRunArtifactsOutcome.RunNotFound>(outcome);
        Assert.Equal(
            RunIdArgument.DescribeMissingRun("run-0000000000000000000000000000cafe"), notFound.Message);
    }

    /// <summary>
    /// A registry-supplied UNC events path is refused, exactly as it is for
    /// <c>get_run_events</c>/<c>get_step_timeline</c> — <b>no exemption for a path this server itself
    /// recorded</b>, and specifically no exemption from this tool's stance (b), which softens a swept
    /// or unreadable file into a gap but never softens a containment failure.
    /// </summary>
    [Fact]
    public async Task AUncEventsPath_IsRejectedByTheSharedPathGuard()
    {
        var registry = new StubRunRegistry();
        var runId = registry.AddCompletedRun(@"\\attacker-host\share\events.jsonl").RunId;
        var orchestrator = new GetRunArtifactsOrchestrator(registry);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId), CancellationToken.None);

        Assert.IsType<GetRunArtifactsOutcome.InvalidPath>(outcome);
    }

    /// <summary>
    /// And the check is UNCONDITIONAL: <c>kind: "logs"</c> reads no file at all, and is still refused.
    /// Fail-closed on purpose — see <c>GetRunArtifactsOrchestrator.GetAsync</c>, where a conditional
    /// check would have made the section selector a probe for whether a run's path passes containment.
    /// </summary>
    [Fact]
    public async Task AUncEventsPath_IsRejectedEvenForASectionThatReadsNothing()
    {
        var registry = new StubRunRegistry();
        var runId = registry.AddCompletedRun(@"\\attacker-host\share\events.jsonl").RunId;
        var orchestrator = new GetRunArtifactsOrchestrator(registry);

        var outcome = await orchestrator.GetAsync(
            new GetRunArtifactsRequest(runId, RunArtifactKind.Logs), CancellationToken.None);

        Assert.IsType<GetRunArtifactsOutcome.InvalidPath>(outcome);
    }

    // ── reports: the one artefact this build really has ─────────────────────────────────────────

    [Fact]
    public async Task Reports_PointAtTheRunsOwnEventStream_AndOmitTheEngineWrittenReports()
    {
        var (orchestrator, runId, eventsPath) = GivenWithPath(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Reports));
        var reports = result.Reports;
        Assert.NotNull(reports);

        Assert.Equal(eventsPath, reports.Events.Path);
        Assert.True(reports.Events.Available);

        // No run-artefact resource family exists yet, so nothing invents a URI that resolves to
        // nothing.
        Assert.Null(reports.Events.ResourceUri);

        Assert.Null(reports.Html);
        Assert.Null(reports.Junit);
        Assert.Contains(result.Gaps, g => g.Field == "reports.html" && g.Awaits == "U4");
        Assert.Contains(result.Gaps, g => g.Field == "reports.junit" && g.Awaits == "U4");
    }

    /// <summary>
    /// The stance-(b) divergence from this tool's siblings, pinned: a swept events file is REPORTED
    /// here, where <c>get_step_timeline</c>/<c>get_run_events</c> answer <c>VFX-E-1004</c> over the
    /// same condition. See <see cref="GetRunArtifactsOutcome"/>'s remarks for why the two differ.
    /// </summary>
    [Fact]
    public async Task ASweptEventsFile_IsReportedAsAnUnavailableArtefact_NotRefused()
    {
        var (orchestrator, runId, eventsPath) = GivenWithPath(TwoFailedResources);
        File.Delete(eventsPath);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId));

        var reports = result.Reports;
        Assert.NotNull(reports);
        Assert.False(reports.Events.Available);
        Assert.Equal(eventsPath, reports.Events.Path);

        // The gap says so at both fields it affects, and neither names an upstream ask: nothing
        // upstream would bring a deleted file back.
        var reportsGap = Assert.Single(result.Gaps, g => g.Field == "reports.events");
        Assert.Null(reportsGap.Awaits);
        var environmentGap = Assert.Single(result.Gaps, g => g.Field == "environment.resources");
        Assert.Null(environmentGap.Awaits);

        // And the other sections still answered.
        Assert.NotNull(result.Logs);
        Assert.Empty(result.Logs);
        Assert.NotNull(result.Environment);
        Assert.Empty(result.Environment.Resources);
        Assert.True(result.Partial);
    }

    /// <summary>
    /// The branch between the two the story's scenarios cover: the events file EXISTS and cannot be
    /// read. It is reported like a swept one — a gap with no upstream ask, and the other sections still
    /// answering — rather than refused, but with its own sentence, because "gone" and "there and
    /// unreadable" are different things to go and check.
    /// </summary>
    /// <remarks>
    /// <b>Arranged differently per platform, because "unreadable" is not one mechanism.</b> On Windows
    /// an exclusive share-mode handle is what denies the read; elsewhere it is the file mode. Both
    /// leave <see cref="File.Exists(string)"/> true, which is the precondition the branch needs — the
    /// missing-file branch is a different one, already covered by
    /// <see cref="ASweptEventsFile_IsReportedAsAnUnavailableArtefact_NotRefused"/>. Note that running
    /// the suite as root on a POSIX host defeats the mode arrangement; the assertion below says so.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableEventsFile_IsReportedAsAGapWithItsOwnReason_NotRefused()
    {
        var (orchestrator, runId, eventsPath) = GivenWithPath(TwoFailedResources);

        using FileStream? exclusive = OperatingSystem.IsWindows()
            ? new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None)
            : null;

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(eventsPath, UnixFileMode.None);
        }

        // The arrangement is verified through the production reader before anything is asserted about
        // the orchestrator: if this fails, the test environment (a root POSIX session, say) could not
        // make the file unreadable, and every assertion below would be measuring the wrong branch.
        var (content, _) = await EventsFileReader.TryReadBoundedAsync(eventsPath, CancellationToken.None);
        Assert.True(
            content is null,
            "Could not arrange an unreadable-but-present events file, so this test is not exercising "
            + "the branch it names. On a POSIX host, running the suite as root defeats the file-mode "
            + "arrangement.");

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId));

        // Still a SUCCESS, and still stance (b): the file is one input of three.
        var reports = result.Reports;
        Assert.NotNull(reports);
        Assert.True(reports.Events.Available, "The file is present — only the READ failed.");

        var gap = Assert.Single(result.Gaps, g => g.Field == "environment.resources");
        Assert.Null(gap.Awaits);
        Assert.Contains("could not be read", gap.Reason, StringComparison.Ordinal);
        Assert.Contains("between the check and the read", gap.Reason, StringComparison.Ordinal);

        // And the sections still answered, with no fabricated resource behind the failed read.
        Assert.NotNull(result.Environment);
        Assert.Empty(result.Environment.Resources);
        Assert.NotNull(result.Logs);
        Assert.True(result.Partial);
    }

    // ── partial: computed, not hardcoded ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>partial</c> is the gap list's own emptiness, which is what makes it stance (b)'s varying
    /// marker rather than a constant. Every section carries at least one gated gap today, so it is true
    /// for every call — asserted here across all four <c>kind</c> values so a future change that
    /// populates one section cannot leave a stale <c>true</c> behind.
    /// </summary>
    [Theory]
    [InlineData(RunArtifactKind.Reports)]
    [InlineData(RunArtifactKind.Logs)]
    [InlineData(RunArtifactKind.Environment)]
    [InlineData(RunArtifactKind.All)]
    public async Task Partial_IsTrueExactlyWhenThereAreGaps(string kind)
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId, kind));

        Assert.Equal(result.Gaps.Count > 0, result.Partial);
        Assert.True(result.Partial, "Every section is still U4-gated in this build.");
    }

    /// <summary>Every gap names the field it concerns, so a host never has to read prose to find it.</summary>
    [Fact]
    public async Task EveryGap_NamesAFieldAndAReason()
    {
        var (orchestrator, runId) = Given(TwoFailedResources);

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId));

        Assert.All(result.Gaps, gap =>
        {
            Assert.False(string.IsNullOrWhiteSpace(gap.Field));
            Assert.False(string.IsNullOrWhiteSpace(gap.Reason));
        });

        // No duplicates: one field, one explanation.
        Assert.Equal(result.Gaps.Select(g => g.Field).Distinct(StringComparer.Ordinal).Count(), result.Gaps.Count);
    }

    // ── The response is BOUNDED, not merely usually short ───────────────────────────────────────

    /// <summary>
    /// The cap on distinct resources, and its honesty: what it drops is counted, and both
    /// <c>truncated</c> and <c>omittedResourceCount</c> say so.
    /// </summary>
    [Fact]
    public async Task MoreDistinctResourcesThanTheCap_AreTruncatedAndCounted()
    {
        const int distinct = GetRunArtifactsOrchestrator.MaxEnvironmentResources + 17;
        var (orchestrator, runId) = Given(EnvironmentErrors(distinct, idChars: 12, detailChars: 40));

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));
        var environment = result.Environment;
        Assert.NotNull(environment);

        Assert.Equal(GetRunArtifactsOrchestrator.MaxEnvironmentResources, environment.Resources.Count);
        Assert.Equal(17, environment.OmittedResourceCount);
        Assert.True(environment.Truncated);

        // Kept in first-appearance order, so the resources that failed first are the ones reported.
        Assert.StartsWith("res-0000", environment.Resources[0].Id, StringComparison.Ordinal);
        Assert.StartsWith(
            $"res-{GetRunArtifactsOrchestrator.MaxEnvironmentResources - 1:0000}",
            environment.Resources[^1].Id,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The response bound, MEASURED — and measured against the encoder the WIRE uses, which is the
    /// whole point of this test's existence in its present form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fixture's filler was wrong, and the wrongness hid a real 1.98x overrun</b> (a security
    /// review's MAJOR finding). It used <c>é</c> on the theory that a non-ASCII character is the
    /// expensive case — but <see cref="SuiteEventParser"/> sanitises that to the printable ASCII text
    /// <c>é</c> BEFORE the orchestrator's caps count characters, so the cap kept 128 characters
    /// of already-cheap text and the payload measured comfortably small. The genuinely expensive
    /// character is one <see cref="TextSanitiser"/> passes through untouched and
    /// <c>JavaScriptEncoder.Default</c> — which <c>StructuredToolResult.Options</c> and every size
    /// probe in this server carry — still escapes to six bytes: <c>+</c>, <c>&lt;</c>, <c>&gt;</c>,
    /// <c>&amp;</c> or <c>"</c>. With <c>+</c> as the filler this same fixture measured 64,830 B
    /// against a 32,768 B budget.
    /// </para>
    /// <para>
    /// So the fixture is now pathological in every dimension AT ONCE and in the dimension that
    /// actually costs: far more distinct resources than the cap allows, each with an <c>id</c>,
    /// <c>errorKind</c> and <c>detail</c> past their caps and made entirely of a 6x-escaping printable
    /// character, plus a <c>container</c> echo of 256 ESC bytes (each of which sanitises to six
    /// characters before the second cap). What holds the bound is no longer arithmetic but
    /// <c>GetRunArtifactsOrchestrator.FitWithinBudget</c>'s measured shed, and the assertions below
    /// pin that it sheds HONESTLY rather than merely quietly.
    /// </para>
    /// <para>
    /// MEASURED with this fixture: unshed 64,830 B; shed to 12 resources at 31,747 B with
    /// <c>omittedResourceCount: 188</c>. The assertions deliberately pin the PROPERTIES rather than
    /// those figures — a cap change should move the count without failing this test, while losing the
    /// bound or losing the report of what went should fail it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheWorstCasePayload_IsShedUntilItFitsTheBudget_AndSaysWhatItShed()
    {
        const int distinct = 200;
        var (orchestrator, runId) = Given(
            EnvironmentErrors(count: distinct, idChars: 400, detailChars: 3_000, wireEscaping: true));

        var result = await FoundAsync(
            orchestrator,
            new GetRunArtifactsRequest(
                runId,
                Container: new string('\u001b', GetRunArtifactsOrchestrator.MaxContainerChars),
                TailLines: GetRunArtifactsOrchestrator.MaxTailLines));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

        Assert.True(
            bytes <= GetRunArtifactsOrchestrator.EffectiveArtifactsBudgetBytes,
            $"The worst-case artifacts payload serialised to {bytes} B, over the "
            + $"{GetRunArtifactsOrchestrator.EffectiveArtifactsBudgetBytes} B budget. "
            + "GetRunArtifactsOrchestrator.FitWithinBudget is supposed to make that impossible — it "
            + "either stopped shedding, or something outside the resource list now dominates the "
            + "payload.");

        // Non-vacuity, in both directions. A payload that came back with no resources at all would
        // satisfy the bound while proving nothing about the shed; one that kept all 25 would mean the
        // fixture had stopped being worst-case.
        Assert.NotNull(result.Environment);
        Assert.NotEmpty(result.Environment.Resources);
        Assert.True(
            result.Environment.Resources.Count < GetRunArtifactsOrchestrator.MaxEnvironmentResources,
            $"All {result.Environment.Resources.Count} resources survived, so this test is no longer "
            + "measuring the shed. Check that the filler still escapes on the wire.");

        // The shed is REPORTED, and reported against the distinct total the parse saw — not against
        // whatever survived MaxEnvironmentResources first.
        Assert.True(result.Environment.Truncated);
        Assert.Equal(
            distinct, result.Environment.Resources.Count + result.Environment.OmittedResourceCount);
    }

    /// <summary>
    /// The shed is not merely a property of the pathological fixture: a run of ordinary SIZE whose
    /// text happens to be made of wire-escaped characters is bounded too.
    /// </summary>
    [Fact]
    public async Task ARunWhoseTextEscapesOnTheWire_IsStillWithinTheBudget()
    {
        var (orchestrator, runId) = Given(EnvironmentErrors(
            count: GetRunArtifactsOrchestrator.MaxEnvironmentResources,
            idChars: GetRunArtifactsOrchestrator.MaxResourceIdChars,
            detailChars: GetRunArtifactsOrchestrator.MaxDetailChars,
            wireEscaping: true));

        var result = await FoundAsync(orchestrator, new GetRunArtifactsRequest(runId));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;
        Assert.True(
            bytes <= GetRunArtifactsOrchestrator.EffectiveArtifactsBudgetBytes,
            $"Serialised to {bytes} B, over the "
            + $"{GetRunArtifactsOrchestrator.EffectiveArtifactsBudgetBytes} B budget.");
    }

    /// <summary>Every per-field cap actually bites, so the arithmetic above rests on enforced figures.</summary>
    [Fact]
    public async Task EveryPerFieldCap_IsApplied()
    {
        var (orchestrator, runId) = Given(EnvironmentErrors(count: 1, idChars: 4_000, detailChars: 4_000));

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));

        var environment = result.Environment;
        Assert.NotNull(environment);

        var resource = Assert.Single(environment.Resources);
        Assert.Equal(GetRunArtifactsOrchestrator.MaxResourceIdChars, resource.Id!.Length);
        Assert.Equal(GetRunArtifactsOrchestrator.MaxDetailChars, resource.Detail!.Length);
    }

    /// <summary>
    /// <b>Two distinct resources that share a long prefix stay two resources.</b> The fold is keyed on
    /// the id the shared parser produced, and the display cap is applied afterwards — the reverse order
    /// (which is what shipped) merged them into one entry with a doubled occurrence count, a wrong
    /// derived fact that the output gave no sign of.
    /// </summary>
    [Fact]
    public async Task TwoResourcesSharingTheirCappedPrefix_AreNotSilentlyMergedIntoOne()
    {
        var prefix = new string('p', GetRunArtifactsOrchestrator.MaxResourceIdChars + 20);
        var (orchestrator, runId) = Given(
            $$"""
            {"type":"environment-error","errorKind":"ImagePull","resourceName":"{{prefix}}-alpha","detail":"first"}
            {"type":"environment-error","errorKind":"Provision","resourceName":"{{prefix}}-beta","detail":"second"}
            """);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));

        var environment = result.Environment;
        Assert.NotNull(environment);

        Assert.Equal(2, environment.Resources.Count);
        Assert.All(environment.Resources, resource =>
        {
            // Each is still CAPPED for display — the fix moves where the cap applies, not whether it
            // does — so the two ids are identical on the wire and only `occurrences` would have
            // betrayed the merge. That is exactly why the count is asserted.
            Assert.Equal(GetRunArtifactsOrchestrator.MaxResourceIdChars, resource.Id!.Length);
            Assert.Equal(1, resource.Occurrences);
        });

        Assert.Equal("ImagePull", environment.Resources[0].ErrorKind);
        Assert.Equal("Provision", environment.Resources[1].ErrorKind);
    }

    /// <summary>
    /// An <c>environment-error</c> event that names no resource gets a NULL id and its own
    /// <c>source</c> — never the parser's <c>(unknown)</c> placeholder dressed up as an engine-reported
    /// identity (see <see cref="RunEnvironmentResourceSource.UnnamedEnvironmentErrorEvent"/>).
    /// </summary>
    [Fact]
    public async Task AnEventThatNamesNoResource_ReportsANullIdAndItsOwnSource_KeepingTheOccurrenceCount()
    {
        var (orchestrator, runId) = Given(
            """
            {"type":"environment-error","errorKind":"Provision","detail":"the topology did not start"}
            {"type":"environment-error","errorKind":"HealthGate","detail":"still not up"}
            {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-db","detail":"pull access denied"}
            """);

        var result = await FoundAsync(
            orchestrator, new GetRunArtifactsRequest(runId, RunArtifactKind.Environment));

        var environment = result.Environment;
        Assert.NotNull(environment);
        Assert.Equal(2, environment.Resources.Count);

        var unnamed = environment.Resources[0];
        Assert.Null(unnamed.Id);
        Assert.Equal(RunEnvironmentResourceSource.UnnamedEnvironmentErrorEvent, unnamed.Source);

        // Folded, and the count survives: two failures that named nothing are still two failures.
        Assert.Equal(2, unnamed.Occurrences);
        Assert.Equal("Provision", unnamed.ErrorKind);

        // The named one is untouched by any of that.
        Assert.Equal("orders-db", environment.Resources[1].Id);
        Assert.Equal(RunEnvironmentResourceSource.EnvironmentErrorEvent, environment.Resources[1].Source);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>An <c>environment-error</c> line per distinct resource, sized to order.</summary>
    /// <param name="wireEscaping">
    /// When true every character of every field is <c>+</c> — a PRINTABLE ASCII character that
    /// <see cref="TextSanitiser"/> passes through untouched (so the orchestrator's caps count it at
    /// face value) and that <c>JavaScriptEncoder.Default</c> then escapes to the six bytes
    /// <c>+</c> on the wire. That 6x is the real worst case; the <c>é</c> this parameter used to
    /// select was not, because the parser had already flattened it to printable ASCII before any cap
    /// ran. See the budget test's remarks.
    /// </param>
    private static string EnvironmentErrors(int count, int idChars, int detailChars, bool wireEscaping = false)
    {
        var filler = wireEscaping ? '+' : 'x';

        var lines = Enumerable.Range(0, count).Select(n =>
        {
            var id = $"res-{n:0000}" + new string(filler, Math.Max(0, idChars - 8));
            var detail = new string(filler, detailChars);
            var kind = new string(filler, 200);

            return $$"""{"type":"environment-error","errorKind":"{{kind}}","resourceName":"{{id}}","detail":"{{detail}}"}""";
        });

        return string.Join('\n', lines);
    }

    private (GetRunArtifactsOrchestrator Orchestrator, string RunId) Given(string eventsFileContent)
    {
        var (orchestrator, runId, _) = GivenWithPath(eventsFileContent);
        return (orchestrator, runId);
    }

    private (GetRunArtifactsOrchestrator Orchestrator, string RunId, string EventsPath) GivenWithPath(
        string eventsFileContent)
    {
        var path = WriteEvents(eventsFileContent);
        var registry = StubRunRegistry.WithCompletedRun(path);
        return (new GetRunArtifactsOrchestrator(registry), registry.ListRuns()[0].RunId, path);
    }

    private string WriteEvents(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"get-run-artifacts-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<GetRunArtifactsResult> FoundAsync(
        GetRunArtifactsOrchestrator orchestrator, GetRunArtifactsRequest request)
    {
        var outcome = await orchestrator.GetAsync(request, CancellationToken.None);
        return Assert.IsType<GetRunArtifactsOutcome.Found>(outcome).Result;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A temp file this test could not remove is not a test failure.
            }
        }

        GC.SuppressFinalize(this);
    }
}
