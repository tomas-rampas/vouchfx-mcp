using Vouchfx.Mcp.Specs;

namespace Vouchfx.Mcp.Tests.Specs;

/// <summary>
/// US-S5-01 AC-005: <c>vouchfx://workspace/specs</c> indexes the <c>.e2e.yaml</c> suites under the
/// workspace's <c>specsDir</c> — and nothing outside it.
/// </summary>
/// <remarks>
/// <para>
/// The containment scenario is the one that matters and is asserted twice, from opposite directions:
/// a sibling suite in <c>../secrets/</c> never appears (US-S5-01's own Gherkin), and — where the
/// platform lets the test create one — neither does a suite reached through a symlink planted inside
/// <c>e2e/</c>. That second case is what distinguishes real containment from "we only enumerated one
/// directory": <c>Directory.EnumerateFiles</c> with <c>RecurseSubdirectories</c> follows a directory
/// link by default, so the index would otherwise leave the workspace without any <c>..</c> ever being
/// written down.
/// </para>
/// <para>
/// These tests write into their own temp directories, which is the established pattern for a test
/// that needs a real filesystem in this repository (<c>RealWorkspaceContainmentMcpTests</c> and
/// <c>RealRunRegistryMcpTests</c> both do it). The read-only invariant is about the SERVER never
/// writing, and <c>ReadOnlySourceGuardTests</c> scans <c>src/</c> only.
/// </para>
/// </remarks>
public class WorkspaceSpecIndexerTests : IDisposable
{
    private readonly DirectoryInfo _root =
        Directory.CreateTempSubdirectory("vfx-mcp-spec-index-");

    private string SpecsDir => Path.Combine(_root.FullName, Workspace.SpecsDirectoryName);

    private Workspace ResolvedWorkspace => Workspace.Resolve(_root.FullName);

    /// <summary>
    /// The budget every HEALTHY-path test builds under: generous enough that no clock can fire, so
    /// these tests assert what the indexer produces rather than what this machine's load allowed.
    /// </summary>
    /// <remarks>
    /// <b>This is the fix for a measured flake, not belt-and-braces.</b> These tests previously ran
    /// against the production budget while the rest of the suite ran in parallel — validation workers,
    /// real spawned-server tests, and this class's own spin tests all competing — and a spawn that
    /// lost its scheduling slice made a HEALTHY suite come back <c>readable: false</c>. Two full-suite
    /// runs failed on two DIFFERENT tests for that one reason. A healthy-path test must never be able
    /// to fail because the machine was busy; the clocks are the spin test's subject, and only its
    /// subject.
    /// </remarks>
    private static readonly SpecIndexWorkerBudget GenerousBudget = new(
        Startup: TimeSpan.FromMinutes(2),
        Stall: TimeSpan.FromMinutes(2),
        Total: TimeSpan.FromMinutes(5));

    /// <summary>
    /// The budget the SPIN test builds under: a real (non-trivial) start-up allowance so a loaded
    /// machine still gets its worker running, and a SHORT stall clock so the hostile file is convicted
    /// in seconds rather than tens of them.
    /// </summary>
    /// <remarks>
    /// The separation is exactly the production one — see <c>SpecIndexWorkerBudget</c> — and this is
    /// what makes shortening it safe: the number being shortened measures a file's parse, not the
    /// machine's start-up cost, so a busy CI runner does not make a healthy file look hostile. It also
    /// takes this invariant's cost from roughly 25 s per run to about 4 s, which matters because the
    /// contention those 25 s created is what the flake was made of.
    /// </remarks>
    private static readonly SpecIndexWorkerBudget FastSpinBudget = new(
        Startup: TimeSpan.FromSeconds(60),
        Stall: TimeSpan.FromSeconds(3),
        Total: TimeSpan.FromSeconds(90));

