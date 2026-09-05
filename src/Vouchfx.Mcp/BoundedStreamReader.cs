using System.Text;

namespace Vouchfx.Mcp;

/// <summary>
/// Reads a <see cref="Stream"/> to the end as text in a caller-supplied <see cref="Encoding"/>
/// (UTF-8 by default), unless doing so would exceed a caller-supplied byte cap — shared by every
/// process-spawning boundary in this server that must never buffer an untrusted or
/// potentially-runaway child process's output without limit:
/// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/> (the <c>validate_suite</c> worker)
/// and <see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/> (the <c>vouchfx</c> CLI relay).
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
    /// <c>VouchfxCliProcessRunner</c> therefore passes that console code page here (see its
    /// <c>ResolveEngineOutputEncoding</c>), which is what actually fixes the corruption. Note that
    /// <c>ProcessStartInfo.StandardOutputEncoding</c> would NOT have fixed it: it only governs how
    /// <c>Process.StandardOutput</c>'s own <c>StreamReader</c> decodes, whereas this method reads the
    /// raw <c>BaseStream</c> bytes and decodes them itself — so the decode HERE is what had to change.
    /// </para>
    /// <para>
    /// <b>Honest scope of that fix.</b> Decoding with the console code page recovers only the
    /// characters that code page can REPRESENT. When the engine writes a character its console code
    /// page cannot encode, .NET best-fit-maps it AT THE SOURCE, before any byte reaches this method,
    /// and no parent-side decode can recover it. MEASURED under cp852 against the pinned schema:
    /// <c>§</c> (byte <c>0xF5</c>) IS recovered by the cp852 decode; <c>—</c> is best-fit-mapped to
    /// <c>-</c> and <c>…</c> to a raw <c>0x07</c> (which still breaks JSON parsing) before we see
    /// them, so both remain lost and <c>get_schema</c>'s cross-verification still reports VFX-D-1106
    /// — now for a genuine transcoding loss, not a decode bug. On a console whose code page CAN
    /// represent every schema character (e.g. Windows-1252) the fix is COMPLETE: MEASURED CLEAN.
    /// One residual is not purely benign loss: on a cp1252/Latin-1 console a PATH-hijacked engine's
    /// high bytes can decode to C1 control characters (U+0080–U+009F) rather than U+FFFD. Immaterial
    /// to safety — every diagnostic sink escapes non-0x20–0x7E via <c>TextSanitiser</c> and the
    /// JSON-RPC wire serialises them — but the decode is not exclusively lossy transcoding.
    /// The remaining OEM-console gap's only complete fix is the engine emitting UTF-8 when its output
    /// is redirected — an engine-side ask, for which #70 stays open; <c>chcp 65001</c> before
    /// starting this server is a full workaround, documented in <c>docs/errors/VFX-D-1106.md</c>.
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
