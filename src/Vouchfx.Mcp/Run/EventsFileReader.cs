using System.Text;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// Reads a vouchfx events file (the <c>--events</c> JSON Lines stream) up to a bounded byte cap —
/// shared by <see cref="RunSuiteOrchestrator"/> (reading the file it just produced) and
/// <c>Vouchfx.Mcp.Diagnosis.ExplainRunOrchestrator</c> (reading an EXISTING file, agent-supplied or
/// from <see cref="ILastRunTracker"/>), so both consumers of the events-file contract share exactly
/// one bounded-read implementation rather than two.
/// </summary>
/// <remarks>
/// The file is agent-influenced content (step ids, <c>script.csharp</c> output, observation payloads
/// from a suite the caller supplied), and every other boundary in this server caps what it reads into
/// memory — this is no exception. A file larger than <see cref="MaxEventsFileBytes"/> is read only up
/// to that many bytes (never rejected outright, never buffered in full) rather than thrown for; the
/// caller is told via the returned <c>Truncated</c> flag. Whatever complete lines fit within the cap
/// still parse normally via <see cref="SuiteEventParser"/> — a truncated final partial line is simply
/// skipped by that parser's own per-line tolerance.
/// </remarks>
public static class EventsFileReader
{
    /// <summary>
    /// Maximum bytes read from an events file before parsing. Mirrors
    /// <see cref="Validation.ValidationWorkerClient.MaxWorkerOutputBytes"/>'s exact value and
    /// rationale — a legitimate structured result from this same overall system can reasonably reach
    /// that scale, and 50 MB is a generous margin over any realistic single suite's event stream
    /// while still bounding how much of a runaway or hostile stream this server ever buffers in
    /// memory at once.
    /// </summary>
    public const long MaxEventsFileBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Reads <paramref name="path"/> up to <see cref="MaxEventsFileBytes"/>.
    /// </summary>
    /// <returns>
    /// The file's content (up to the cap) and whether it was truncated, or <see langword="null"/>
    /// content when the file could not be read at all (missing, a permissions problem, a
    /// non-seekable special file, or another I/O failure) — callers distinguish "could not read"
    /// from "read but empty" via this <see langword="null"/>-ness, exactly as before this was
    /// extracted from <see cref="RunSuiteOrchestrator"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Non-seekable paths (a review fix):</b> a Windows reserved device name (<c>NUL</c>,
    /// <c>CON</c>, <c>COM1</c>, <c>LPT1</c>, <c>PRN</c>, <c>AUX</c>) or another special file opens
    /// successfully but produces a stream with <see cref="Stream.CanSeek"/> <see langword="false"/> —
    /// and <see cref="Stream.Length"/>'s own documented contract permits it to throw
    /// <see cref="NotSupportedException"/> for exactly such a stream. <c>CanSeek</c> is therefore
    /// checked BEFORE <c>Length</c> is ever read, treating a non-seekable path the same as any other
    /// "could not be read" outcome rather than letting that propagate uncaught — which would have
    /// bypassed the graceful <c>EventsFileUnreadable</c> result an agent-supplied
    /// <c>eventsPath</c> is otherwise always resolved to. The catch clause below is likewise widened
    /// to every exception type, not just the two most common ones, matching what this method's own
    /// contract already promised ("could not be read... or another I/O failure") but did not, until
    /// now, actually implement.
    /// </para>
    /// <para>
    /// <b>Cancellation is deliberately EXCLUDED from that broad catch</b> (a further review fix): a
    /// genuine caller cancellation of <paramref name="cancellationToken"/> surfaces from
    /// <c>stream.ReadAsync</c> as an <see cref="OperationCanceledException"/> (or its
    /// <see cref="TaskCanceledException"/> subtype) — catching THAT alongside every other I/O failure
    /// would silently turn a caller's own cancellation into a misleading "could not be read" result
    /// instead of letting it propagate as the ordinary cancellation every other boundary in this
    /// codebase already treats it as.
    /// </para>
    /// </remarks>
    public static async Task<(string? Content, bool Truncated)> TryReadBoundedAsync(
        string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);

            if (!stream.CanSeek)
            {
                return (null, false);
            }

            var length = stream.Length;
            var truncated = length > MaxEventsFileBytes;
            var bytesToRead = truncated ? MaxEventsFileBytes : length;

            var buffer = new byte[bytesToRead];
            var totalRead = 0L;
            while (totalRead < bytesToRead)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory((int)totalRead, (int)(bytesToRead - totalRead)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return (Encoding.UTF8.GetString(buffer, 0, (int)totalRead), truncated);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: this is the
        // documented backstop for EVERY way reading an agent-supplied path can fail (a missing file,
        // a permissions problem, a Windows reserved device name, a race with deletion, an encoding
        // failure, ...) — narrower catches here previously let an exception type outside that short
        // list propagate uncaught past this boundary instead of resolving to the graceful
        // "could not be read" outcome every caller already expects. The `when` filter excludes
        // OperationCanceledException (and its TaskCanceledException subtype) so a genuine caller
        // cancellation still propagates normally instead of being folded into that same outcome.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return (null, false);
        }
    }
}