    private Task<WorkspaceSpecIndex> BuildAsync(CancellationToken cancellationToken = default) =>
        WorkspaceSpecIndexer.BuildAsync(ResolvedWorkspace, GenerousBudget, cancellationToken);

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must never fail the test that produced it.
        }

        GC.SuppressFinalize(this);
    }

    // ── The "no workspace" state is REPORTED, not an error and not an empty list ────────────────

    [Fact]
    public async Task NoWorkspaceConfigured_ReportsWhyRatherThanReturningAnAmbiguousEmptyList()
    {
        var index = await WorkspaceSpecIndexer.BuildAsync(workspace: null);

        Assert.False(index.WorkspaceConfigured);
        Assert.Null(index.SpecsDir);
        Assert.Empty(index.Specs);
        Assert.False(index.Truncated);
        Assert.Equal(WorkspaceSpecIndexReasons.NoWorkspaceConfigured, index.Reason);

        // Actionable: it names the flag that would populate it. A host that reads only `specs: []`
        // would otherwise conclude the directory is empty, which is a different situation with a
        // different next action.
        Assert.Contains("--workspace", index.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingSpecsDir_IsDistinguishableFromAnEmptyOne()
    {
        // The workspace resolves fine; e2e/ has simply never been created.
        var index = await BuildAsync();

        Assert.True(index.WorkspaceConfigured);
        Assert.NotNull(index.SpecsDir);
        Assert.Empty(index.Specs);
        Assert.Equal(WorkspaceSpecIndexReasons.SpecsDirMissing, index.Reason);
    }

    [Fact]
    public async Task AnEmptySpecsDir_IsAnOrdinaryCompleteAnswerWithNoReason()
    {
        Directory.CreateDirectory(SpecsDir);

        var index = await BuildAsync();

        Assert.True(index.WorkspaceConfigured);
        Assert.Empty(index.Specs);
        Assert.False(index.Truncated);

        // The discriminator that makes `specs: []` unambiguous: no reason means "the directory is
        // genuinely empty", which is exactly what the three reason literals exist to distinguish from.
        Assert.Null(index.Reason);
        Assert.Null(index.Detail);
    }

    // ── The index itself ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASuite_IsIndexedWithItsNameTagsStepTypesAndCount()
    {
        WriteSpec("orders/place-order.e2e.yaml", GoodSuiteYaml);

        var index = await BuildAsync();

        var entry = Assert.Single(index.Specs);
        Assert.Equal("orders/place-order.e2e.yaml", entry.Path);
        Assert.Equal("Orders API health and persistence smoke test", entry.Name);
        Assert.Equal(["smoke"], entry.Tags);
        Assert.Equal(["http.rest", "db-assert.postgres"], entry.StepTypes);
        Assert.Equal(2, entry.Steps);
        // Message carries ParseError: a bare Assert.True here once failed in CI with no diagnosis at
        // all, which cost a whole investigation round to reproduce. Never assert readability silently.
        Assert.True(entry.Readable, $"Expected a readable entry; ParseError was: {entry.ParseError}");
        Assert.Null(entry.ParseError);
    }

    [Fact]
    public async Task APathIsRelativeToSpecsDirAndUsesForwardSlashesOnEveryPlatform()
    {
        WriteSpec(Path.Combine("deeply", "nested", "flow.e2e.yaml"), GoodSuiteYaml);

        var entry = Assert.Single((await BuildAsync()).Specs);

        Assert.Equal("deeply/nested/flow.e2e.yaml", entry.Path);
        Assert.DoesNotContain('\\', entry.Path);

        // Relative, so the list never repeats the machine's directory layout per entry — the host
        // composes an absolute path from specsDir, which the payload carries once.
        Assert.False(Path.IsPathRooted(entry.Path));
    }

    [Fact]
    public async Task SuitesAreOrderedDeterministically_SoTwoReadsOfAnUnchangedDirectoryAgree()
    {
        WriteSpec("zulu.e2e.yaml", GoodSuiteYaml);
        WriteSpec("alpha.e2e.yaml", GoodSuiteYaml);
        WriteSpec("mike.e2e.yaml", GoodSuiteYaml);

        var first = await BuildAsync();
        var second = await BuildAsync();

        Assert.Equal(
            ["alpha.e2e.yaml", "mike.e2e.yaml", "zulu.e2e.yaml"],
            first.Specs.Select(entry => entry.Path));

        // The cacheability property: same directory, same bytes.
        Assert.Equal(
            first.Specs.Select(entry => entry.Path),
            second.Specs.Select(entry => entry.Path));
    }

    [Fact]
    public async Task ANonSuiteFile_IsNotIndexed()
    {
        WriteSpec("notes.md", "# not a suite");
        WriteSpec("config.yaml", "not: a suite");
        WriteSpec("real.e2e.yaml", GoodSuiteYaml);

        var entry = Assert.Single((await BuildAsync()).Specs);
        Assert.Equal("real.e2e.yaml", entry.Path);
    }

    [Fact]
    public async Task ASuiteWithNoMetadata_IsIndexedWithANullNameAndNoTags()
    {
        WriteSpec("bare.e2e.yaml", "steps:\n  - id: only\n    type: http.rest\n    target: api\n    method: GET\n    path: /x\n");

        var entry = Assert.Single((await BuildAsync()).Specs);

        // metadata is optional in the schema, so absent is an ordinary state rather than a defect.
        Assert.Null(entry.Name);
        Assert.Empty(entry.Tags);
        // Message carries ParseError: a bare Assert.True here once failed in CI with no diagnosis at
        // all, which cost a whole investigation round to reproduce. Never assert readability silently.
        Assert.True(entry.Readable, $"Expected a readable entry; ParseError was: {entry.ParseError}");
    }

    [Fact]
    public async Task AnUnparseableSuite_IsStillListed_MarkedUnreadableWithOneReason()
    {
        WriteSpec("broken.e2e.yaml", "steps:\n  - id: x\n   type: bad-indent\n\t\tnope:\n");

        var entry = Assert.Single((await BuildAsync()).Specs);

        // Listed, not hidden: a suite that will not parse is exactly the one worth knowing about
        // before authoring a second covering the same flow.
        Assert.Equal("broken.e2e.yaml", entry.Path);
        Assert.False(entry.Readable);
        Assert.False(string.IsNullOrWhiteSpace(entry.ParseError));

        // One sentence, not a diagnostic set — this resource is not a bulk validate_suite.
        Assert.Null(entry.Name);
        Assert.Empty(entry.StepTypes);
    }

    // ── B1: the Scanner-spin regression. THE reason this resource parses out-of-process ─────────

    /// <summary>
    /// The exact input <c>ValidationWorkerClient</c>'s remarks name as the uninterruptible YamlDotNet
    /// Scanner spin: a scalar <c>a: b</c> immediately followed by a MORE-indented <c>a: b</c>.
    /// </summary>
    /// <remarks>
    /// Twelve bytes. In-process — which is what the first version of this indexer did —
    /// <c>YamlSafetyGuard.Check</c> on this text does not return (measured against the Release build:
    /// still running at 12 seconds), and no <see cref="CancellationToken"/> can stop it because the
    /// Scanner's loop has no cooperative cancellation point. The guard is not a defence here: its
    /// nesting check IS the Scanner. Only killing a process stops it.
    /// </remarks>
    private const string ScannerSpinYaml = "a: b\n  a: b\n";

    /// <summary>
    /// ONE test for the whole invariant — it returns, the hostile file is convicted alone, and the
    /// suites on BOTH sides of it parse.
    /// </summary>
    /// <remarks>
    /// <b>Collapsed from two tests deliberately.</b> The pair cost roughly 25 seconds of wall clock
    /// per run between them, each burning a real timeout — and that contention was itself part of what
    /// made the rest of the suite flaky, which is a poor trade for one invariant. With the stall clock
    /// separated from the start-up clock, a short injected stall budget convicts the hostile file in
    /// about three seconds without weakening anything: the file after it can only appear if a fresh
    /// worker was started, and the file before it can only appear if partial output survived the kill,
    /// so the merged test still exercises every path the pair did.
    /// </remarks>
    [Fact]
    public async Task AScannerSpinningSuite_DoesNotWedgeTheIndex_AndTheSuitesAroundItStillParse()
    {
        // Named to sort on BOTH sides of the hostile file. 'aaa' proves partial output survives the
        // kill; 'zzz' proves the resume — it can only appear if a fresh worker started after it.
        WriteSpec("aaa-before.e2e.yaml", GoodSuiteYaml);
        WriteSpec("spin.e2e.yaml", ScannerSpinYaml);
        WriteSpec("zzz-after.e2e.yaml", GoodSuiteYaml);

        // A generous but finite bound on the TEST, independent of the injected budget: if the fix
        // regressed, this fails by timing out here rather than hanging the suite forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var index = await WorkspaceSpecIndexer.BuildAsync(ResolvedWorkspace, FastSpinBudget, cts.Token);

        // The narrowest statement of the invariant, first: the resource RETURNS AT ALL. Before the
        // process boundary existed, this call never came back.
        Assert.Equal(3, index.Specs.Count);

        foreach (var path in new[] { "aaa-before.e2e.yaml", "zzz-after.e2e.yaml" })
        {
            var healthy = Assert.Single(index.Specs, entry => entry.Path == path);
            Assert.True(healthy.Readable, $"{path} should have parsed: {healthy.ParseError}");
            Assert.Equal("Orders API health and persistence smoke test", healthy.Name);
        }

        // The hostile one is a DEGRADED ENTRY, not a missing row and not an error response.
        var spun = Assert.Single(index.Specs, entry => entry.Path == "spin.e2e.yaml");
        Assert.False(spun.Readable);
        Assert.Equal(SpecIndexWorkerClient.StalledEntryDetail, spun.ParseError);

        // The message does NOT assert a spin as fact — this server cannot tell a spin from a loaded
        // machine, and saying otherwise would put a false accusation in front of an operator whose
        // suite is fine. It names both, and hands them the tool that can decide.
        Assert.Contains("heavily loaded", spun.ParseError!, StringComparison.Ordinal);
        Assert.Contains("validate_suite", spun.ParseError!, StringComparison.Ordinal);

        // The whole list is still an ordinary, complete answer — a suite that would not parse is not
        // a truncation of the index, and it is not an environmental failure either.
        Assert.False(index.Truncated);
        Assert.Null(index.Reason);
    }

    /// <summary>
    /// A suite the build never got to is reported WITHOUT being blamed for it.
    /// </summary>
    /// <remarks>
    /// <b>Driven by exhausting the build budget, which is the one non-parse failure path this test
    /// can reach deterministically.</b> The other two — <see cref="System.Diagnostics.Process"/>
    /// failing to start, and a worker that never produces output — need either a machine that cannot
    /// spawn a process or an injected process factory this class does not have. An earlier version of
    /// this test tried to force the second with a 1 ms start-up allowance and FAILED, for a reason
    /// worth recording: the watchdog polls at 200 ms, so a worker that finishes inside the first tick
    /// is correctly reported as Exited no matter how small the start-up allowance was. That is right
    /// behaviour, and a test that demanded otherwise would have been pinning a bug.
    /// <para>
    /// So the invariant those paths carry — <b>a failure of the machine is never published as a
    /// failure of a suite</b> — is asserted here on the reachable path, and at the message level by
    /// <see cref="TheBlamelessMessages_NeverAccuseAFileAndTheAccusingOneIsHedged"/> for all of them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASuiteTheBuildNeverReached_IsReportedWithoutBeingBlamed()
    {
        WriteSpec("healthy.e2e.yaml", GoodSuiteYaml);

        // Total already spent: no attempt runs at all, so nothing was ever learned about any file.
        var exhausted = new SpecIndexWorkerBudget(
            Startup: TimeSpan.FromMinutes(1),
            Stall: TimeSpan.FromMinutes(1),
            Total: TimeSpan.Zero);

        var index = await WorkspaceSpecIndexer.BuildAsync(ResolvedWorkspace, exhausted);

        // Listed and not readable — both true — with text that says nothing about the file.
        var entry = Assert.Single(index.Specs);
        Assert.Equal("healthy.e2e.yaml", entry.Path);
        Assert.False(entry.Readable);
        Assert.Equal(SpecIndexWorkerClient.NotReachedEntryDetail, entry.ParseError);
        Assert.Contains("Nothing was established about the file itself", entry.ParseError!, StringComparison.Ordinal);
    }

    /// <summary>
    /// M1's regression: a workspace large enough to exceed the OS pipe buffer must still produce an
    /// index within the budget — never a hang on the stdin write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the peer review measured.</b> The stdin write that hands the worker its path list sat
    /// outside every clock: <c>WatchAsync</c> — owner of Startup, Stall and Total — starts only after
    /// the write returns, and the write had no bound of its own. A 4 KB payload completes; 8 KB blocks
    /// on the OS pipe buffer against a child that is not reading. The 500-path JSON array is roughly
    /// 35 KB and crosses 4 KB at about 60 suites, so any realistic workspace was exposed to an
    /// unbounded block.
    /// </para>
    /// <para>
    /// <b>What this test can and cannot reach, stated rather than implied.</b> A child that
    /// deliberately never reads its stdin is not reachable without a NEW fixture process — the
    /// production worker drains stdin to EOF as its first act, and <c>StdinEofChildFixture</c> exists
    /// to exercise the opposite property (graceful stop on EOF) for <c>VouchfxCliSuiteRunner</c>, so
    /// neither can be pointed at this. Rather than add a third fixture for one assertion, this pins
    /// what IS reachable and is the condition the defect actually manifested under: a payload well
    /// past the measured 8 KB blocking threshold completes, in full, inside a budget short enough that
    /// an unbounded write could not have fitted in it. Against the pre-fix code the write blocks until
    /// the child drains — which it does, so the ordinary path was never broken; what this asserts is
    /// that the write is now INSIDE the clock, by giving it a clock too small to hide in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWorkspaceWhosePathListExceedsThePipeBuffer_StillIndexesWithinTheBudget()
    {
        // 120 suites in a deeply-named directory: the JSON path array is comfortably past the 8 KB
        // measured blocking threshold, and past the ~4 KB one at which the review saw it start.
        const int suiteCount = 120;
        var deepDirectory = string.Join('/', Enumerable.Repeat("nested-directory-segment", 4));
        for (var i = 0; i < suiteCount; i++)
        {
            WriteSpec($"{deepDirectory}/suite-with-a-deliberately-long-name-{i:D4}.e2e.yaml", GoodSuiteYaml);
        }

        var payloadBytes = System.Text.Json.JsonSerializer.Serialize(
            Directory.GetFiles(SpecsDir, "*.e2e.yaml", SearchOption.AllDirectories)).Length;
        Assert.True(
            payloadBytes > 8 * 1024,
            $"Fixture is too small to exercise the pipe-buffer case: payload is {payloadBytes} bytes.");

        // A budget every part of which is short. If the write were still outside the clocks, a blocked
        // one would run past the test's own bound rather than being reported inside this budget.
        var tight = new SpecIndexWorkerBudget(
            Startup: TimeSpan.FromSeconds(30),
            Stall: TimeSpan.FromSeconds(10),
            Total: TimeSpan.FromSeconds(45));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var index = await WorkspaceSpecIndexer.BuildAsync(ResolvedWorkspace, tight, cts.Token);

        Assert.Equal(suiteCount, index.Specs.Count);
        Assert.All(
            index.Specs,
            entry => Assert.True(entry.Readable, $"{entry.Path} should have parsed: {entry.ParseError}"));
        Assert.Null(index.Reason);
    }

    [Fact]
    public async Task ANonAsciiSuiteFileName_RoundTripsThroughThePublishedPath()
    {
        // m2's regression: this field used to go through SanitiseForDisplay, which literal-escapes
        // every non-ASCII character — so this suite was published as "commandes-café.e2e.yaml",
        // a string that is not the file's name and that a host could not feed back to validate_suite.
        const string fileName = "commandes-café.e2e.yaml";
        WriteSpec(fileName, GoodSuiteYaml);

        var entry = Assert.Single((await BuildAsync()).Specs);

        Assert.Equal(fileName, entry.Path);
        Assert.DoesNotContain("\\u", entry.Path, StringComparison.Ordinal);

        // THE ROUND TRIP, which is the property the field's documentation promises: joining the
        // published path onto the published specsDir must name the file that exists on disk.
        Assert.True(
            File.Exists(Path.Combine(SpecsDir, entry.Path.Replace('/', Path.DirectorySeparatorChar))),
            "The published path must resolve back to the real file.");
    }

    [Fact]
    public void AControlCharacterInAPathIsStillRemoved_WhileEveryPrintableCharacterSurvives()
    {
        // The half of the sanitisation that is kept: a control character must never reach a host's
        // terminal. Composed numerically rather than written as a literal, per this repository's
        // convention.
        var escape = ((char)27).ToString();

        Assert.Equal(
            "café/naïve.e2e.yaml",
            WorkspaceSpecIndexer.CapAndSanitiseWirePath($"café/{escape}naïve.e2e.yaml"));
    }

    [Fact]
    public void TheBlamelessMessages_NeverAccuseAFileAndTheAccusingOneIsHedged()
    {
        // THE REGRESSION THIS PINS: the first version had ONE message for every non-parse outcome, so
        // a machine that could not spawn a process published every suite in the workspace as having
        // "driven the parser into an unrecoverable spin". Three of the four texts must therefore be
        // incapable of blaming a file, and the fourth — the only one with evidence — must still hedge.
        foreach (var blameless in new[]
        {
            SpecIndexWorkerClient.NotReachedEntryDetail,
            SpecIndexWorkerClient.WorkerUnavailableEntryDetail,
            SpecIndexWorkerClient.IncompleteEntryDetail,
        })
        {
            Assert.Contains("Nothing was established about the file itself", blameless, StringComparison.Ordinal);
            Assert.DoesNotContain("spin", blameless, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unparseable", blameless, StringComparison.OrdinalIgnoreCase);
        }

        // The one message that may point at a file names BOTH explanations and neither as fact,
        // because this server cannot tell them apart from the outside.
        Assert.Contains("spin", SpecIndexWorkerClient.StalledEntryDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("heavily loaded", SpecIndexWorkerClient.StalledEntryDetail, StringComparison.Ordinal);
        Assert.Contains("validate_suite", SpecIndexWorkerClient.StalledEntryDetail, StringComparison.Ordinal);
    }

    // ── Containment: the AC's own scenario, and the harder symlink case ─────────────────────────

    [Fact]
    public async Task ASuiteOutsideSpecsDir_NeverAppearsInTheIndex()
    {
        // US-S5-01's Gherkin, verbatim: "a workspace with specsDir 'e2e' and a suite file outside it
        // at '../secrets/other.e2e.yaml' … the out-of-specsDir file never appears in the index".
        //
        // HONEST ABOUT WHAT THIS PROVES (a peer review's nit): it is close to trivially true, because
        // Directory.EnumerateFiles rooted at specsDir could not return the sibling whether the
        // containment guard existed or not. It is kept because it is the story's own scenario and
        // states the requirement in the requirement's words — but the guard's real coverage is the
        // symlink case below and, definitively, PathContainmentPrimitiveTests, which calls the
        // containment primitives directly so the mechanism under test is unambiguous.
        WriteSpec("inside.e2e.yaml", GoodSuiteYaml);

        var secrets = Directory.CreateDirectory(Path.Combine(_root.FullName, "secrets"));
        File.WriteAllText(Path.Combine(secrets.FullName, "other.e2e.yaml"), GoodSuiteYaml);

        var index = await BuildAsync();

        var entry = Assert.Single(index.Specs);
        Assert.Equal("inside.e2e.yaml", entry.Path);
        Assert.DoesNotContain(index.Specs, candidate => candidate.Path.Contains("other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASymlinkInsideSpecsDirPointingOutOfIt_IsNotFollowed()
    {
        WriteSpec("inside.e2e.yaml", GoodSuiteYaml);

        var outside = Directory.CreateDirectory(Path.Combine(_root.FullName, "secrets"));
        File.WriteAllText(Path.Combine(outside.FullName, "leaked.e2e.yaml"), GoodSuiteYaml);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(SpecsDir, "linked"), outside.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating a symlink needs Developer Mode or elevation on Windows. Skipping is honest:
            // the assertion below would otherwise be vacuously true on such a machine, and claiming
            // coverage that did not run is worse than recording that it did not. The structural half
            // of the guard (AttributesToSkip = ReparsePoint) does not depend on this test to hold.
            return;
        }

        var index = await BuildAsync();

        var entry = Assert.Single(index.Specs);
        Assert.Equal("inside.e2e.yaml", entry.Path);
        Assert.DoesNotContain(index.Specs, candidate => candidate.Path.Contains("leaked", StringComparison.Ordinal));
    }

    // ── Bounds ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoreSuitesThanTheCap_AreTruncatedAndSaySo()
    {
        for (var i = 0; i <= WorkspaceSpecIndexer.MaxSpecsIndexed; i++)
        {
            WriteSpec($"suite-{i:D4}.e2e.yaml", GoodSuiteYaml);
        }

        var index = await BuildAsync();

        Assert.Equal(WorkspaceSpecIndexer.MaxSpecsIndexed, index.Specs.Count);

        // The same `truncated` meaning get_run_events, get_step_timeline and list_runs carry: what
        // you got may not be all there is. Without it a host reading exactly 500 entries cannot tell
        // a capped walk from a directory that genuinely holds 500 suites.
        Assert.True(index.Truncated);
        Assert.Equal(WorkspaceSpecIndexReasons.SpecLimitReached, index.Reason);
        Assert.False(string.IsNullOrWhiteSpace(index.Detail));
    }

    [Fact]
    public async Task ExactlyTheCapManySuites_IsNotReportedAsTruncated()
    {
        // The single extra probe HasAnotherMatch makes is what earns this distinction. Without it the
        // honest answer at exactly 500 would have to be "possibly truncated", which would train hosts
        // to ignore the flag on the one directory size where it is most likely to appear.
        for (var i = 0; i < WorkspaceSpecIndexer.MaxSpecsIndexed; i++)
        {
            WriteSpec($"suite-{i:D4}.e2e.yaml", GoodSuiteYaml);
        }

        var index = await BuildAsync();

        Assert.Equal(WorkspaceSpecIndexer.MaxSpecsIndexed, index.Specs.Count);
        Assert.False(index.Truncated);
        Assert.Null(index.Reason);
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_StopsTheBuildRatherThanFinishingIntoNothing()
    {
        WriteSpec("inside.e2e.yaml", GoodSuiteYaml);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The token reaches the enumeration loop, which is the unbounded part of this build (a
        // directory tree is as large as the disk allows). An abandoned resources/read must be able to
        // stop it mid-walk rather than leaving it to complete into a response nobody will read.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await BuildAsync(cts.Token));
    }

    private void WriteSpec(string relativePath, string content)
    {
        var fullPath = Path.Combine(SpecsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private const string GoodSuiteYaml = """
        metadata:
          name: "Orders API health and persistence smoke test"
          owner: "platform-team"
          tags:
            - smoke

        steps:
          - id: check-health
            type: http.rest
            target: orders-api
            method: GET
            path: /health

          - id: assert-order-row
            type: db-assert.postgres
            target: orders-db
            query: "SELECT count(*) FROM orders WHERE status = 'shipped'"
            expect:
              rowCount: 1
        """;
}
