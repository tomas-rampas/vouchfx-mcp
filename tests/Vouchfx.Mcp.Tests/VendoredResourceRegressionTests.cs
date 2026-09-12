using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-01 AC-007: the two pre-existing <c>vouchfx-docs:///</c> resources are BYTE-UNCHANGED by this
/// sprint — "a regression test reads both and asserts byte-identical content to before this sprint".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a recorded SHA-256 and not just "equals the embedded resource".</b> Comparing the served
/// text to the embedded bytes proves the SERVING path is faithful, and
/// <c>VendoredArtefactsTests</c> already proves the embedded bytes equal the committed
/// <c>vendored/</c> files. Neither says anything about "before this sprint": both sides would move
/// together if the content itself changed. Each digest below is anchored to a point in TIME rather
/// than to the current tree — which is exactly what the AC asks for — and the two are anchored to
/// DIFFERENT points, because each moves only when its own document does:
/// <c>language-reference.md</c> still carries its Sprint 5 start value (branch base 71189dc,
/// ENGINE_PIN v1.0.0-rc.4) because the rc.5 resync left it byte-identical, while
/// <c>recipes.md</c> is re-anchored to the Sprint 6 v1.0.0-rc.5 resync that changed it.
/// </para>
/// <para>
/// <b>When this test legitimately fails.</b> Advancing <c>ENGINE_PIN</c> and re-running
/// <c>scripts/sync-vendored.ps1 -Update</c> changes these documents on purpose; the digests are then
/// updated in the same commit, alongside the pin, and the pairing is the point — a vendored document
/// changing WITHOUT a pin bump is a hand-edit, which <c>vendored/README.md</c> forbids outright. Do
/// not "fix" a failure here by pasting in the new hash without establishing which of the two happened.
/// </para>
/// <para>
/// The read is driven over the real MCP wire rather than against
/// <c>VendoredDocRepository</c>, because the regression the AC is worried about is a SPRINT 5 change
/// to the resource layer (a new aggregator, a new registration site, a second scheme registered
/// alongside), and only a wire read exercises all of it.
/// </para>
/// </remarks>
public class VendoredResourceRegressionTests
{
    /// <summary>
    /// SHA-256 of <c>vendored/language-reference.md</c> as committed at Sprint 5's start — see this
    /// type's remarks before changing it. Still the Sprint 5 value: the Sprint 6 rc.4→rc.5 resync
    /// left this document byte-identical, so its anchor did not move.
    /// </summary>
    private const string LanguageReferenceSha256 =
        "c194acd0ed1030a1d94584cdbb04a7119ecb7461ead3ccf7e031047df234f072";

    /// <summary>
    /// SHA-256 of <c>vendored/recipes.md</c> as committed at the Sprint 6 ENGINE_PIN bump to
    /// <c>v1.0.0-rc.5</c> (commit <c>cc5e8efa9c84f59e1135568456f7c156261f6263</c>), which moved this
    /// document — see this type's remarks before changing it. Its previous anchor, Sprint 5's start
    /// at <c>v1.0.0-rc.4</c>, was <c>9c0096a5eb190acc25a27978554a6aac265f00237345c8bdcd1acfea646a3d00</c>.
    /// </summary>
    private const string RecipesSha256 =
        "ffa2ca4fccac329de685386519432de984f14e6f36ada875897186d9a66d3ffb";

    public static TheoryData<string, string> VendoredResources() => new()
    {
        { VendoredDocuments.LanguageReference.ResourceUri, LanguageReferenceSha256 },
        { VendoredDocuments.Recipes.ResourceUri, RecipesSha256 },
    };

    [Theory]
    [MemberData(nameof(VendoredResources))]
    public async Task ResourceRead_ReturnsContentByteIdenticalToBeforeThisSprint(string uri, string expectedSha256)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token);

        var content = Assert.Single(result.Contents);
        var text = Assert.IsType<TextResourceContents>(content);

        Assert.Equal("text/markdown", text.MimeType);
        Assert.Equal(uri, text.Uri);

        // UTF-8, because that is the encoding the file is committed in and the encoding
        // VendoredDocRepository decodes it from — hashing anything else would be hashing this test's
        // own choice rather than the document.
        Assert.Equal(expectedSha256, Sha256Hex(text.Text ?? string.Empty));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [MemberData(nameof(VendoredResources))]
    public async Task ResourceRead_StillReturnsExactlyTheEmbeddedText(string uri, string expectedSha256)
    {
        _ = expectedSha256;

        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var document = VendoredDocuments.All.Single(candidate => candidate.ResourceUri == uri);

        var result = await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));

        // The serving path is faithful — nothing between the manifest resource and the wire
        // normalises line endings, trims, or re-encodes.
        Assert.Equal(VendoredDocRepository.GetRawText(document.Id), text.Text);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task TheirAdvertisedMetadata_IsUnchangedByTheNewSchemeExistingAlongsideThem()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);

        foreach (var document in VendoredDocuments.All)
        {
            var advertised = Assert.Single(resources, resource => resource.Uri == document.ResourceUri);

            // Name, description and MIME type are cached and displayed by hosts just as the content
            // is; "unchanged" has to cover the metadata or a host's resource picker still changes.
            Assert.Equal(document.Title, advertised.Name);
            Assert.Equal(document.Description, advertised.Description);
            Assert.Equal("text/markdown", advertised.MimeType);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
