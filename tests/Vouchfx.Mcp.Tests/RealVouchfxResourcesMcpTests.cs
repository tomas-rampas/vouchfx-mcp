using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Examples;
using Vouchfx.Mcp.Resources;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-01's wire-facing goldens: every new <c>vouchfx://</c> URI resolves over the real MCP
/// protocol, serves the content its AC names, and refuses a hostile argument.
/// </summary>
/// <remarks>
/// <para>
/// The central assertion in the run-resource section is
/// <c>ResourceBodyEqualsToolPayloadWithoutMeta</c>: the AC requires these resources serve "the SAME
/// data" their tools return, and the only version of that claim worth testing is a byte-level one.
/// So each run resource's body is compared to the corresponding TOOL CALL's own
/// <c>structuredContent</c> with its <c>meta</c> property removed — see
/// <see cref="ResourceJson"/>'s header for why <c>meta</c> is deliberately the one difference.
/// A divergence in either direction fails here.
/// </para>
/// <para>
/// Every test asserts stdout cleanliness, like every other <c>Real*McpTests</c> class in this repo:
/// stdout is the JSON-RPC channel and nothing else may ever write to it (plan §2.7 invariant 11), and
/// this story adds handlers that could have broken it.
/// </para>
/// </remarks>
public class RealVouchfxResourcesMcpTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

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
                // A locked temp file must never fail the test that produced it.
            }
        }

        GC.SuppressFinalize(this);
    }

    // ── vouchfx://schema/{version} ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SchemaResource_LatestAndTheLiteralVersion_ReturnByteIdenticalContent()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var viaAlias = await ReadTextAsync(harness, "vouchfx://schema/latest", cts.Token);
        var viaVersion = await ReadTextAsync(harness, $"vouchfx://schema/{VendoredSchemaVersion.Value}", cts.Token);

        // AC-002's Gherkin scenario, over the wire.
        Assert.Equal(viaAlias.Text, viaVersion.Text);
        Assert.Equal(VendoredComposedSchema.RawJson, viaAlias.Text);
        Assert.Equal(ResourceJson.MimeType, viaAlias.MimeType);

        // Substantive, so the equality above is not two empty strings.
        Assert.True(viaAlias.Text!.Length > 10_000);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task SchemaResource_AnUnknownVersion_IsAProtocolErrorRatherThanTheWrongDocument()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync("vouchfx://schema/v99", cancellationToken: cts.Token));

        // The server survives the refusal and keeps serving — the property every unresolvable
        // template parameter in this server is held to.
        Assert.Equal(
            VendoredComposedSchema.RawJson,
            (await ReadTextAsync(harness, "vouchfx://schema/latest", cts.Token)).Text);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── vouchfx://docs/errors/{code} — the scheme alias ────────────────────────────────────────

    [Theory]
    [InlineData("VFX-E-1002")]
    [InlineData("VFX-D-1201")]
    [InlineData("VFX-E-1903")]
    public async Task ErrorPageAlias_ServesExactlyWhatTheSprint1UriServes(string code)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var viaOriginal = await ReadTextAsync(harness, $"vouchfx-docs:///errors/{code}", cts.Token);
        var viaAlias = await ReadTextAsync(harness, $"vouchfx://docs/errors/{code}", cts.Token);

        // AC-003: same content, and the Sprint 1 URI keeps working unchanged (plan D4).
        Assert.Equal(viaOriginal.Text, viaAlias.Text);
        Assert.Equal(viaOriginal.MimeType, viaAlias.MimeType);
        Assert.False(string.IsNullOrWhiteSpace(viaAlias.Text));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ErrorPageAlias_AnUnknownCode_IsARefusalOnBothUris()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync("vouchfx-docs:///errors/VFX-E-9999", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync("vouchfx://docs/errors/VFX-E-9999", cancellationToken: cts.Token));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── vouchfx://examples/{name} ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExampleResource_ServesEveryCataloguedExampleVerbatim()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        foreach (var example in ExampleSuites.All)
        {
            var content = await ReadTextAsync(harness, $"vouchfx://examples/{example.Name}", cts.Token);

            Assert.Equal(ExampleSuiteRepository.GetRawText(example.Name), content.Text);
            Assert.Equal(ResourceJson.YamlMimeType, content.MimeType);

            // The annotation survives the wire — comments are the payload here, and a
            // parse-and-re-emit anywhere on the path would silently drop them.
            Assert.Contains("#", content.Text!, StringComparison.Ordinal);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExampleResource_AnUnknownName_IsARefusalNamingTheAvailableOnes()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var ex = await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync("vouchfx://examples/nope", cancellationToken: cts.Token));

        Assert.Contains(ExampleSuites.HttpSmoke.Name, ex.Message, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── vouchfx://workspace/specs ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkspaceSpecsResource_WithNoWorkspaceConfigured_SaysSoRatherThanLookingEmpty()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The default harness mode: no --workspace, which is the full-fidelity legacy mode rather
        // than a degraded one (Workspace.cs).
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await ReadJsonAsync(harness, VouchfxResourceUris.WorkspaceSpecsUri, cts.Token);

        Assert.False(payload.GetProperty("workspaceConfigured").GetBoolean());
        Assert.Empty(payload.GetProperty("specs").EnumerateArray());
        Assert.Equal("no-workspace-configured", payload.GetProperty("reason").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task WorkspaceSpecsResource_IndexesOnlySuitesUnderSpecsDir()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var root = Directory.CreateTempSubdirectory("vfx-mcp-specs-resource-");
        try
        {
            var specsDir = Directory.CreateDirectory(Path.Combine(root.FullName, Workspace.SpecsDirectoryName));
            File.WriteAllText(Path.Combine(specsDir.FullName, "inside.e2e.yaml"), GoodSuiteYaml);

            // US-S5-01's Gherkin: a suite OUTSIDE specsDir must never appear.
            var secrets = Directory.CreateDirectory(Path.Combine(root.FullName, "secrets"));
            File.WriteAllText(Path.Combine(secrets.FullName, "other.e2e.yaml"), GoodSuiteYaml);

            await using var harness = await McpTestHarness.StartAsync(
                cts.Token, workspace: Workspace.Resolve(root.FullName));

            var payload = await ReadJsonAsync(harness, VouchfxResourceUris.WorkspaceSpecsUri, cts.Token);

            Assert.True(payload.GetProperty("workspaceConfigured").GetBoolean());

            var specs = payload.GetProperty("specs").EnumerateArray().ToArray();
            var only = Assert.Single(specs);
            Assert.Equal("inside.e2e.yaml", only.GetProperty("path").GetString());
            Assert.Equal("Orders API health and persistence smoke test", only.GetProperty("name").GetString());
            Assert.Equal(
                ["smoke"],
                only.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray());
            Assert.Equal(2, only.GetProperty("steps").GetInt32());
            Assert.True(only.GetProperty("readable").GetBoolean());

            // Belt and braces on the containment scenario: not merely "one entry", but "not THAT
            // entry" — a same-count coincidence would otherwise pass.
            var wholeBody = payload.GetRawText();
            Assert.DoesNotContain("other.e2e.yaml", wholeBody, StringComparison.Ordinal);
            Assert.DoesNotContain("secrets", wholeBody, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── vouchfx://runs/{runId}/… ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunVerdictResource_ServesExactlyWhatExplainRunReturnsForThatRun()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var eventsPath = WriteEventsFile(FailingRunEvents);
        var registry = StubRunRegistry.WithCompletedRun(eventsPath, nameof(RunVerdict.Fail));
        var runId = registry.ListRuns()[0].RunId;

        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        var viaResource = await ReadJsonAsync(harness, $"vouchfx://runs/{runId}/verdict", cts.Token);
        var viaTool = await CallToolAsync(
            harness, "explain_run", new Dictionary<string, object?> { ["eventsPath"] = eventsPath }, cts.Token);

        // AC-006's Gherkin: "the content matches what explain_run … would return for that run".
        AssertResourceBodyEqualsToolPayloadWithoutMeta(viaResource, viaTool);

        // Anti-vacuity, and the taxonomy check the whole server is held to: response-string
        // vocabulary, never a wire token (sprint-00-overview.md §5).
        Assert.Equal(nameof(RunVerdict.Fail), viaResource.GetProperty("verdict").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunEventsResource_ServesExactlyWhatGetRunEventsReturnsForThatRun()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var eventsPath = WriteEventsFile(FailingRunEvents);
        var registry = StubRunRegistry.WithCompletedRun(eventsPath, nameof(RunVerdict.Fail));
        var runId = registry.ListRuns()[0].RunId;

        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        var viaResource = await ReadJsonAsync(harness, $"vouchfx://runs/{runId}/events", cts.Token);
        var viaTool = await CallToolAsync(
            harness, "get_run_events", new Dictionary<string, object?> { ["runId"] = runId }, cts.Token);

        AssertResourceBodyEqualsToolPayloadWithoutMeta(viaResource, viaTool);

        // WIRE vocabulary here, deliberately unlike the verdict resource above: a raw-event relay
        // reports the engine's own tokens (sprint-00-overview.md §5).
        var verdicts = viaResource.GetProperty("events").EnumerateArray()
            .Where(e => e.TryGetProperty("verdict", out _))
            .Select(e => e.GetProperty("verdict").GetString())
            .ToArray();
        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, verdict => Assert.Equal("FAIL", verdict));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunLogsResource_IsHonestlyEmpty_NeverFabricated()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var eventsPath = WriteEventsFile(FailingRunEvents);
        var registry = StubRunRegistry.WithCompletedRun(eventsPath, nameof(RunVerdict.Fail));
        var runId = registry.ListRuns()[0].RunId;

        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        var viaResource = await ReadJsonAsync(harness, $"vouchfx://runs/{runId}/logs/orders-api", cts.Token);
        var viaTool = await CallToolAsync(
            harness,
            "get_run_artifacts",
            new Dictionary<string, object?>
            {
                ["runId"] = runId,
                ["kind"] = RunArtifactKind.Logs,
                ["container"] = "orders-api",
            },
            cts.Token);

        AssertResourceBodyEqualsToolPayloadWithoutMeta(viaResource, viaTool);

        // The honesty the AC and this repo's stance (b) require: empty, flagged partial, and the gap
        // NAMED with the upstream ask — not a fabricated line anywhere, and not an error either.
        Assert.Empty(viaResource.GetProperty("logs").EnumerateArray());
        Assert.True(viaResource.GetProperty("partial").GetBoolean());
        Assert.Contains(
            viaResource.GetProperty("gaps").EnumerateArray(),
            gap => gap.GetProperty("field").GetString() == "logs");

        // The container argument is echoed back so a host can confirm the server understood it, even
        // though it selects nothing pre-U4.
        Assert.Equal("orders-api", viaResource.GetProperty("container").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("vouchfx://runs/no-such-run/verdict")]
    [InlineData("vouchfx://runs/no-such-run/events")]
    [InlineData("vouchfx://runs/no-such-run/logs/api")]
    public async Task RunResources_AnUnknownRunId_IsRefusedWithTheSameWordingTheToolsUse(string uri)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var ex = await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token));

        // The shared VFX-E-1505 sentence, so a host reading a tool's refusal and a resource's
        // refusal reads one fact rather than two wordings for one condition.
        Assert.Contains("is in the run registry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("list_runs", ex.Message, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── AC-008: read-only, and hostile arguments refused ───────────────────────────────────────

    [Theory]
    // URL-encoded so the segment survives URI parsing and reaches the handler as a UNC-shaped
    // string — which is exactly how such a value would arrive from a real client.
    [InlineData("vouchfx://runs/%5C%5Cattacker%5Cshare/verdict")]
    [InlineData("vouchfx://runs/%5C%5Cattacker%5Cshare/events")]
    [InlineData("vouchfx://runs/run-1/logs/%5C%5Cattacker%5Cshare")]
    [InlineData("vouchfx://examples/%5C%5Cattacker%5Cshare")]
    [InlineData("vouchfx://schema/%5C%5Cattacker%5Cshare")]
    [InlineData("vouchfx://docs/errors/%5C%5Cattacker%5Cshare")]
    [InlineData("vouchfx-docs:///errors/%5C%5Cattacker%5Cshare")]
    public async Task ANetworkShapedTemplateArgument_IsRefusedByTheArgumentGuardItself(string uri)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var ex = await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token));

        // ASSERTS THE GUARD'S OWN MESSAGE, not merely that something threw (a code review's MAJOR
        // finding: every case here also throws WITHOUT ResourceArgumentGuard, because '\\attacker\share'
        // is not a known run id / example name / schema version either — so a bare "it threw" proved
        // nothing about the security control it claims to cover). This text is produced only by the
        // network/UNC branch, so it is evidence the value was refused for WHAT IT IS, before anything
        // resolved it. Refused rather than resolved matters because reading — or merely probing — a UNC
        // path on Windows triggers an outbound SMB/NTLM authentication to the named host.
        Assert.Contains("network/UNC", ex.Message, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    // %2e%2e%2f is '../'. Percent-encoded because the load-bearing assumption is DECODE-THEN-GUARD
    // ordering: the SDK expands a template argument from the decoded URI, so a traversal that is
    // invisible in the raw string must still meet the guard. Only the %5C%5C cases above pinned that
    // ordering before, and they pin it for one shape (a security review's MINOR).
    [InlineData("vouchfx://examples/%2e%2e%2fsecrets")]
    [InlineData("vouchfx://schema/%2e%2e%2fsecrets")]
    [InlineData("vouchfx://runs/%2e%2e%2fsecrets/verdict")]
    [InlineData("vouchfx://runs/run-1/logs/%2e%2e%2fsecrets")]
    [InlineData("vouchfx://docs/errors/%2e%2e%2fsecrets")]
    public async Task APercentEncodedTraversalArgument_IsRefusedByTheArgumentGuardItself(string uri)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var ex = await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token));

        // The separator/traversal branch's own sentence — again, so this is evidence about the guard
        // rather than about an unrelated lookup failing.
        Assert.Contains("flat identifier", ex.Message, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ReadingEveryNewResource_LeavesTheWorkspaceTreeByteForByteUnchanged()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // AC-008's "every new resource is read-only", asserted as an OBSERVED fact rather than as a
        // protocol argument. MCP has no resources/write method, so "never accepts a write" is
        // trivially true and worth nothing on its own; the claim that can actually regress is that
        // READING one of these resources does not itself write. That is what this measures: the whole
        // workspace tree's file set and every file's bytes, before and after reading all ten
        // resources. The structural half of the same guarantee is ReadOnlySourceGuardTests, whose
        // fail-closed exact-equality list over src/ already covers every file this story adds.
        var root = Directory.CreateTempSubdirectory("vfx-mcp-resource-readonly-");
        try
        {
            var specsDir = Directory.CreateDirectory(Path.Combine(root.FullName, Workspace.SpecsDirectoryName));
            File.WriteAllText(Path.Combine(specsDir.FullName, "inside.e2e.yaml"), GoodSuiteYaml);

            // INSIDE the workspace, not the OS temp directory. Measured: with a workspace configured,
            // the run resources put the registry's recorded events path through the same
            // PathSafetyGuard containment check the tools do, and a temp-directory events file is
            // correctly refused as outside the root. That is the right behaviour and worth recording
            // here — the guard reaches the resource surface, not just the tool surface.
            var runsDir = Directory.CreateDirectory(Path.Combine(root.FullName, ".vouchfx", "runs"));
            var eventsPath = Path.Combine(runsDir.FullName, "events.jsonl");
            File.WriteAllText(eventsPath, FailingRunEvents);

            var registry = StubRunRegistry.WithCompletedRun(eventsPath, nameof(RunVerdict.Fail));
            var runId = registry.ListRuns()[0].RunId;

            var before = SnapshotTree(root);

            await using var harness = await McpTestHarness.StartAsync(
                cts.Token, runRegistry: registry, workspace: Workspace.Resolve(root.FullName));

            foreach (var uri in new[]
            {
                "vouchfx-docs:///language-reference",
                "vouchfx-docs:///recipes",
                "vouchfx-docs:///errors/VFX-E-1002",
                "vouchfx://docs/errors/VFX-E-1002",
                "vouchfx://schema/latest",
                $"vouchfx://examples/{ExampleSuites.HttpSmoke.Name}",
                VouchfxResourceUris.WorkspaceSpecsUri,
                $"vouchfx://runs/{runId}/verdict",
                $"vouchfx://runs/{runId}/events",
                $"vouchfx://runs/{runId}/logs/orders-api",
            })
            {
                var result = await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token);
                Assert.NotEmpty(result.Contents);
            }

            Assert.Equal(before, SnapshotTree(root));
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>Every file under <paramref name="root"/>, as relative path plus content — the read-only baseline.</summary>
    private static IReadOnlyList<string> SnapshotTree(DirectoryInfo root) =>
        [.. Directory
            .EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root.FullName, path)}={File.ReadAllText(path)}")
            .OrderBy(entry => entry, StringComparer.Ordinal)];

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts <paramref name="resourceBody"/> is exactly <paramref name="toolPayload"/> with its
    /// <c>meta</c> stamp removed — the checkable form of AC-006's "serving the same data".
    /// </summary>
    /// <remarks>
    /// Compared as re-serialised JSON text rather than by walking properties, so a field added on one
    /// side and not the other fails immediately instead of being missed by a hand-written comparison
    /// that nobody remembered to extend. <c>meta</c> is the one permitted difference, and
    /// <see cref="ResourceJson"/>'s header records why.
    /// </remarks>
    private static void AssertResourceBodyEqualsToolPayloadWithoutMeta(
        JsonElement resourceBody, JsonElement toolPayload)
    {
        // Anti-vacuity: if the stamp were ever absent from a tool result this comparison would
        // silently become "equal to itself".
        Assert.True(
            toolPayload.TryGetProperty("meta", out _),
            "Expected the tool result to carry the meta stamp every successful tool result carries.");

        var expected = JsonSerializer.Serialize(
            toolPayload.EnumerateObject()
                .Where(property => !property.NameEquals("meta"))
                .ToDictionary(property => property.Name, property => property.Value));

        var actual = JsonSerializer.Serialize(
            resourceBody.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));

        Assert.Equal(expected, actual);
    }

    private static async Task<TextResourceContents> ReadTextAsync(
        McpTestHarness harness, string uri, CancellationToken cancellationToken)
    {
        var result = await harness.Client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
        var content = Assert.Single(result.Contents);
        var text = Assert.IsType<TextResourceContents>(content);

        Assert.Equal(uri, text.Uri);
        return text;
    }

    private static async Task<JsonElement> ReadJsonAsync(
        McpTestHarness harness, string uri, CancellationToken cancellationToken)
    {
        var text = await ReadTextAsync(harness, uri, cancellationToken);

        Assert.Equal(ResourceJson.MimeType, text.MimeType);

        using var document = JsonDocument.Parse(text.Text!);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> CallToolAsync(
        McpTestHarness harness,
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false, $"{toolName} returned an error: {result.Content.Count} block(s).");
        return result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
    }

    private string WriteEventsFile(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"vouchfx-mcp-resource-events-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Never fail a test over temp-directory cleanup.
        }
    }

    private const string FailingRunEvents = """
        {"type":"step-attempt","stepId":"assert-order-status","attempt":1,"outcome":"unmatched","observed":"status 500"}
        {"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
        """;

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
