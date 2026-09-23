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
/// DIFFERENT points, because each moves only when its own document does. The v1.0.0-rc.6 resync
/// changed both, so both are now anchored there; before it, <c>language-reference.md</c> still
/// carried its Sprint 5 start value (ENGINE_PIN v1.0.0-rc.4, which the rc.5 resync left
/// byte-identical) while <c>recipes.md</c> was anchored to the v1.0.0-rc.5 resync.
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
    /// SHA-256 of <c>vendored/language-reference.md</c> as committed at the ENGINE_PIN bump to
    /// <c>v1.0.0-rc.6</c> (commit <c>93287ffbb0623ba253816ed4d909f50e1b26da93</c>), whose resync
    /// changed it (32,115 → 33,132 bytes) — see this type's remarks before changing it. Its previous
    /// anchor, Sprint 5's start at <c>v1.0.0-rc.4</c> and unchanged through the rc.5 resync, was
    /// <c>c194acd0ed1030a1d94584cdbb04a7119ecb7461ead3ccf7e031047df234f072</c>.
    /// </summary>
    private const string LanguageReferenceSha256 =
        "6fa275b22682bf265a29b38268629aeb0ddd70a1041a9065c94bb930d81d5882";

    /// <summary>
    /// SHA-256 of <c>vendored/recipes.md</c> as committed at the ENGINE_PIN bump to
    /// <c>v1.0.0-rc.6</c> (commit <c>93287ffbb0623ba253816ed4d909f50e1b26da93</c>), whose resync
    /// changed it (52,325 → 52,737 bytes) — see this type's remarks before changing it. Its previous
    /// anchors: <c>ffa2ca4fccac329de685386519432de984f14e6f36ada875897186d9a66d3ffb</c> at the
    /// <c>v1.0.0-rc.5</c> bump, and <c>9c0096a5eb190acc25a27978554a6aac265f00237345c8bdcd1acfea646a3d00</c>
    /// at Sprint 5's start (<c>v1.0.0-rc.4</c>).
    /// </summary>
    private const string RecipesSha256 =
        "3c0a06b56b6b64a8cc9ccb792474b5719ce318273a263fe21e32ac625b6d7aee";

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
