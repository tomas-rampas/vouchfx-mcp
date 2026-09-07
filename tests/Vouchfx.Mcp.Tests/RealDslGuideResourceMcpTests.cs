using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Docs;
using Vouchfx.Mcp.Resources;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-05's wire-facing golden: <c>vouchfx://docs/dsl-guide</c> is advertised on
/// <c>resources/list</c> and serves the repository's own guide, byte for byte, as Markdown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Byte equality against the embedded document, not a "looks like the guide" check.</b>
/// <see cref="DslGuideForAgentsTests"/> already holds the CONTENT honest — its size, its topics, and
/// the schema-validity of every example — by reading <c>docs/dsl-guide-for-agents.md</c> from the
/// repository. What that cannot see is the wire: an embed under the wrong logical name, a stray BOM,
/// or a transport that re-encodes the text would leave every one of those tests green while a host
/// received something different. So this asserts the served string equals
/// <see cref="DslGuideDocument.RawMarkdown"/> exactly, and the repo-file tests supply the meaning of
/// that string. The two halves compose: repo file → embed (this test's <c>Assert.Equal</c> chain
/// back through <c>DslGuideForAgentsTests</c>) → wire.
/// </para>
/// <para>
/// Stdout cleanliness is asserted here as in every other <c>Real*McpTests</c> class: stdout is the
/// JSON-RPC channel and nothing else may ever write to it.
/// </para>
/// </remarks>
public class RealDslGuideResourceMcpTests
{
    [Fact]
    public async Task DslGuideResource_IsAdvertised_WithItsRegisteredNameAndMarkdownMimeType()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);
        var guide = Assert.Single(
            resources,
            resource => string.Equals(resource.Uri, VouchfxResourceUris.DslGuideUri, StringComparison.Ordinal));

        Assert.Equal("vouchfx DSL guide for agents", guide.Name);
        Assert.Equal(ResourceJson.MarkdownMimeType, guide.ProtocolResource.MimeType);

        // The description is what a host reads when deciding whether to spend a read on this. It must
        // say what the guide IS rather than merely that it exists.
        Assert.NotNull(guide.Description);
        Assert.Contains(".e2e.yaml", guide.Description!, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DslGuideResource_ServesTheEmbeddedGuide_ByteForByte()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var read = await harness.Client.ReadResourceAsync(
            VouchfxResourceUris.DslGuideUri, cancellationToken: cts.Token);

        var content = Assert.Single(read.Contents);
        var text = Assert.IsType<TextResourceContents>(content);

        Assert.Equal(DslGuideDocument.RawMarkdown, text.Text);

        // Anti-vacuity: an empty embed would satisfy the equality above against an empty document.
        Assert.True(text.Text.Length > 2_000, $"The served guide is implausibly short ({text.Text.Length} chars).");

        // No byte-order mark reaches a host — DslGuideDocument reads with BOM detection on precisely
        // so an editor-added U+FEFF cannot become the first character of the served text.
        Assert.False(text.Text.StartsWith('﻿'), "The served guide begins with a byte-order mark.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DslGuideResource_IsNotAlsoAdvertisedAsATemplate()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // There is exactly one guide, so it is a CONCRETE resource rather than a one-value template
        // (DslGuideResourceRegistry's own remarks record that choice). A URI in both listings would
        // tell a host two contradictory things about how to use it.
        var templates = await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token);

        Assert.DoesNotContain(
            templates,
            template => string.Equals(
                template.ProtocolResourceTemplate.UriTemplate,
                VouchfxResourceUris.DslGuideUri,
                StringComparison.Ordinal));

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
