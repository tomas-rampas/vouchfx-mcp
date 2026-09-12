using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Observability;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S6-04's wire-level span assertions: every tool call emits exactly one span from THIS SERVER'S
/// <see cref="ActivitySource"/>, named <c>vouchfx.mcp.tool/&lt;toolName&gt;</c>, carrying only the four
/// allowlisted attributes.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="McpTestHarness"/> over the real MCP wire with a plain
/// <see cref="ActivityListener"/> attached (<see cref="SpanRecorder"/>) — the identical seam an
/// OpenTelemetry exporter consumes, with no OTel package on either side of the build. See the
/// amended US-S6-04 test-convention line for why a listener rather than an in-memory exporter.
/// </para>
/// <para>
/// <b>Every test here gives its server its OWN temporary workspace</b> and filters recorded spans by
/// the resulting <c>vouchfx.workspace.hash</c>. That is not incidental tidiness: an
/// <see cref="ActivityListener"/> is process-wide, so without that scoping these assertions would be
/// racing every other test class in a parallel assembly. The class additionally joins
/// <see cref="SpanAssertionGroup"/>. See both types' remarks.
/// </para>
/// </remarks>
[Collection(SpanAssertionGroup.Name)]
public class RealToolSpanMcpTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _testOutput;

    public RealToolSpanMcpTests(Xunit.Abstractions.ITestOutputHelper testOutput) => _testOutput = testOutput;

    /// <summary>
    /// A run id of exactly the shape this server mints ("run-" + 32 lowercase hex) that no registry
    /// will contain — so the argument path is exercised through the shape gate while the lookup
    /// still fails.
    /// </summary>
    private const string WellFormedButAbsentRunId = "run-0123456789abcdef0123456789abcdef";

    /// <summary>A minimal passing event stream for the FakeSuiteRunner-driven success test.</summary>
    private const string PassingEventsFileContent = """
        {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
        """;

    private const string ValidSuiteYaml = """
        steps:
          - id: fine
            type: http.rest
            target: api
            method: GET
            path: /x
        """;

    [Fact]
    public async Task ASuccessfulValidateSuiteCall_EmitsExactlyOneSpanFromThisServersSource()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = ValidSuiteYaml },
            cancellationToken: cts.Token);

        var span = Assert.Single(recorder.ForWorkspaceHash(expectedHash!));

        Assert.Equal("vouchfx.mcp.tool/validate_suite", span.DisplayName);
        Assert.Equal("success", span.GetTagItem("vouchfx.outcome"));
    }

    [Fact]
    public async Task AFailedRunSuiteCall_EmitsASpanWithOutcomeError_AndNeverTheVfxErrorMessage()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = "does-not-exist.e2e.yaml" },
            cancellationToken: cts.Token);

        var span = Assert.Single(recorder.ForWorkspaceHash(expectedHash!));

        Assert.Equal("vouchfx.mcp.tool/run_suite", span.DisplayName);
        Assert.Equal("error", span.GetTagItem("vouchfx.outcome"));

        // The VfxError's MESSAGE FIELD specifically — not the whole serialised error blob, which
        // also contains the code and other short tokens that could coincidentally appear in an
        // attribute and turn this into a test of nothing in particular. The message is the field the
        // AC names and the one that can carry arbitrary text.
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var errorMessage = structured.TryGetProperty("message", out var message)
            ? message.GetString()
            : ErrorTextOf(result);

        Assert.False(
            string.IsNullOrWhiteSpace(errorMessage),
            "Expected run_suite to fail with a message to compare against — without one this assertion is void.");

        foreach (var (_, key, value) in TagsOf(span))
        {
            Assert.DoesNotContain(errorMessage!, value, StringComparison.Ordinal);
            Assert.NotEqual("error.message", key);
        }
    }

    [Fact]
    public async Task ASuiteCarryingACanaryLiteral_LeaksItThroughNoAttributeOfAnySpan()
    {
        const string canary = "CANARY-DO-NOT-LEAK";

        // ALL sources, not just ours: the MCP SDK emits its own span per request, and a leak through
        // ITS attributes would be exactly as real as one through ours. The AC is about what reaches a
        // tracing backend, and a backend receives both.
        using var recorder = SpanRecorder.ForAllSources();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var canarySuite = $"""
            steps:
              - id: canary-step
                type: http.rest
                target: api
                method: GET
                path: /{canary}
                bogusField: {canary}
            """;

        await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = canarySuite },
            cancellationToken: cts.Token);

        var tags = recorder.AllTags();
        Assert.NotEmpty(tags);

        // Anti-vacuity: without this the test would pass just as happily with this server's
        // instrumentation deleted entirely — no spans of ours, therefore no leak of ours.
        Assert.Contains(
            recorder.VouchfxToolSpans(),
            span => span.DisplayName == "vouchfx.mcp.tool/validate_suite");

        foreach (var (spanName, key, value) in tags)
        {
            Assert.False(
                value.Contains(canary, StringComparison.Ordinal),
                $"Span '{spanName}' attribute '{key}' leaked the canary literal: {value}");
        }
    }

    [Fact]
    public async Task NoAttributeOnOurSpans_IsARawFilesystemPath()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = ValidSuiteYaml },
            cancellationToken: cts.Token);

        var span = Assert.Single(recorder.ForWorkspaceHash(expectedHash!));

        // The workspace root itself is the most likely thing to leak, so it is named explicitly
        // rather than approximated by a path-shaped regex.
        foreach (var (_, key, value) in TagsOf(span))
        {
            Assert.DoesNotContain(temp.Workspace.Root, value, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                value.Contains(Path.DirectorySeparatorChar) || value.Contains('/'),
                $"Attribute '{key}' looks like a filesystem path: {value}");
        }
    }

    /// <summary>
    /// AC 2 ("every one of the eighteen tools emits exactly one span"), held STRUCTURALLY: every tool
    /// the registry produces is wrapped by the instrumenting decorator.
    /// </summary>
    /// <remarks>
    /// An earlier revision of this test compared <c>ToolTelemetry.SpanName(tool.Name)</c> to
    /// <c>$"vouchfx.mcp.tool/{tool.Name}"</c>, which is the helper's own implementation restated —
    /// a tautology that would have passed with the decorator deleted entirely. What actually has to
    /// be true is that nothing escapes the wrap, so that is what is asserted here, and it doubles as
    /// the bypass guard: a future <c>WithTools&lt;T&gt;()</c> registration, or an append after
    /// <c>WrapAll</c>, produces an unwrapped tool and fails this.
    /// </remarks>
    [Fact]
    public async Task EveryRegisteredTool_IsWrappedByTheInstrumentingDecorator()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // The collection the server ACTUALLY registered, read out of the production DI configuration
        // — stronger than calling ToolRegistry.CreateAll directly, because it also covers a tool
        // added to options by some other route.
        var registered = harness.Host.Services
            .GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;

        Assert.NotNull(registered);
        Assert.Equal(18, registered.Count);
        Assert.All(registered, tool => Assert.IsType<InstrumentedMcpServerTool>(tool));

        // And the wrapped set really is the advertised set, so "eighteen wrapped tools" cannot be
        // eighteen of something else.
        var advertised = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);

        Assert.Equal(
            registered.Select(tool => tool.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal),
            advertised.Select(tool => tool.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARunLifecycleTool_CarriesTheRunIdAttribute_AndANonRunToolDoesNot()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        // get_run_status takes a runId ARGUMENT, so the decorator can tag it even though the lookup
        // fails — which is the point: the attribute is sourced from the call, not from success. The
        // id must be WELL-FORMED ("run-" + 32 lowercase hex) or the shape gate drops it, which is
        // itself covered below.
        await harness.Client.CallToolAsync(
            "get_run_status",
            new Dictionary<string, object?> { ["runId"] = WellFormedButAbsentRunId },
            cancellationToken: cts.Token);

        // search_docs is not a run-lifecycle tool and must carry no runId at all.
        await harness.Client.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "http" },
            cancellationToken: cts.Token);

        var spans = recorder.ForWorkspaceHash(expectedHash!);

        var runSpan = Assert.Single(spans, s => s.DisplayName == "vouchfx.mcp.tool/get_run_status");
        Assert.Equal(WellFormedButAbsentRunId, runSpan.GetTagItem("vouchfx.run.id"));

        var docsSpan = Assert.Single(spans, s => s.DisplayName == "vouchfx.mcp.tool/search_docs");
        Assert.Null(docsSpan.GetTagItem("vouchfx.run.id"));
    }

    /// <summary>
    /// The canary AC applied to the one attribute a CALLER controls: a hostile string supplied as
    /// <c>runId</c> must reach no span attribute of any source.
    /// </summary>
    /// <remarks>
    /// The other canary test feeds the literal through a suite's CONTENTS, which this server never
    /// puts on a span at all. This one feeds it through the single channel that was genuinely open —
    /// an unbounded caller-supplied argument that was tagged verbatim before the shape gate landed.
    /// It is the regression test for that finding, not a variation on the first canary.
    /// </remarks>
    [Fact]
    public async Task ACanaryPassedAsARunIdArgument_ReachesNoSpanAttributeOfAnySource()
    {
        const string canary = "CANARY-DO-NOT-LEAK";

        using var recorder = SpanRecorder.ForAllSources();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        await harness.Client.CallToolAsync(
            "get_run_status",
            new Dictionary<string, object?> { ["runId"] = $"run-{canary}" },
            cancellationToken: cts.Token);

        var tags = recorder.AllTags();
        Assert.NotEmpty(tags);

        // Anti-vacuity, matching the sibling canary test: without a span of OURS present this would
        // pass just as happily with the instrumentation deleted.
        Assert.Contains(
            recorder.VouchfxToolSpans(),
            span => span.DisplayName == "vouchfx.mcp.tool/get_run_status");

        foreach (var (spanName, key, value) in tags)
        {
            Assert.False(
                value.Contains(canary, StringComparison.Ordinal),
                $"Span '{spanName}' attribute '{key}' leaked a caller-supplied canary: {value}");
        }
    }

    [Fact]
    public async Task AMalformedRunIdArgument_IsDroppedEntirelyRatherThanSanitisedOntoTheSpan()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        await harness.Client.CallToolAsync(
            "get_run_status",
            new Dictionary<string, object?> { ["runId"] = "../../etc/passwd" },
            cancellationToken: cts.Token);

        var span = Assert.Single(recorder.ForWorkspaceHash(expectedHash!));

        // No tag at all — not a scrubbed one. A run id that cannot exist carries no debugging value,
        // and echoing a cleaned-up version would keep the channel the gate exists to close.
        Assert.Null(span.GetTagItem("vouchfx.run.id"));

        // And under no OTHER key either — a key containing "run" in any casing. Asserting only the
        // one name would pass against a future attribute that recorded the same value elsewhere.
        Assert.DoesNotContain(
            span.TagObjects,
            tag => tag.Key.Contains("run", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The RESULT-derived runId path (<c>run_suite</c> mints an id rather than taking one), which the
    /// argument path cannot reach.
    /// </summary>
    /// <remarks>
    /// This path rests on three facts nothing else pinned: that the result's structured payload spells
    /// the property <c>runId</c>, that the serializer's naming policy leaves it camel-cased rather
    /// than renaming it, and that the payload is the tool's own object rather than a wrapper. A
    /// successful run through <see cref="FakeSuiteRunner"/> exercises all three at once — if any
    /// changes, the tag silently disappears, which is exactly the kind of failure a span test that
    /// only looked at error paths would never see.
    /// </remarks>
    [Fact]
    public async Task ASuccessfulRunSuiteCall_TagsTheRunIdItsOwnResultAnnounced()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var suitePath = Path.Combine(temp.Workspace.SpecsDir, "passing.e2e.yaml");
        Directory.CreateDirectory(temp.Workspace.SpecsDir);
        await File.WriteAllTextAsync(suitePath, ValidSuiteYaml.ReplaceLineEndings("\n"), cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = suitePath },
            cancellationToken: cts.Token);

        var span = Assert.Single(recorder.ForWorkspaceHash(expectedHash!));

        Assert.Equal("vouchfx.mcp.tool/run_suite", span.DisplayName);
        Assert.Equal("success", span.GetTagItem("vouchfx.outcome"));

        // The tag must equal the id the CALLER was given — not merely be present, which a stale or
        // invented value would also satisfy.
        var structured = Assert.IsType<JsonElement>(result.StructuredContent);
        var reportedRunId = structured.GetProperty("runId").GetString();

        Assert.False(string.IsNullOrEmpty(reportedRunId), "Expected run_suite's result to carry a runId.");
        Assert.Equal(reportedRunId, span.GetTagItem("vouchfx.run.id"));
    }

    [Fact]
    public async Task OurSpan_IsParentedUnderTheSdksOwnRequestSpanWhenOneExists()
    {
        using var recorder = SpanRecorder.ForAllSources();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        var expectedHash = WorkspaceHash.Of(temp.Workspace);

        await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = ValidSuiteYaml },
            cancellationToken: cts.Token);

        var ours = Assert.Single(recorder.ForWorkspaceHash(expectedHash!));

        // The SDK emits its own span per tools/call. Ours must nest under it rather than starting a
        // second, unrelated trace — otherwise a backend shows two disconnected roots per call.
        Assert.NotNull(ours.Parent ?? (object?)ours.ParentId);
    }

    [Fact]
    public async Task WithAListenerAttached_StdoutStaysByteEmpty()
    {
        // Invariant 11, re-asserted rather than assumed. ActivitySource performs no I/O, but that is
        // an architecture argument; this is the evidence.
        using var consoleOut = new ConsoleOutCapture();
        using var recorder = SpanRecorder.ForAllSources();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: temp.Workspace);

        await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = ValidSuiteYaml },
            cancellationToken: cts.Token);

        Assert.NotEmpty(recorder.VouchfxToolSpans());
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task WithNoListenerAttached_TheCallSucceedsIdenticallyAndNoSpanIsEvenCreated()
    {
        // The additive AC, tested as the real production shape: with nothing listening,
        // ActivitySource.StartActivity returns null, so there is no span object at all — not a span
        // that is created and discarded.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        Assert.False(ToolTelemetry.HasListeners(), "A listener leaked from another test — this assertion is void.");

        var result = await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = ValidSuiteYaml },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Records which <see cref="ActivitySource"/> names are live during a tool call. The MCP SDK's own
    /// source name is a MEASURED fact this build depends on (our span must parent under its span), so
    /// it is pinned here rather than guessed from documentation.
    /// </summary>
    [Fact]
    public async Task TheSourceNamesHeardDuringAToolCall_AreThisServersAndTheSdksOwn()
    {
        using var recorder = SpanRecorder.ForAllSources();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = ValidSuiteYaml },
            cancellationToken: cts.Token);

        var names = recorder.DistinctSourceNames();

        _testOutput.WriteLine("MEASURED distinct ActivitySource names during a tool call: [" + string.Join(", ", names) + "]");

        Assert.Contains(SpanRecorder.ToolActivitySourceName, names);
        Assert.Contains(McpSdkActivitySourceName, names);
    }

    /// <summary>
    /// The MCP C# SDK's own <see cref="ActivitySource"/> name, MEASURED on ModelContextProtocol 2.2.0
    /// (see the test above, which fails if it moves). Recorded because the amended US-S6-04 scopes
    /// "exactly one span" to this server's source, which is only meaningful if the other source in
    /// the process is identified rather than assumed.
    /// </summary>
    public const string McpSdkActivitySourceName = "Experimental.ModelContextProtocol";

    private static (string SpanName, string Key, string Value)[] TagsOf(Activity span) =>
        span.TagObjects
            .Select(tag => (span.DisplayName, tag.Key, tag.Value?.ToString() ?? string.Empty))
            .ToArray();

    private static string ErrorTextOf(CallToolResult result) =>
        string.Join(
            " ",
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
