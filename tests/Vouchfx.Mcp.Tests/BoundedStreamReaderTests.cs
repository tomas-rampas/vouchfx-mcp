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
        // without this provider (the production code registers it too, in Cli/EngineOutputEncoding —
        // which is where that resolution moved in issue #89, so get_schema and this decode path
        // cannot end up with two different answers). Idempotent, process-global.
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

    // ── ReadDecodedAsync (vouchfx-mcp#115): the STREAMING sibling VouchfxCliSuiteRunner's live relay
    // uses. Its defining property is surviving a multi-byte sequence split across a chunk boundary —
    // ReadUpToAsync above never has to, because it decodes once from a single accumulated buffer. ──

    private static async Task<string> ReadAllDecodedAsync(Stream stream, Encoding encoding)
    {
        var result = new StringBuilder();
        await BoundedStreamReader.ReadDecodedAsync(
            stream, encoding, (buffer, count) => result.Append(buffer, 0, count));
        return result.ToString();
    }

    [Fact]
    public async Task ReadDecodedAsync_OrdinaryAsciiText_ArrivesUnchanged()
    {
        var decoded = await ReadAllDecodedAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("Starting DCP...")), Encoding.UTF8);

        Assert.Equal("Starting DCP...", decoded);
    }

    [Fact]
    public async Task ReadDecodedAsync_MultiByteUtf8TextDeliveredInOneChunk_DecodesCorrectly()
    {
        const string original = "café — 注文 …";
        var decoded = await ReadAllDecodedAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(original)), Encoding.UTF8);

        Assert.Equal(original, decoded);
    }

    /// <summary>
    /// THE load-bearing case (vouchfx-mcp#115): <see cref="OneByteAtATimeStream"/> forces EVERY
    /// multi-byte UTF-8 sequence in <c>original</c> to straddle a separate
    /// <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> call — the é (2-byte: C3 A9),
    /// each em dash (3-byte: E2 80 94) and 注/文 (3-byte each) all split at the worst possible point.
    /// A naive per-chunk <c>Encoding.GetString(chunk)</c> decode (the mistake
    /// <see cref="BoundedStreamReader.ReadDecodedAsync"/>'s remarks name) would turn every one of
    /// these into replacement characters; the single stateful <see cref="Decoder"/> underneath it must
    /// not.
    /// </summary>
    [Fact]
    public async Task ReadDecodedAsync_MultiByteUtf8SequenceSplitAcrossChunkBoundary_DecodesIntact()
    {
        const string original = "café — 注文 …done";
        using var stream = new OneByteAtATimeStream(Encoding.UTF8.GetBytes(original));

        var decoded = await ReadAllDecodedAsync(stream, Encoding.UTF8);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public async Task ReadDecodedAsync_WithInjectedCp852Encoding_DecodesTheSectionSign()
    {
        // The same MEASURED cp852 byte ReadUpToAsync_WithInjectedCp852Encoding_... uses above,
        // proving ReadDecodedAsync's caller-supplied encoding is genuinely used, not just accepted.
        var decoded = await ReadAllDecodedAsync(new MemoryStream(new byte[] { 0xF5 }), Encoding.GetEncoding(852));

        Assert.Equal("§", decoded);
    }

    [Fact]
    public async Task ReadDecodedAsync_EmptyStream_ProducesEmptyTextWithoutThrowing()
    {
        var decoded = await ReadAllDecodedAsync(new MemoryStream(), Encoding.UTF8);

        Assert.Equal(string.Empty, decoded);
    }

    /// <summary>
    /// A minimal readable-only <see cref="Stream"/> that returns AT MOST ONE byte per
    /// <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> call, regardless of how large a
    /// buffer the caller offers — the worst-case chunk granularity, used to force a multi-byte
    /// sequence to split at every possible boundary rather than just one convenient one.
    /// </summary>
    private sealed class OneByteAtATimeStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= bytes.Length || buffer.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = bytes[_position];
            _position++;
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
