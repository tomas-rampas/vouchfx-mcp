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
/// together if the content itself changed. The digests below were computed from the committed files
/// at Sprint 5's start (branch base 71189dc, ENGINE_PIN v1.0.0-rc.4), so they are the only assertion
/// here anchored to a point in TIME rather than to the current tree — which is exactly what the AC
/// asks for.
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
    /// type's remarks before changing it.
    /// </summary>
    private const string LanguageReferenceSha256 =
        "c194acd0ed1030a1d94584cdbb04a7119ecb7461ead3ccf7e031047df234f072";

    /// <summary>SHA-256 of <c>vendored/recipes.md</c> as committed at Sprint 5's start.</summary>
    private const string RecipesSha256 =
        "9c0096a5eb190acc25a27978554a6aac265f00237345c8bdcd1acfea646a3d00";

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
