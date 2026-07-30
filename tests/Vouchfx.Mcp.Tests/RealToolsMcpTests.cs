using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 4 (REQ-003, REQ-004, EDGE-003's validate path) end to end, through the same
/// in-memory MCP harness <c>McpServerSkeletonTests</c> uses: <c>validate_suite</c>,
/// <c>list_step_types</c>, and <c>describe_step_type</c> calling the REAL tool handlers (not
/// stubs) via a real <c>tools/call</c> round trip.
/// </summary>
/// <remarks>
/// Service-level behaviour (schema-structure derivation, error filtering, line resolution) is
/// covered directly against <see cref="Vouchfx.Mcp.Validation.StepTypeCatalogue"/> and
/// <see cref="Vouchfx.Mcp.Validation.SuiteValidator"/> in <c>Validation/*Tests.cs</c>; these
/// tests instead confirm the MCP-facing contract: the tools are wired up, return structured
/// content with the expected shape, and never crash the server.
/// <para>
/// <b>validate_suite spawns a real child process for every call that isn't a fast reject</b> (see
/// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>): even here, over the in-memory
/// harness, <c>Environment.ProcessPath</c> resolves to this test host rather than the
/// <c>Vouchfx.Mcp</c> apphost, so <c>ValidationWorkerClient</c>'s executable-resolution fallback
/// launches the real, built <c>Vouchfx.Mcp.dll</c> via <c>dotnet</c> — a genuine subprocess, not a
/// simulation. That is what makes the hang-shape regression test below a real proof rather than an
/// in-process approximation.
/// </para>
/// </remarks>
public class RealToolsMcpTests
{
    // ── validate_suite ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateSuite_GoodFixture_ReturnsValidTrue()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.True(payload.GetProperty("valid").GetBoolean());
        Assert.Empty(payload.GetProperty("errors").EnumerateArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ValidateSuite_BadFixture_ReturnsBothTheUnknownTypeAndMissingFieldErrors()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = FixturePath("bad-suite.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.False(payload.GetProperty("valid").GetBoolean());

        var errors = payload.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(2, errors.Length);

        Assert.Contains(errors, e =>
            e.GetProperty("kind").GetString() == "unknown-step-type" &&
            e.GetProperty("instancePath").GetString() == "/steps/0/type");

        Assert.Contains(errors, e =>
            e.GetProperty("kind").GetString() == "schema" &&
            e.GetProperty("instancePath").GetString() == "/steps/1" &&
            e.GetProperty("message").GetString()!.Contains("method", StringComparison.Ordinal) &&
            e.GetProperty("message").GetString()!.Contains("path", StringComparison.Ordinal));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ValidateSuite_MalformedYamlFixture_ReturnsYamlParseErrorWithLine()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = FixturePath("malformed.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.False(payload.GetProperty("valid").GetBoolean());

        var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
        Assert.Equal("yaml-parse", error.GetProperty("kind").GetString());
        Assert.True(error.GetProperty("line").GetInt64() > 0);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ValidateSuite_MissingFile_ReturnsFileNotFoundErrorWithoutCrashingServer()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        var stopwatch = Stopwatch.StartNew();
        var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = missingPath }, cts.Token);
        stopwatch.Stop();

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.False(payload.GetProperty("valid").GetBoolean());

        var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
        Assert.Equal("file-not-found", error.GetProperty("kind").GetString());

        // A missing file is caught by ValidationWorkerClient's in-process fast reject (see its
        // remarks) — no worker process is ever spawned for it. A spawned dotnet child takes
        // materially longer than a pure in-process check; this generous bound is only reachable
        // if no process was started.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Expected the fast-reject path (no process spawn) to complete quickly, took {stopwatch.Elapsed}.");

        // The server must still be responsive afterwards — this was never a crash.
        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(9, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── B1: untrusted YAML that could crash the server must be rejected, not parsed ───────────

    [Fact]
    public async Task ValidateSuite_DeeplyNestedFlowBracketFile_ReturnsTooDeepAndServerStaysResponsive()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // The proven native-StackOverflowException shape from the security review, written to a
        // real file so this exercises validate_suite's full path (file read -> guard) exactly as
        // an attacker-supplied path would. The guard must reject it from the raw text alone,
        // strictly before any YamlDotNet call — if it didn't, this test process itself would be
        // the one that crashes uncatchably, not just the assertions below that fail cleanly.
        var deeplyNestedPath = WriteTempSuite(new string('[', 20_000) + new string(']', 20_000));
        try
        {
            var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = deeplyNestedPath }, cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = GetStructuredContent(result);
            Assert.False(payload.GetProperty("valid").GetBoolean());

            var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
            Assert.Equal("too-deep", error.GetProperty("kind").GetString());

            // The server must still be responsive afterwards.
            var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
            Assert.Equal(9, tools.Count);
        }
        finally
        {
            File.Delete(deeplyNestedPath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ValidateSuite_BillionLaughsShapedFile_ReturnsAliasLimitAndServerStaysResponsive()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        const string billionLaughs = """
            a0: &a0 "x"
            a1: &a1 [*a0, *a0, *a0, *a0, *a0, *a0, *a0, *a0]
            a2: &a2 [*a1, *a1, *a1, *a1, *a1, *a1, *a1, *a1]
            a3: &a3 [*a2, *a2, *a2, *a2, *a2, *a2, *a2, *a2]
            steps: []
            """;
        var billionLaughsPath = WriteTempSuite(billionLaughs);
        try
        {
            var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = billionLaughsPath }, cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = GetStructuredContent(result);
            Assert.False(payload.GetProperty("valid").GetBoolean());

            var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
            Assert.Equal("alias-limit", error.GetProperty("kind").GetString());

            var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
            Assert.Equal(9, tools.Count);
        }
        finally
        {
            File.Delete(billionLaughsPath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── M2: a network/UNC path must never reach a filesystem call ──────────────────────────────

    [Fact]
    public async Task ValidateSuite_UncPath_ReturnsInvalidPathAndServerStaysResponsive()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var stopwatch = Stopwatch.StartNew();
        var result = await CallToolAsync(
            harness, "validate_suite", new() { ["path"] = @"\\attacker-host\share\suite.e2e.yaml" }, cts.Token);
        stopwatch.Stop();

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.False(payload.GetProperty("valid").GetBoolean());

        var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
        Assert.Equal("invalid-path", error.GetProperty("kind").GetString());

        // A UNC path is caught by ValidationWorkerClient's in-process fast reject — see the
        // equivalent comment on the missing-file test above.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Expected the fast-reject path (no process spawn) to complete quickly, took {stopwatch.Elapsed}.");

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(9, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Process isolation: the Scanner-hang shape must never hang the server ───────────────────

    [Fact]
    public async Task ValidateSuite_DegenerateHangShape_ReturnsValidationTimeoutAndServerStaysResponsive()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // The exact 12-byte degenerate shape found during the security review's final
        // adversarial pass on YamlSafetyGuard's Scanner-based nesting bound: a scalar "a: b"
        // immediately followed by a MORE-indented "a: b". Empirically confirmed (an isolated,
        // bounded scratchpad probe, never committed) to drive YamlDotNet's raw Scanner into an
        // unbounded, ~100%-CPU spin that does not return even after several seconds — not a
        // crash, and not merely a slow parse: a genuinely uninterruptible in-process hang with no
        // cooperative cancellation point anywhere in the Scanner's loop for a CancellationToken to
        // reach. This is the definitive regression proof for the process-isolation architecture
        // (see ValidationWorkerClient's remarks): validate_suite must still return a clean,
        // structured result within ValidationWorkerClient.DefaultTimeout, and — critically — this
        // harness (standing in for the server) must stay fully responsive to the very next call
        // afterwards, proving the hang was contained to a killed CHILD process rather than
        // blocking the caller.
        var degenerateHangPath = WriteTempSuite("a: b\n  a: b\n");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await CallToolAsync(harness, "validate_suite", new() { ["path"] = degenerateHangPath }, cts.Token);
            stopwatch.Stop();

            Assert.False(result.IsError ?? false);
            var payload = GetStructuredContent(result);
            Assert.False(payload.GetProperty("valid").GetBoolean());

            var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
            Assert.Equal("validation-timeout", error.GetProperty("kind").GetString());

            // Proves the production 10-second ValidationWorkerClient.DefaultTimeout actually
            // elapsed (the worker really did hang and really was killed at that bound) rather
            // than some unrelated fast-fail path happening to report the same error kind.
            Assert.True(
                stopwatch.Elapsed >= TimeSpan.FromSeconds(9),
                $"Expected the call to take close to the 10s production timeout, took {stopwatch.Elapsed}.");
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(20),
                $"Expected the call to return soon after the 10s timeout (the worker should have " +
                $"been killed promptly), took {stopwatch.Elapsed}.");

            // The defining property under test: the server was never blocked by the hung child —
            // it answers the very next request immediately.
            var responsivenessStopwatch = Stopwatch.StartNew();
            var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
            responsivenessStopwatch.Stop();

            Assert.Equal(9, tools.Count);
            Assert.True(
                responsivenessStopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"Expected tools/list to respond immediately after the hang was contained, took {responsivenessStopwatch.Elapsed}.");
        }
        finally
        {
            File.Delete(degenerateHangPath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── list_step_types ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListStepTypes_ReturnsTheFullSetGroupedByFamily()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "list_step_types", null, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        var families = payload.GetProperty("families").EnumerateArray().ToArray();

        var allTypes = families
            .SelectMany(f => f.GetProperty("types").EnumerateArray())
            .Select(t => t.GetProperty("type").GetString())
            .ToArray();

        Assert.Equal(25, allTypes.Length);
        Assert.Contains("http.rest", allTypes);
        Assert.Contains("db-assert.postgres", allTypes);

        // REQ-010 / bar B: list summaries include familyIntent + captureSupported.
        var httpFamily = Assert.Single(families, f => f.GetProperty("family").GetString() == "http");
        Assert.False(string.IsNullOrWhiteSpace(httpFamily.GetProperty("familyIntent").GetString()));
        var httpRest = Assert.Single(
            httpFamily.GetProperty("types").EnumerateArray(),
            t => t.GetProperty("type").GetString() == "http.rest");
        Assert.True(httpRest.GetProperty("captureSupported").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(httpRest.GetProperty("familyIntent").GetString()));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── describe_step_type ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DescribeStepType_HttpRest_ReturnsRequiredAndOptionalFields()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "describe_step_type", new() { ["type"] = "http.rest" }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.Equal("http.rest", payload.GetProperty("type").GetString());
        Assert.Equal("http", payload.GetProperty("family").GetString());
        Assert.Equal("rest", payload.GetProperty("provider").GetString());

        var fields = payload.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Contains(fields, f => f.GetProperty("name").GetString() == "method" && f.GetProperty("required").GetBoolean());
        Assert.Contains(fields, f => f.GetProperty("name").GetString() == "path" && f.GetProperty("required").GetBoolean());
        Assert.Contains(fields, f => f.GetProperty("name").GetString() == "headers" && !f.GetProperty("required").GetBoolean());

        // REQ-010 bar B shape from live catalogue export.
        var requiredFields = payload.GetProperty("requiredFields").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        Assert.Contains("method", requiredFields);
        Assert.Contains("path", requiredFields);
        Assert.Contains("target", requiredFields);

        var optionalFields = payload.GetProperty("optionalFields").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        Assert.Contains("headers", optionalFields);
        Assert.Contains("body", optionalFields);

        Assert.True(payload.GetProperty("captureSupported").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("familyIntent").GetString()));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_UnknownType_ReturnsToolErrorListingValidTypes()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "describe_step_type", new() { ["type"] = "nope.nope" }, cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("nope.nope", content.Text, StringComparison.Ordinal);
        Assert.Contains("http.rest", content.Text, StringComparison.Ordinal);

        // Not a crash: the server must still respond afterwards.
        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(9, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static ValueTask<CallToolResult> CallToolAsync(
        McpTestHarness harness, string toolName, Dictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static JsonElement GetStructuredContent(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    /// <summary>
    /// Writes <paramref name="content"/> to a fresh temp file and returns its path. Used instead
    /// of a checked-in fixture for the B1 regression tests: the "attack" content is a small,
    /// clearly-labelled generator local to the test that needs it, rather than a permanent file
    /// in the repo that a future contributor might not understand the purpose of at a glance.
    /// </summary>
    private static string WriteTempSuite(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
