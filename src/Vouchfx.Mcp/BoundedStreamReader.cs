using System.Text;

namespace Vouchfx.Mcp;

/// <summary>
/// Reads a <see cref="Stream"/> to the end as text in a caller-supplied <see cref="Encoding"/>
/// (UTF-8 by default), unless doing so would exceed a caller-supplied byte cap — shared by every
/// process-spawning boundary in this server that must never buffer an untrusted or
/// potentially-runaway child process's output without limit:
/// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/> (the <c>validate_suite</c> worker)
/// and <see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/> (the <c>vouchfx</c> CLI relay).
/// <see cref="ReadDecodedAsync"/> is the STREAMING sibling of that same decode contract, for
/// <see cref="Vouchfx.Mcp.Run.VouchfxCliSuiteRunner"/>'s live progress relay (vouchfx-mcp#115), which
/// must react to output as the child runs rather than only once it has exited.
/// </summary>
public static class BoundedStreamReader
{
    /// <summary>
    /// Reads <paramref name="stream"/> to the end as text decoded with <paramref name="encoding"/>
    /// (UTF-8 when <see langword="null"/>), unless doing so would exceed <paramref name="maxBytes"/>
    /// — in which case <paramref name="onExceeded"/> is invoked and this returns
    /// <see langword="null"/> without reading further.
    /// </summary>
    /// <param name="stream">The stream to drain.</param>
    /// <param name="maxBytes">The inclusive byte cap; exceeding it aborts the read.</param>
    /// <param name="onExceeded">Invoked exactly once if the cap is breached.</param>
    /// <param name="encoding">
    /// How the accumulated bytes are decoded. <see langword="null"/> means UTF-8 — the correct and
    /// unchanged choice for <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>, whose worker
    /// serialises its result with <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> so its
    /// stdout is always pure ASCII (see <c>ValidationWorkerProtocol.JsonOptions</c>), and for every
    /// non-Windows relay. <see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/> passes the Windows
    /// console output code page instead — see the next paragraph.
    /// </param>
    /// <remarks>
    /// <para>
    /// Reads with <see cref="CancellationToken.None"/> deliberately: this keeps running in the
    /// background even after the caller has moved on (a kill, a cap breach elsewhere, or the
    /// caller's own cancellation) — <see cref="ObserveQuietly"/> is how a caller that no longer
    /// needs the result stops caring about it without forcibly aborting the read itself.
    /// </para>
    /// <para>
    /// <b>Why the decode is now caller-chosen (issue #70).</b> A .NET child writes its redirected
    /// stdout in the CONSOLE'S ACTIVE output code page (<c>GetConsoleOutputCP</c>), NOT UTF-8, so a
    /// hardcoded UTF-8 decode corrupts every non-ASCII engine byte on any non-65001 console.
    /// <c>VouchfxCliProcessRunner</c> therefore passes that console code page here (resolved by
    /// <see cref="Vouchfx.Mcp.Cli.EngineOutputEncoding"/>), which is what actually fixes the
    /// corruption. Note that <c>ProcessStartInfo.StandardOutputEncoding</c> would NOT have fixed it:
    /// it only governs how <c>Process.StandardOutput</c>'s own <c>StreamReader</c> decodes, whereas
    /// this method reads the raw <c>BaseStream</c> bytes and decodes them itself — so the decode HERE
    /// is what had to change.
    /// </para>
    /// <para>
    /// <b>Honest scope of that fix.</b> Decoding with the console code page recovers only the
    /// characters that code page can REPRESENT. When the engine writes a character its console code
    /// page cannot encode, .NET best-fit-maps it AT THE SOURCE, before any byte reaches this method,
    /// and no parent-side decode can recover it. MEASURED under cp852 against the pinned schema:
    /// <c>§</c> (byte <c>0xF5</c>) IS recovered by the cp852 decode; <c>—</c> is best-fit-mapped to
    /// <c>-</c> and <c>…</c> to a raw <c>0x07</c> (which breaks JSON parsing) before we see them, so
    /// both remain lost. On a console whose code page CAN represent every schema character (e.g.
    /// Windows-1252) the decode is exact: MEASURED CLEAN.
    /// One residual is not purely benign loss: on a cp1252/Latin-1 console a PATH-hijacked engine's
    /// high bytes can decode to C1 control characters (U+0080–U+009F) rather than U+FFFD. Immaterial
    /// to safety — every diagnostic sink escapes non-0x20–0x7E via <c>TextSanitiser</c> and the
    /// JSON-RPC wire serialises them — but the decode is not exclusively lossy transcoding.
    /// The remaining OEM-console gap's only complete fix is the engine emitting UTF-8 when its output
    /// is redirected — an engine-side ask, for which #70 stays open.
    /// </para>
    /// <para>
    /// <b>What that residual no longer costs (issue #89).</b> This paragraph used to end with
    /// "<c>get_schema</c>'s cross-verification still reports VFX-D-1106 … <c>chcp 65001</c> before
    /// starting this server is a full workaround". Both halves were true of the code as it then
    /// stood and are obsolete now: <c>GetSchemaOrchestrator</c> compares the live export against the
    /// vendored document PROJECTED through this same encoding, so an unrecoverable best-fit mapping
    /// that altered both identically is no longer reported as schema drift, and no console
    /// reconfiguration is needed to get a clean cross-verification. The loss described above is still
    /// real — the bytes are still gone — it simply no longer produces a per-call false positive. Note
    /// this was never only a terminal problem: MEASURED 2026-09-15, a parent on a WINDOW-LESS CONSOLE
    /// (<c>CreateNoWindow = true</c>) and a window-less-console child both read the machine's OEM page
    /// (852 on that host), not 0 and not UTF-8, so a headless MCP deployment was affected identically.
    /// A GENUINELY console-less process (<c>DETACHED_PROCESS</c>, a service host) is a different and
    /// UNMEASURED configuration — see <see cref="Vouchfx.Mcp.Cli.EngineOutputEncoding"/>'s named
    /// residual for what is inferred about it.
    /// </para>
    /// </remarks>
    public static async Task<string?> ReadUpToAsync(
        Stream stream, long maxBytes, Action onExceeded, Encoding? encoding = null)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, CancellationToken.None)) > 0)
        {
            if (buffer.Length + bytesRead > maxBytes)
            {
                onExceeded();
                return null;
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return (encoding ?? Encoding.UTF8).GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to completion as raw byte chunks, decoding each chunk through a
    /// SINGLE stateful <see cref="Decoder"/> built once from <paramref name="encoding"/>, and handing
    /// the decoded characters to <paramref name="onChars"/> as each chunk arrives — the STREAMING
    /// counterpart to <see cref="ReadUpToAsync"/>'s "buffer everything, decode once at the end" shape,
    /// for a caller that must react to output AS the child runs rather than only once it has exited
    /// (<see cref="Vouchfx.Mcp.Run.VouchfxCliSuiteRunner"/>'s live progress relay — vouchfx-mcp#115).
    /// </summary>
    /// <param name="stream">The stream to drain.</param>
    /// <param name="encoding">
    /// How the bytes are decoded — <see cref="Vouchfx.Mcp.Cli.EngineOutputEncoding.Current"/> for the
    /// engine's own redirected output, exactly as <see cref="ReadUpToAsync"/> is already called with
    /// it. Caller-supplied and never defaulted, so a test can inject a fixed encoding directly without
    /// needing <c>EngineOutputEncoding</c>'s Windows-console resolution.
    /// </param>
    /// <param name="onChars">
    /// Invoked once per decoded chunk with the buffer and the valid character count. The SAME buffer
    /// instance is reused across calls (this method owns it, not the caller), so it must be consumed
    /// before returning — safe here because every invocation is awaited synchronously between reads,
    /// never handed off.
    /// </param>
    /// <remarks>
    /// <b>Why a stateful <see cref="Decoder"/> rather than per-chunk <see cref="Encoding.GetString(byte[])"/>
    /// (the mistake this method exists to avoid).</b> A byte stream read in fixed-size chunks can split
    /// a multi-byte sequence exactly at a chunk boundary. SBCS code pages (cp852, Windows-1252, …)
    /// cannot suffer this — one byte is always one character there — but nothing here is entitled to
    /// assume the resolved encoding stays single-byte forever: non-Windows resolves UTF-8 (see
    /// <see cref="Vouchfx.Mcp.Cli.EngineOutputEncoding"/>), where a code point can take up to four
    /// bytes. Decoding each chunk independently with <c>encoding.GetString(chunk)</c> would corrupt
    /// exactly a sequence split that way — the identical-looking per-chunk decode in
    /// <see cref="DrainAsciiIntoAsync"/> is safe ONLY because its own producer emits pure ASCII by
    /// construction (see that method's remarks), a guarantee this method's callers do not have. A
    /// single <see cref="Decoder"/> instance carries any incomplete trailing bytes from one call into
    /// the next <c>GetChars</c> call, so a split sequence still decodes intact regardless of where the
    /// chunk boundary fell — proven by <c>BoundedStreamReaderTests</c>' split-sequence case, which
    /// forces the split with a one-byte-at-a-time stream.
    /// </remarks>
    public static async Task ReadDecodedAsync(Stream stream, Encoding encoding, Action<char[], int> onChars)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(onChars);

        var decoder = encoding.GetDecoder();
        var byteChunk = new byte[8192];
        var charChunk = new char[encoding.GetMaxCharCount(byteChunk.Length)];

        int bytesRead;
        // CancellationToken.None, for ReadUpToAsync's own documented reason: this keeps draining even
        // after a caller has stopped waiting on it (VouchfxCliSuiteRunner's bounded-wait-then-abandon
        // pattern) — ObserveQuietly is how such a caller stops caring about the eventual result.
        while ((bytesRead = await stream.ReadAsync(byteChunk, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            onChars(charChunk, decoder.GetChars(byteChunk, 0, bytesRead, charChunk, 0, flush: false));
        }

        // Flushes any incomplete trailing byte sequence the decoder is still holding at end-of-stream,
        // through the encoding's own fallback (a replacement character for every encoding this server
        // ever resolves) rather than silently dropping it — the same completion Encoding.GetString
        // applies to a byte array that ends mid-character.
        onChars(charChunk, decoder.GetChars(Array.Empty<byte>(), 0, 0, charChunk, 0, flush: true));
    }

    /// <summary>
    /// Drains <paramref name="stream"/> into <paramref name="sink"/> as it arrives — so a caller that
    /// later kills the child still holds every byte that reached it BEFORE the kill.
    /// </summary>
    /// <param name="stream">The stream to drain.</param>
    /// <param name="sink">
    /// Where decoded text accumulates. <b>Every access, here and in the caller, must hold a lock on
    /// this same instance</b>: the caller reads it while this method may still be appending (that is
    /// the entire point), and <see cref="StringBuilder"/> is not thread-safe.
    /// </param>
    /// <param name="maxBytes">The inclusive byte cap; exceeding it invokes <paramref name="onExceeded"/> and stops the drain.</param>
    /// <param name="onExceeded">Invoked at most once if the cap is breached.</param>
    /// <remarks>
    /// <para>
    /// <b>Why this exists beside <see cref="ReadUpToAsync"/> rather than replacing it.</b> That method
    /// answers "give me the child's whole output, or nothing" — correct for
    /// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>, whose worker emits ONE JSON
    /// document that is meaningless when truncated. <see cref="Vouchfx.Mcp.Specs.SpecIndexWorkerClient"/>
    /// needs the opposite: its worker emits one self-contained JSON LINE per suite and flushes after
    /// each, so the lines that arrived before a timeout are exactly as valid as they would have been
    /// had the worker finished. Discarding them would throw away the whole batch because of one
    /// hostile file, which is the failure that boundary exists to prevent.
    /// </para>
    /// <para>
    /// <b>Decoded per chunk as ASCII-safe UTF-8, and that is only sound because the producer is
    /// ours.</b> Decoding a byte stream in fixed-size chunks would split a multi-byte UTF-8 sequence
    /// across two decodes and corrupt it. It cannot here: the spec-index worker serialises through
    /// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerProtocol.JsonOptions"/>, whose
    /// <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> escapes every non-ASCII character as
    /// <c>\uXXXX</c> (see that field's remarks — it is the defect-#70 mitigation), so this stream is
    /// pure ASCII by construction and one byte is always one character. <b>Do not point this method at
    /// a stream this repository does not produce</b>; use <see cref="ReadUpToAsync"/>, which decodes
    /// once at the end and has no such precondition.
    /// </para>
    /// </remarks>
    /// <param name="onData">
    /// Invoked after each chunk is appended — a LIVENESS signal, so a caller can distinguish "this
    /// child is working and producing results" from "this child has gone quiet". Optional; the
    /// spec-index worker's stall watchdog is what it exists for.
    /// </param>
    public static async Task DrainAsciiIntoAsync(
        Stream stream, StringBuilder sink, long maxBytes, Action onExceeded, Action? onData = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var chunk = new byte[8192];
        long total = 0;
        int bytesRead;

        // CancellationToken.None, for ReadUpToAsync's own documented reason: the drain keeps running
        // after the caller has moved on, and ObserveQuietly is how the caller stops caring. The
        // child's death is what ends this loop.
        while ((bytesRead = await stream.ReadAsync(chunk, CancellationToken.None)) > 0)
        {
            if (total + bytesRead > maxBytes)
            {
                onExceeded();
                return;
            }

            total += bytesRead;

            var text = Encoding.UTF8.GetString(chunk, 0, bytesRead);
            lock (sink)
            {
                sink.Append(text);
            }

            // AFTER the append, so a caller woken by this signal and reading the sink sees the data
            // that caused it rather than racing ahead of it.
            onData?.Invoke();
        }
    }

    /// <summary>
    /// Attaches a continuation that observes (and discards) any fault on <paramref name="task"/>
    /// without awaiting it, so an exception from a background read abandoned after a kill never
    /// surfaces as an unobserved task exception.
    /// </summary>
    public static void ObserveQuietly(Task task)
    {
        _ = task.ContinueWith(
            static t => t.Exception?.Handle(_ => true),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
