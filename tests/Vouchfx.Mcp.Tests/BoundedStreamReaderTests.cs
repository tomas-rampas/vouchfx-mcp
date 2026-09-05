using System.Text;
using System.Text.Json;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers <see cref="BoundedStreamReader.ReadUpToAsync"/>'s decode contract — the seam the issue #70
/// fix turns on. The reader now decodes with a CALLER-SUPPLIED <see cref="Encoding"/> (UTF-8 by
/// default) instead of a hardcoded UTF-8, so the engine-relay path
/// (<see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/>) can decode with the Windows console output
/// code page while the worker-relay path (<see cref="ValidationWorkerClient"/>) stays UTF-8.
/// </summary>
public class BoundedStreamReaderTests
{
    static BoundedStreamReaderTests()
    {
        // cp852 / cp1252 are OEM/ANSI code pages the in-box runtime does not provide an Encoding for
        // without this provider (the production code registers it too, in
        // VouchfxCliProcessRunner.ResolveEngineOutputEncoding). Idempotent, process-global.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Task<string?> ReadAllAsync(byte[] bytes, Encoding? encoding = null) =>
        BoundedStreamReader.ReadUpToAsync(
            new MemoryStream(bytes), maxBytes: 1024 * 1024, onExceeded: () => { }, encoding);

    [Fact]
    public async Task ReadUpToAsync_WithInjectedCp852Encoding_DecodesTheSectionSign()
    {
        // 0xF5 is 'section sign' (§, U+00A7) in cp852 — the exact byte MEASURED coming off
        // `vouchfx schema` under a cp852 console. The injected encoding must recover it.
        var decoded = await ReadAllAsync(new byte[] { 0xF5 }, Encoding.GetEncoding(852));

        Assert.Equal("§", decoded); // §
    }

    [Fact]
    public async Task ReadUpToAsync_WithDefaultUtf8_TreatsTheSameCp852ByteAsInvalid()
    {
        // The pre-fix behaviour, pinned so a regression to hardcoded UTF-8 for the engine relay would
        // be caught: 0xF5 is not a valid UTF-8 lead byte, so a UTF-8 decode yields the replacement
        // character U+FFFD — NOT the section sign. This is precisely the corruption #70 is about.
        var decoded = await ReadAllAsync(new byte[] { 0xF5 });

        Assert.Equal("�", decoded);
    }

    [Fact]
    public async Task ReadUpToAsync_WithInjectedCp1252Encoding_DecodesEmDashAndEllipsis()
    {
        // cp1252 CAN represent every character the pinned schema uses: 0x97 em-dash, 0x85 ellipsis,
        // 0xA7 section sign — the MEASURED bytes under a forced cp1252 console, where the fix is
        // complete (clean cross-verification). Proves the injected encoding recovers all three.
        var decoded = await ReadAllAsync(new byte[] { 0x97, 0x85, 0xA7 }, Encoding.GetEncoding(1252));

        Assert.Equal("—…§", decoded); // — … §
    }

    [Fact]
    public async Task ReadUpToAsync_DefaultEncoding_IsUtf8_AndRoundTripsMultibyteContent()
    {
        // Non-Windows relays and the worker path pass no encoding and must get UTF-8. Feeding the
        // UTF-8 bytes of multibyte text back through the default decode must reproduce it exactly.
        const string original = "café — 注文 …";
        var decoded = await ReadAllAsync(Encoding.UTF8.GetBytes(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public async Task ReadUpToAsync_WorkerRelayPath_RoundTripsAsciiEscapedJson_UnderDefaultUtf8()
    {
        // The worker-relay path (ValidationWorkerClient) must STAY UTF-8. That is correct precisely
        // because ValidationWorkerProtocol.JsonOptions escapes every non-ASCII character as \uXXXX,
        // so the worker's stdout is pure ASCII — and a UTF-8 decode of ASCII is lossless. This proves
        // both halves: the serialised bytes are all ASCII, and the default decode round-trips them.
        var json = JsonSerializer.Serialize("café 注文 — …", ValidationWorkerProtocol.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.All(bytes, b => Assert.True(b < 0x80, $"Worker JSON must be pure ASCII; found byte 0x{b:X2}."));

        var decoded = await ReadAllAsync(bytes); // no encoding => UTF-8, the worker-relay default
        Assert.Equal(json, decoded);
    }

    [Fact]
    public async Task ReadUpToAsync_WhenCapExceeded_InvokesCallbackAndReturnsNull()
    {
        // The cap contract is orthogonal to the decode change but must survive it: exceeding the cap
        // invokes onExceeded and returns null regardless of the encoding chosen.
        var exceeded = false;
        var result = await BoundedStreamReader.ReadUpToAsync(
            new MemoryStream(new byte[64]), maxBytes: 8, onExceeded: () => exceeded = true,
            Encoding.GetEncoding(852));

        Assert.Null(result);
        Assert.True(exceeded);
    }
}
