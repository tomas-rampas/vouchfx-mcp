using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 5 (REQ-005) end to end, through the same in-memory MCP harness
/// <c>McpServerSkeletonTests</c>/<c>RealToolsMcpTests</c> use: the two vendored-document resources
/// (<c>resources/list</c>, <c>resources/read</c>) and the now-real <c>search_docs</c> tool, calling
/// the REAL registrations and REAL handlers (not stubs) via real <c>resources/*</c> and
/// <c>tools/call</c> round trips.
/// </summary>
/// <remarks>
/// Service-level behaviour (slug generation, section parsing, search scoring) is covered directly
/// against <see cref="Vouchfx.Mcp.Docs.MarkdownSlugifier"/>, <see cref="Vouchfx.Mcp.Docs.MarkdownDocumentParser"/>,
/// and <see cref="Vouchfx.Mcp.Docs.DocSearchService"/> in <c>Docs/*Tests.cs</c>; these tests instead
/// confirm the MCP-facing contract: the resources and tool are wired up, return the expected
/// shapes, and never crash the server.
/// </remarks>
public class RealDocsMcpTests
{
    // ── resources/list ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResourcesList_IncludesBothVendoredDocuments()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);

        Assert.Equal(2, resources.Count);

        var languageReference = Assert.Single(resources, r => r.Uri == VendoredDocuments.LanguageReference.ResourceUri);
        Assert.Equal(VendoredDocuments.LanguageReference.Title, languageReference.Name);
        Assert.False(string.IsNullOrWhiteSpace(languageReference.Description));
        Assert.Equal("text/markdown", languageReference.MimeType);

        var recipes = Assert.Single(resources, r => r.Uri == VendoredDocuments.Recipes.ResourceUri);
        Assert.Equal(VendoredDocuments.Recipes.Title, recipes.Name);
        Assert.False(string.IsNullOrWhiteSpace(recipes.Description));
        Assert.Equal("text/markdown", recipes.MimeType);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── resources/read ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("vouchfx-docs:///language-reference", "language-reference")]
    [InlineData("vouchfx-docs:///recipes", "recipes")]
    public async Task ResourcesRead_ReturnsTheFullEmbeddedMarkdownTextVerbatim(string uri, string docId)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token);

        var content = Assert.Single(result.Contents);
        var textContent = Assert.IsType<TextResourceContents>(content);

        Assert.Equal("text/markdown", textContent.MimeType);
        Assert.False(string.IsNullOrEmpty(textContent.Text));
        Assert.Equal(VendoredDocRepository.GetRawText(docId), textContent.Text);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ResourcesRead_ServerStaysResponsiveAfterwards()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await harness.Client.ReadResourceAsync(VendoredDocuments.Recipes.ResourceUri, cancellationToken: cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(7, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── search_docs ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchDocs_VerifyModeQuery_ReturnsRetryAndImmediateDocumentationWithAVouchfxIoLink()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "search_docs", new() { ["query"] = "verifyMode" }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        var matches = payload.GetProperty("matches").EnumerateArray().ToArray();

        Assert.NotEmpty(matches);
        Assert.Contains(matches, m =>
            m.GetProperty("snippet").GetString()!.Contains("RETRY", StringComparison.Ordinal) &&
            m.GetProperty("url").GetString()!.StartsWith("https://", StringComparison.Ordinal) &&
            m.GetProperty("url").GetString()!.Contains("vouchfx.io", StringComparison.Ordinal));

        Assert.Contains(matches, m =>
            m.GetProperty("snippet").GetString()!.Contains("IMMEDIATE", StringComparison.Ordinal) &&
            m.GetProperty("snippet").GetString()!.Contains("RETRY", StringComparison.Ordinal));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task SearchDocs_NoMatchQuery_ReturnsAnEmptyStructuredResultWithoutCrashingServer()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "search_docs", new() { ["query"] = "zzz-nonexistent-xyz-999" }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        Assert.Empty(payload.GetProperty("matches").EnumerateArray());

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(7, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task SearchDocs_HostileControlCharacterQuery_SanitisesTheEchoedQueryAndServerStaysResponsive()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var disallowedByte = ((char)27).ToString();
        var result = await CallToolAsync(harness, "search_docs", new() { ["query"] = $"verifyMode{disallowedByte}" }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);
        var echoedQuery = payload.GetProperty("query").GetString()!;

        Assert.DoesNotContain(disallowedByte, echoedQuery, StringComparison.Ordinal);
        foreach (var c in echoedQuery)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(7, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static ValueTask<CallToolResult> CallToolAsync(
        McpTestHarness harness, string toolName, Dictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static JsonElement GetStructuredContent(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");
}
