using System.Text.Json;
using ModelContextProtocol.Protocol;

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
    /// outside any workspace — so both halves depended on <c>ExplainRunOrchestrator</c>'s
    /// containment exemptions. Since US-S3-01, a workspace-configured server places run artefacts
    /// under <c>Workspace.OutputDir</c>, INSIDE the root, so containment now passes over them
    /// naturally and the exemptions are inert here (they still cover the no-workspace mode and
    /// entries recorded under an older layout — see <c>ExplainRunOrchestrator.ExplainAsync</c>). The
    /// caller-visible contract this test actually guards — hand back what <c>run_suite</c> returned,
    /// or omit it, and either way the run is explained — is unchanged, which is the whole point of
    /// keeping it. The sibling test above still rejects an arbitrary caller-supplied events path
    /// outside the root, so the exempt set has not widened.
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
        // property of the layout rather than of the OS temp directory's retention policy. If this
        // ever regressed to a temp path, the round trip below would start depending on
        // ExplainRunOrchestrator's containment exemptions again, and this assertion is what would
        // say so rather than letting it pass silently.
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
    /// exemption must reach it — asserted rather than assumed, because "it inherits it" is exactly
    /// the kind of claim that stops being true after one refactor.
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
