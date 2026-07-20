using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 6 (REQ-008) end to end, through the same in-memory MCP harness the other
/// <c>Real*McpTests</c> classes use: <c>run_suite</c>'s CLI presence + version handshake gate, and
/// — just as importantly — that the OTHER tools stay completely CLI-independent regardless of the
/// gate's outcome.
/// </summary>
/// <remarks>
/// Service-level behaviour (version normalisation, message building, caching) is covered directly
/// against <see cref="Vouchfx.Mcp.Cli.CliPinVerifier"/> in <c>Cli/CliPinVerifierTests.cs</c>; these
/// tests instead confirm the MCP-facing contract, using <see cref="FakeVouchfxCli"/> so nothing
/// here depends on the real <c>vouchfx</c> CLI being installed. Every test below uses a VALID suite
/// fixture path (<c>good-suite.e2e.yaml</c>) — REQ-006's gate ordering runs EDGE-003 pre-validation
/// BEFORE the CLI handshake (see <c>RunSuiteOrchestrator</c>'s remarks), so an invalid/nonexistent
/// path would be rejected by that earlier gate and never actually reach the CLI check these tests
/// exist to cover; see <c>RealRunSuiteMcpTests</c> for that earlier gate's own coverage.
/// </remarks>
public class RealCliHandshakeMcpTests
{
    // ── The gate: CLI missing ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunSuite_CliNotFound_ReturnsTheInstallCommandErrorWithoutCrashingServer()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await CallToolAsync(harness, "run_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") }, cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("not found on PATH", content.Text, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global vouchfx --version", content.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented", content.Text, StringComparison.OrdinalIgnoreCase);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(6, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunSuite_CliVersionMismatch_ReturnsTheUpdateCommandErrorNamingBothVersions()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: FakeVouchfxCli.ReportingVersion("1.0.0-alpha.1"));

        var result = await CallToolAsync(harness, "run_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") }, cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("1.0.0-alpha.1", content.Text, StringComparison.Ordinal);
        Assert.Contains(McpTestHarness.DefaultTestPin.Version, content.Text, StringComparison.Ordinal);
        Assert.Contains("dotnet tool update --global vouchfx --version", content.Text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── The gate: CLI present and matching -> proceeds to actually run the suite ────────────────

    [Fact]
    public async Task RunSuite_CliMatchesPin_ProceedsToRunTheSuite()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The harness's default fake CLI already reports a version matching its default pin — no
        // override needed to exercise the "Ok" path. A real (fake) runner is supplied so the call
        // completes instead of hitting the default harness's NeverExpectedToRun() guard.
        var runner = FakeSuiteRunner.Succeeding(
            outputLines: [],
            eventsFileContent: """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""",
            exitCode: 0);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await CallToolAsync(harness, "run_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal("Pass", payload.GetProperty("verdict").GetString());
        Assert.Equal(1, runner.InvocationCount);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── CLI-independence: the other tools must keep working regardless of the gate's outcome ──

    [Fact]
    public async Task ValidateSuite_StillReturnsValidTrue_WhenTheCliIsNotFound()
    {
        // The core proof of REQ-008's CLI-independence requirement: a run_suite-blocking CLI
        // absence must have ZERO effect on validate_suite, which never calls the verifier at all.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await CallToolAsync(
            harness, "validate_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.True(payload.GetProperty("valid").GetBoolean());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("list_step_types")]
    [InlineData("search_docs")]
    public async Task OtherCliIndependentTools_StillRespond_WhenTheCliIsNotFound(string toolName)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var arguments = toolName == "search_docs"
            ? new Dictionary<string, object?> { ["query"] = "verifyMode" }
            : null;

        var result = await CallToolAsync(harness, toolName, arguments, cts.Token);

        Assert.False(result.IsError ?? false);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static ValueTask<CallToolResult> CallToolAsync(
        McpTestHarness harness, string toolName, Dictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
