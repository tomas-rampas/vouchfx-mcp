using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-08 end to end over the real MCP wire protocol (the in-memory <see cref="McpTestHarness"/>
/// with a <see cref="FakeVouchfxCli"/> — no real engine, no Docker): the VFX-E-1001 containment
/// golden, and the no-workspace compatibility path that proves the same input is still accepted for
/// a caller who never opted in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every containment assertion here is PAIRED with its no-workspace twin.</b> Plan §2.1 requires
/// this story to land as an explicit behaviour change, gated on workspace-configured-ness: a test
/// that only showed the rejection would prove the new policy works while saying nothing about the
/// promise that matters more — that nobody who never passed <c>--workspace</c> sees it. The pairs
/// below run the IDENTICAL tool call against the IDENTICAL file, differing only in whether the
/// server was given a workspace.
/// </para>
/// <para>
/// The layout each test builds is the story's own scenario, on disk:
/// <c>&lt;temp&gt;/workspace-a</c> (the configured root, holding a valid suite) beside
/// <c>&lt;temp&gt;/workspace-b</c> (holding <c>secret.e2e.yaml</c>, the file
/// <c>../workspace-b/secret.e2e.yaml</c> reaches).
/// </para>
/// </remarks>
public class RealWorkspaceContainmentMcpTests : IDisposable
{
    private const string ValidSuiteYaml = """
        metadata:
          name: "Orders API health smoke test"
          owner: "platform-team"

        steps:
          - id: check-health
            type: http.rest
            description: "Confirms the health endpoint responds successfully."
            target: orders-api
            method: GET
            path: /health
        """;

    private readonly string _sandbox;
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly Workspace _workspaceA;

    public RealWorkspaceContainmentMcpTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-containment-" + Guid.NewGuid().ToString("N"));
        _rootA = Path.Combine(_sandbox, "workspace-a");
        _rootB = Path.Combine(_sandbox, "workspace-b");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);

        File.WriteAllText(Path.Combine(_rootA, "inside.e2e.yaml"), ValidSuiteYaml);
        File.WriteAllText(Path.Combine(_rootB, "secret.e2e.yaml"), ValidSuiteYaml);

        _workspaceA = Workspace.Resolve(_rootA);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    /// <summary>The path from the story's second scenario: <c>../workspace-b/secret.e2e.yaml</c>, rooted at A.</summary>
    private string EscapingPath => Path.Combine(_rootA, "..", "workspace-b", "secret.e2e.yaml");

    // ── Scenario 2: a path escaping the configured workspace root is rejected ───────────────────

    [Fact]
    public async Task ValidateSuite_WorkspaceConfigured_PathEscapingTheRoot_IsRejectedWithVfxE1001()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = EscapingPath }, cts.Token);

        // A tool error, not a validation failure: the suite's validity was never determined, which is
        // the same split every other rejected-path code already follows (US-S1-04).
        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("outside the configured workspace root", ErrorMessageOf(result), StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_WorkspaceConfigured_PathEscapingTheRoot_IsRejectedWithVfxE1001()
    {
        // normalize_suite reaches the same worker client through the same input resolver, so it must
        // give the same verdict — two tools disagreeing about one path is exactly what sharing that
        // client exists to prevent.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(harness, "normalize_suite", new() { ["path"] = EscapingPath }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
    }

    [Fact]
    public async Task RunSuite_WorkspaceConfigured_PathEscapingTheRoot_IsRefusedBeforeTheEngineIsSpawned()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // NeverExpectedToRun is the assertion: reaching the runner at all would mean the escaping
        // path had already been handed to the engine CLI.
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.NeverExpectedToRun(), workspace: _workspaceA);

        var result = await CallToolAsync(harness, "run_suite", new() { ["path"] = EscapingPath }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
    }

    [Fact]
    public async Task ExplainRun_WorkspaceConfigured_CallerSuppliedEventsPathEscapingTheRoot_IsRejectedWithVfxE1001()
    {
        var eventsPath = Path.Combine(_rootA, "..", "workspace-b", "events.jsonl");
        File.WriteAllText(Path.Combine(_rootB, "events.jsonl"), "{}\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(harness, "explain_run", new() { ["eventsPath"] = eventsPath }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("outside the configured workspace root", ErrorMessageOf(result), StringComparison.Ordinal);
    }

    // ── Scenario 3: no --workspace configured — behaviour is unchanged ──────────────────────────

    [Fact]
    public async Task ValidateSuite_NoWorkspaceConfigured_TheSameEscapingPath_SucceedsExactlyAsBefore()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = EscapingPath }, cts.Token);

        // Not merely "no VFX-E-1001" — a fully successful validation of the file that path reaches.
        // That is the byte-for-byte compatibility claim: the traversal is read, not refused.
        Assert.False(result.IsError ?? false);

        var payload = GetStructuredContent(result);
        Assert.True(payload.GetProperty("valid").GetBoolean());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainRun_NoWorkspaceConfigured_TheSameEscapingEventsPath_IsNotRejectedForContainment()
    {
        var eventsPath = Path.Combine(_rootA, "..", "workspace-b", "events.jsonl");
        File.WriteAllText(Path.Combine(_rootB, "events.jsonl"), "{}\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "explain_run", new() { ["eventsPath"] = eventsPath }, cts.Token);

        // The file is read and found to carry nothing recognisable — a DIFFERENT code entirely. The
        // point is only that the path itself was never the objection.
        Assert.NotEqual("VFX-E-1001", ErrorCodeOf(result));
    }

    // ── Scenario 4: UNC is rejected in both modes ───────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateSuite_UncPath_IsRejectedWhetherOrNotAWorkspaceIsConfigured(bool configureWorkspace)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, workspace: configureWorkspace ? _workspaceA : null);

        var result = await CallToolAsync(
            harness, "validate_suite", new() { ["path"] = @"\\attacker-host\share\suite.e2e.yaml" }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));

        // The UNC arm's own wording, unchanged by this story — not the containment message.
        Assert.Contains("network/UNC", ErrorMessageOf(result), StringComparison.Ordinal);
    }

    // ── A path INSIDE the configured root still works ───────────────────────────────────────────

    [Fact]
    public async Task ValidateSuite_WorkspaceConfigured_PathInsideTheRoot_StillValidates()
    {
        // Anti-vacuity for every rejection above: containment must not simply refuse everything.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(
            harness, "validate_suite", new() { ["path"] = Path.Combine(_rootA, "inside.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.True(GetStructuredContent(result).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task ValidateSuite_WorkspaceConfigured_InlineYaml_IsUnaffectedByContainment()
    {
        // An inline source carries no path, so there is nothing for containment to bound. That is the
        // absence of a subject, not an exemption — and it must keep working with a workspace set.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(harness, "validate_suite", new() { ["yaml"] = ValidSuiteYaml }, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.True(GetStructuredContent(result).GetProperty("valid").GetBoolean());
    }

    // ── run_suite → explain_run round trip under a configured workspace ─────────────────────────

    /// <summary>
    /// A code review's MAJOR finding, closed: <c>run_suite</c> RETURNS <c>eventsFilePath</c>, and a
    /// host that hands that exact value back to <c>explain_run</c> was getting VFX-E-1001 for doing
    /// precisely what the tool contract invites — while the very same file was readable if
    /// <c>eventsPath</c> was OMITTED. Both halves of that inconsistency are pinned here, together,
    /// in one workspace-configured session.
    /// </summary>
    /// <remarks>
    /// <b>US-S3-01 changed WHY this passes, and the test is kept for exactly that reason.</b> When
    /// the finding was raised, <c>run_suite</c> wrote its events file into the OS temp directory —
    /// outside any workspace — so both halves depended on two containment EXEMPTIONS
    /// <c>ExplainRunOrchestrator</c> carried. Since US-S3-01 a workspace-configured server places run
    /// artefacts under <c>Workspace.OutputDir</c>, INSIDE the root, so containment passes over them on
    /// their merits — which is what let those exemptions be RETIRED outright rather than kept
    /// (see <c>ExplainRunOrchestrator.ExplainAsync</c>). <b>This test is therefore the proof that the
    /// retirement did not break the round trip</b>, and it asserts the caller-visible contract, not
    /// the mechanism: hand back what <c>run_suite</c> returned, or omit it, and either way the run is
    /// explained. The sibling test above still rejects an arbitrary caller-supplied events path
    /// outside the root.
    /// </remarks>
    [Theory]
    // The explicit round trip: hand back exactly what run_suite returned.
    [InlineData(true)]
    // EDGE-004's documented default: omit eventsPath entirely.
    [InlineData(false)]
    public async Task RunSuiteThenExplainRun_WorkspaceConfigured_TheServerProducedEventsPathIsReadable(
        bool passEventsPathExplicitly)
    {
        const string eventsFileContent = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], eventsFileContent, exitCode: 0),
            workspace: _workspaceA);

        var run = await CallToolAsync(
            harness, "run_suite", new() { ["path"] = Path.Combine(_rootA, "inside.e2e.yaml") }, cts.Token);

        Assert.False(run.IsError ?? false);

        var eventsFilePath = GetStructuredContent(run).GetProperty("eventsFilePath").GetString();
        Assert.False(string.IsNullOrEmpty(eventsFilePath));

        // Anti-vacuity, INVERTED by US-S3-01: the events file must now be inside the configured root
        // (under the workspace's own output directory), which is what makes restart survival a
        // property of the layout rather than of the OS temp directory's retention policy. Since the
        // containment exemptions were retired there is no longer any fallback if this regressed to a
        // temp path — the round trip below would simply start failing with VFX-E-1001 — so this
        // assertion is what names the CAUSE rather than leaving it to be diagnosed from the symptom.
        Assert.StartsWith(
            _workspaceA.OutputDir + Path.DirectorySeparatorChar,
            eventsFilePath!,
            StringComparison.OrdinalIgnoreCase);

        var arguments = passEventsPathExplicitly
            ? new Dictionary<string, object?> { ["eventsPath"] = eventsFilePath }
            : [];

        var explain = await CallToolAsync(harness, "explain_run", arguments, cts.Token);

        Assert.False(explain.IsError ?? false);
        Assert.Equal("Pass", GetStructuredContent(explain).GetProperty("verdict").GetString());
    }

    /// <summary>
    /// <c>diagnose_run</c> shares <c>ExplainRunOrchestrator</c>'s whole path-intake seam, so the same
    /// round trip must work through it — asserted rather than assumed, because "it inherits it" is
    /// exactly the kind of claim that stops being true after one refactor.
    /// </summary>
    [Fact]
    public async Task RunSuiteThenDiagnoseRun_WorkspaceConfigured_TheServerProducedEventsPathIsReadable()
    {
        const string eventsFileContent = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], eventsFileContent, exitCode: 0),
            workspace: _workspaceA);

        var run = await CallToolAsync(
            harness, "run_suite", new() { ["path"] = Path.Combine(_rootA, "inside.e2e.yaml") }, cts.Token);

        Assert.False(run.IsError ?? false);
        var eventsFilePath = GetStructuredContent(run).GetProperty("eventsFilePath").GetString();

        var diagnose = await CallToolAsync(
            harness, "diagnose_run", new() { ["eventsPath"] = eventsFilePath }, cts.Token);

        Assert.False(diagnose.IsError ?? false);
    }

    // ── Workspace-relative path resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// The US-S3-08 review fix for "workspace-relative": every path-taking tool's description
    /// promises it, but relative paths used to resolve against the PROCESS's current directory —
    /// which is almost never the workspace root — so under a workspace every relative path was
    /// rejected. The assertion proves the file was read from <c>&lt;root&gt;/&lt;path&gt;</c> BY ITS
    /// CONTENT, not merely that the call succeeded.
    /// </summary>
    [Fact]
    public async Task ValidateSuite_WorkspaceConfigured_RelativePathResolvesAgainstTheRoot()
    {
        // A suite that is INVALID in a uniquely identifiable way, placed only inside the root. If the
        // relative path had resolved against the process CWD instead, the call would have failed with
        // VFX-E-1002 (file not found) rather than reporting THIS file's own schema error.
        var relativeDirectory = Path.Combine(_rootA, "nested");
        Directory.CreateDirectory(relativeDirectory);
        File.WriteAllText(
            Path.Combine(relativeDirectory, "relative.e2e.yaml"),
            ValidSuiteYaml.Replace("type: http.rest", "type: not.a.real.step.type", StringComparison.Ordinal));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(
            harness, "validate_suite", new() { ["path"] = "nested/relative.e2e.yaml" }, cts.Token);

        Assert.False(result.IsError ?? false);

        var payload = GetStructuredContent(result);
        Assert.False(payload.GetProperty("valid").GetBoolean());

        // Content proof: the errors name the file the ROOT-relative path reaches.
        Assert.Contains("not.a.real.step.type", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateSuite_WorkspaceConfigured_RelativePathEscapingTheRoot_IsRejectedWithVfxE1001()
    {
        // Rebasing decides WHICH absolute path is meant; it is not itself containment. `../` still
        // escapes, and the guard is still what refuses it.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: _workspaceA);

        var result = await CallToolAsync(
            harness, "validate_suite", new() { ["path"] = "../workspace-b/secret.e2e.yaml" }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("outside the configured workspace root", ErrorMessageOf(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The compatibility half of the rebasing fix: with NO workspace, a relative path still resolves
    /// against the process's current directory, byte for byte as before. Asserted by pointing at a
    /// file that exists relative to the CWD and nowhere else.
    /// </summary>
    [Fact]
    public async Task ValidateSuite_NoWorkspaceConfigured_RelativePathStillResolvesAgainstTheCurrentDirectory()
    {
        var relativeName = "vouchfx-mcp-cwd-probe-" + Guid.NewGuid().ToString("N") + ".e2e.yaml";
        var cwdFile = Path.Combine(Directory.GetCurrentDirectory(), relativeName);
        File.WriteAllText(cwdFile, ValidSuiteYaml);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var harness = await McpTestHarness.StartAsync(cts.Token);

            var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = relativeName }, cts.Token);

            Assert.False(result.IsError ?? false);
            Assert.True(GetStructuredContent(result).GetProperty("valid").GetBoolean());
        }
        finally
        {
            File.Delete(cwdFile);
        }
    }

    /// <summary>
    /// <c>run_suite</c> rebases in its OWN pre-flight (it splices the path into the engine CLI's
    /// argument list as well as handing it to the worker), so the same relative path must reach the
    /// runner as an absolute path inside the root — proved by capturing what the runner was actually
    /// given.
    /// </summary>
    [Fact]
    public async Task RunSuite_WorkspaceConfigured_RelativePathReachesTheRunnerAsAnAbsoluteRootedPath()
    {
        var relative = "nested/runnable.e2e.yaml";
        Directory.CreateDirectory(Path.Combine(_rootA, "nested"));
        File.WriteAllText(Path.Combine(_rootA, "nested", "runnable.e2e.yaml"), ValidSuiteYaml);

        string? observedSuitePath = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Observing(
                spec => observedSuitePath = spec.SuitePath,
                eventsFileContent: """
                    {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
                    """),
            workspace: _workspaceA);

        var result = await CallToolAsync(harness, "run_suite", new() { ["path"] = relative }, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.Equal(Path.Combine(_rootA, "nested", "runnable.e2e.yaml"), observedSuitePath);
    }

    // ── Issue #76: plan_coverage's path/eventsPath, retrofitted onto the same guard ──────────────

    /// <summary>
    /// The minimal plan report the fake CLI returns when a call is EXPECTED to reach the engine —
    /// enough to deserialise, nothing more. Its content is never asserted here; what these tests
    /// assert is whether the engine was reached at all, and with which argument strings.
    /// </summary>
    private const string EmptyPlanReportJson = """
        {
          "schemaVersion": 1,
          "engineVersion": "1.0.0-test",
          "thresholds": { "staleDays": 30, "flakyMinRuns": 2, "fragileMinEnvErrors": 2, "inconclusiveMin": 2 },
          "inventory": {
            "suites": [], "services": [], "dependencies": [], "stepTypes": [],
            "runCount": 0, "firstEventTs": null, "lastEventTs": null,
            "skippedEventLines": 0, "unmatchedObservations": 0,
            "unanalysableSuites": [], "unmappableDependencies": []
          },
          "findings": []
        }
        """;

    /// <summary>
    /// <b>The compatibility-breaking half of issue #76, pinned in BOTH modes.</b> The UNC arm is
    /// unconditional everywhere else in this server, and before the retrofit <c>plan_coverage</c>
    /// handed a UNC path straight to <c>vouchfx plan</c>'s argument list — so the forced-authentication
    /// primitive <see cref="Vouchfx.Mcp.Validation.PathSafetyGuard"/> exists to stop simply landed one
    /// process over, in the engine subprocess. <c>configureWorkspace: false</c> is therefore a NEW
    /// rejection for a caller who never opted into anything, which is exactly why it is asserted
    /// explicitly rather than folded into the workspace case.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlanCoverage_UncPath_IsRejectedWhetherOrNotAWorkspaceIsConfigured(bool configureWorkspace)
    {
        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            vouchfxCli: PlanCli(args => observedArguments = args),
            workspace: configureWorkspace ? _workspaceA : null);

        var result = await CallToolAsync(
            harness, "plan_coverage", new() { ["path"] = @"\\attacker-host\share\suites" }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("network/UNC", ErrorMessageOf(result), StringComparison.Ordinal);

        // The whole point: the engine was never handed the path, so no SMB/NTLM handshake could be
        // performed on this server's behalf.
        Assert.Null(observedArguments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlanCoverage_UncEventsPath_IsRejectedWhetherOrNotAWorkspaceIsConfigured(bool configureWorkspace)
    {
        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            vouchfxCli: PlanCli(args => observedArguments = args),
            workspace: configureWorkspace ? _workspaceA : null);

        var result = await CallToolAsync(
            harness,
            "plan_coverage",
            new() { ["path"] = _rootA, ["eventsPath"] = @"\\attacker-host\share\events.jsonl" },
            cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("network/UNC", ErrorMessageOf(result), StringComparison.Ordinal);
        Assert.Null(observedArguments);
    }

    [Fact]
    public async Task PlanCoverage_WorkspaceConfigured_PathEscapingTheRoot_IsRejectedWithVfxE1001()
    {
        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: PlanCli(args => observedArguments = args), workspace: _workspaceA);

        var result = await CallToolAsync(harness, "plan_coverage", new() { ["path"] = EscapingPath }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("outside the configured workspace root", ErrorMessageOf(result), StringComparison.Ordinal);
        Assert.Null(observedArguments);
    }

    [Fact]
    public async Task PlanCoverage_NoWorkspaceConfigured_TheSameEscapingPath_IsStillAnalysed()
    {
        // The paired compatibility twin: containment is workspace-gated for plan_coverage exactly as
        // it is for every other tool here — the traversal is analysed, not refused, and the engine is
        // handed the caller's own string.
        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: PlanCli(args => observedArguments = args));

        var result = await CallToolAsync(harness, "plan_coverage", new() { ["path"] = EscapingPath }, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(observedArguments);
        Assert.Equal(EscapingPath, observedArguments![1]);
    }

    [Fact]
    public async Task PlanCoverage_WorkspaceConfigured_EventsPathEscapingTheRoot_IsRejectedWithVfxE1001()
    {
        var eventsPath = Path.Combine(_rootA, "..", "workspace-b", "events.jsonl");
        File.WriteAllText(Path.Combine(_rootB, "events.jsonl"), "{}\n");

        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: PlanCli(args => observedArguments = args), workspace: _workspaceA);

        var result = await CallToolAsync(
            harness, "plan_coverage", new() { ["path"] = _rootA, ["eventsPath"] = eventsPath }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal("VFX-E-1001", ErrorCodeOf(result));
        Assert.Contains("outside the configured workspace root", ErrorMessageOf(result), StringComparison.Ordinal);
        Assert.Null(observedArguments);
    }

    [Fact]
    public async Task PlanCoverage_NoWorkspaceConfigured_TheSameEscapingEventsPath_IsStillAnalysed()
    {
        var eventsPath = Path.Combine(_rootA, "..", "workspace-b", "events.jsonl");
        File.WriteAllText(Path.Combine(_rootB, "events.jsonl"), "{}\n");

        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: PlanCli(args => observedArguments = args));

        var result = await CallToolAsync(
            harness, "plan_coverage", new() { ["path"] = _rootA, ["eventsPath"] = eventsPath }, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(observedArguments);
        Assert.Contains(eventsPath, observedArguments!, StringComparer.Ordinal);
    }

    /// <summary>
    /// The rebasing half: <c>plan_coverage</c> splices both paths into the engine's argument list, so
    /// the guard and the engine must see the SAME absolute string — proved by capturing the argv the
    /// fake CLI was actually handed rather than by the call merely succeeding.
    /// </summary>
    [Fact]
    public async Task PlanCoverage_WorkspaceConfigured_RelativePathsReachTheEngineAsAbsoluteRootedPaths()
    {
        Directory.CreateDirectory(Path.Combine(_rootA, "nested"));
        File.WriteAllText(Path.Combine(_rootA, "nested", "planned.e2e.yaml"), ValidSuiteYaml);
        File.WriteAllText(Path.Combine(_rootA, "history.jsonl"), "{}\n");

        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: PlanCli(args => observedArguments = args), workspace: _workspaceA);

        var result = await CallToolAsync(
            harness,
            "plan_coverage",
            new() { ["path"] = "nested", ["eventsPath"] = "history.jsonl" },
            cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(observedArguments);
        Assert.Equal(Path.Combine(_rootA, "nested"), observedArguments![1]);
        Assert.Contains(Path.Combine(_rootA, "history.jsonl"), observedArguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PlanCoverage_WorkspaceConfigured_PathInsideTheRoot_StillReachesTheEngine()
    {
        // Anti-vacuity for every plan_coverage rejection above: the guard must not simply refuse
        // everything this tool is handed.
        IReadOnlyList<string>? observedArguments = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: PlanCli(args => observedArguments = args), workspace: _workspaceA);

        var result = await CallToolAsync(harness, "plan_coverage", new() { ["path"] = _rootA }, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(observedArguments);
        Assert.Equal(_rootA, observedArguments![1]);
    }

    /// <summary>
    /// A <see cref="FakeVouchfxCli"/> whose pin handshake succeeds and whose <c>plan</c> invocation
    /// records the argument list it was given — the ONLY evidence that distinguishes "refused before
    /// the engine" from "refused by the engine".
    /// </summary>
    private static FakeVouchfxCli PlanCli(Action<IReadOnlyList<string>> observe) =>
        FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version),
            args =>
            {
                observe(args);
                return CliInvocationResult.Completed(0, EmptyPlanReportJson, string.Empty);
            });

    private static ValueTask<CallToolResult> CallToolAsync(
        McpTestHarness harness, string toolName, Dictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static JsonElement GetStructuredContent(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");

    private static string? ErrorCodeOf(CallToolResult result) => ErrorFieldOf(result, "code");

    private static string ErrorMessageOf(CallToolResult result) =>
        ErrorFieldOf(result, "message") ?? throw new InvalidOperationException("Expected the error to carry a message.");

    private static string? ErrorFieldOf(CallToolResult result, string propertyName)
    {
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));

        using var document = JsonDocument.Parse(content.Text);
        return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }
}
