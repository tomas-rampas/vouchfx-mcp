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
    /// Reads with <see cref="CancellationToken.None"/> deliberately: this keeps running in the
    /// background even after the caller has moved on (a kill, a cap breach elsewhere, or the
    /// caller's own cancellation) — <see cref="ObserveQuietly"/> is how a caller that no longer
    /// needs the result stops caring about it without forcibly aborting the read itself.
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
