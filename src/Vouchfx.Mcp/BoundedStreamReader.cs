using System.Text;

namespace Vouchfx.Mcp;

/// <summary>
/// Reads a <see cref="Stream"/> to the end as UTF-8 text, unless doing so would exceed a
/// caller-supplied byte cap — shared by every process-spawning boundary in this server that must
/// never buffer an untrusted or potentially-runaway child process's output without limit:
/// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/> (the <c>validate_suite</c> worker)
/// and <see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/> (the <c>vouchfx --version</c> probe).
/// </summary>
public static class BoundedStreamReader
{
    /// <summary>
    /// Reads <paramref name="stream"/> to the end as UTF-8 text, unless doing so would exceed
    /// <paramref name="maxBytes"/> — in which case <paramref name="onExceeded"/> is invoked and
    /// this returns <see langword="null"/> without reading further.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads with <see cref="CancellationToken.None"/> deliberately: this keeps running in the
    /// background even after the caller has moved on (a kill, a cap breach elsewhere, or the
    /// caller's own cancellation) — <see cref="ObserveQuietly"/> is how a caller that no longer
    /// needs the result stops caring about it without forcibly aborting the read itself.
    /// </para>
    /// <para>
    /// <b>KNOWN LIMITATION — the decode below is hardcoded UTF-8, and a Windows child process does
    /// not necessarily write UTF-8.</b> A .NET child writes its stdout in the CONSOLE'S ACTIVE code
    /// page, and <c>VouchfxCliProcessRunner</c> does not set <c>StandardOutputEncoding</c>, so on any
    /// non-65001 console every non-ASCII byte relayed from the engine is corrupted here. MEASURED
    /// under cp852: <c>vouchfx schema</c>'s <c>§</c> arrives as the single byte <c>0xF5</c> and its
    /// <c>—</c> is best-fit-mapped to <c>-</c>, which makes the live export differ from the identical
    /// vendored document and (in that particular run) injects a raw <c>0x07</c> inside a JSON string
    /// so the document does not even parse. This affects EVERY CLI-backed relay path in principle,
    /// not just <c>get_schema</c> — that tool is merely the first caller whose comparison notices.
    /// MEASURED BLAST RADIUS at the current pin (v1.0.0-rc.4): <c>vouchfx list --json</c> is pure
    /// ASCII and byte-identical under cp852 and cp65001, so the only relay this defect corrupts
    /// today is <c>vouchfx schema</c>'s cross-verification. That is why it is a tracked defect rather
    /// than a stop-ship: the exposure is one comparison, not the tool surface.
    /// <b>TRACKED AS https://github.com/tomas-rampas/vouchfx-mcp/issues/70.</b> Candidate fixes, both
    /// out of scope of US-S2-01 and deliberately deferred because the real one touches every
    /// CLI-backed tool's plumbing: decode via the console output code page on Windows (set
    /// <c>ProcessStartInfo.StandardOutputEncoding</c>, or take an <c>Encoding</c> parameter here),
    /// and/or have the engine emit UTF-8 whenever its output is redirected — an engine-side ask.
    /// Until then the practical workaround is <c>chcp 65001</c> before starting this server, and
    /// <c>docs/errors/VFX-D-1106.md</c> documents it as our limitation rather than the user's
    /// misconfiguration.
    /// </para>
    /// </remarks>
    public static async Task<string?> ReadUpToAsync(Stream stream, long maxBytes, Action onExceeded)
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

        return Encoding.UTF8.GetString(buffer.ToArray());
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
