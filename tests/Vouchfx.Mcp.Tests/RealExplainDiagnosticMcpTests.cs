using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;
using Vouchfx.Mcp.Resources;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S1-05 end to end, through the same in-memory MCP harness every other <c>Real*McpTests</c> class
/// uses: the <c>explain_diagnostic</c> tool's success and error shapes, and its content parity with
/// the <c>vouchfx-docs:///errors/{code}</c> resource template — both served from the SAME embedded
/// bytes (<see cref="DiagnosticPageRepository"/>).
/// </summary>
public class RealExplainDiagnosticMcpTests
{
    [Fact]
    public async Task ExplainDiagnostic_CatalogueCode_ReturnsTitleExplanationCausesFixesAndDocsUrl()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "explain_diagnostic",
            new Dictionary<string, object?> { ["code"] = VfxCodeCatalogue.SuiteFileNotFound },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = Structured(result);

        Assert.Equal(VfxCodeCatalogue.SuiteFileNotFound, payload.GetProperty("code").GetString());
        Assert.Equal("SuiteFileNotFound", payload.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("explanation").GetString()));
        Assert.NotEmpty(payload.GetProperty("commonCauses").EnumerateArray());
        Assert.NotEmpty(payload.GetProperty("fixes").EnumerateArray());
        Assert.Equal(
            $"https://vouchfx-mcp.vouchfx.io/docs/errors/{VfxCodeCatalogue.SuiteFileNotFound}.html",
            payload.GetProperty("docsUrl").GetString());

        // Success results carry the US-S1-02 meta stamp like every other tool.
        Assert.True(payload.TryGetProperty("meta", out _));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("VFX-E-1850")] // Inside spec §4.4's deliberate 1800-1899 gap — never catalogued.
    [InlineData("not-a-code-at-all")]
    public async Task ExplainDiagnostic_UnknownCode_ReturnsUnknownDiagnosticCodeError(string code)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "explain_diagnostic",
            new Dictionary<string, object?> { ["code"] = code },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var error = SingleVfxError(result);

        Assert.Equal(VfxCodeCatalogue.UnknownDiagnosticCode, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Contains(code, error.GetProperty("message").GetString()!, StringComparison.Ordinal);

        // The server survives an unknown code and keeps advertising every tool, including this one.
        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(13, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainDiagnostic_HostileControlCharacterCode_SanitisesTheEchoedCode()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var disallowedByte = ((char)27).ToString();
        var result = await harness.Client.CallToolAsync(
            "explain_diagnostic",
            new Dictionary<string, object?> { ["code"] = $"VFX-E-9999{disallowedByte}" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var error = SingleVfxError(result);
        var message = error.GetProperty("message").GetString()!;

        Assert.DoesNotContain(disallowedByte, message, StringComparison.Ordinal);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── vouchfx-docs:///errors/{code} — same content the tool returns ─────────────────────────

    [Fact]
    public async Task ResourceTemplate_IsAdvertised()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var templates = await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token);

        Assert.Contains(templates, t => t.ProtocolResourceTemplate.UriTemplate == DiagnosticResourceRegistry.UriTemplate);

        // Static (non-templated) resources/list stays exactly the two vendored documents —
        // unaffected by this templated resource existing alongside them.
        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);
        Assert.Equal(2, resources.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("VFX-E-1002")]
    [InlineData("VFX-D-1201")]
    [InlineData("VFX-E-1903")]
    public async Task ResourceRead_ReturnsTheSameRawTextTheRepositoryServes(string code)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.ReadResourceAsync($"vouchfx-docs:///errors/{code}", cancellationToken: cts.Token);

        var content = Assert.Single(result.Contents);
        var textContent = Assert.IsType<TextResourceContents>(content);

        Assert.Equal("text/markdown", textContent.MimeType);
        Assert.Equal(DiagnosticPageRepository.GetRawText(code), textContent.Text);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ResourceRead_UnknownCode_ReturnsAProtocolErrorWithoutCrashingServer()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Client.ReadResourceAsync("vouchfx-docs:///errors/VFX-E-1850", cancellationToken: cts.Token).AsTask());

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(13, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Acceptance criterion 3: tool-vs-resource content parity for at least one code ─────────

    [Theory]
    [InlineData("VFX-E-1002")]
    [InlineData("VFX-E-1401")]
    public async Task ExplainDiagnosticTool_AndDiagnosticResource_AgreeOnTitleExplanationCausesAndFixes(string code)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var toolResult = await harness.Client.CallToolAsync(
            "explain_diagnostic", new Dictionary<string, object?> { ["code"] = code }, cancellationToken: cts.Token);
        Assert.False(toolResult.IsError ?? false);
        var toolPayload = Structured(toolResult);

        var resourceResult = await harness.Client.ReadResourceAsync($"vouchfx-docs:///errors/{code}", cancellationToken: cts.Token);
        var resourceText = Assert.IsType<TextResourceContents>(Assert.Single(resourceResult.Contents)).Text;
        var resourcePage = DiagnosticPageParser.Parse(resourceText);

        // Both access paths must describe the SAME page — parsed from the SAME embedded bytes the
        // tool itself parsed — so a host can never see the tool and the resource disagree about
        // what a code means.
        Assert.Equal(resourcePage.Code, toolPayload.GetProperty("code").GetString());
        Assert.Equal(resourcePage.Title, toolPayload.GetProperty("title").GetString());
        Assert.Equal(resourcePage.Explanation, toolPayload.GetProperty("explanation").GetString());
        Assert.Equal(
            resourcePage.CommonCauses,
            toolPayload.GetProperty("commonCauses").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            resourcePage.Fixes,
            toolPayload.GetProperty("fixes").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static JsonElement Structured(CallToolResult result) =>
        result.StructuredContent ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");

    private static JsonElement SingleVfxError(CallToolResult result)
    {
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));

        using var document = JsonDocument.Parse(content.Text);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.Clone();
    }
}
