namespace Vouchfx.Mcp;

/// <summary>
/// Builds the friendly, terminal-safe message printed to stderr when the ENGINE_PIN file cannot
/// be read or parsed at startup.
/// </summary>
/// <remarks>
/// Extracted out of <c>Program.cs</c> — which cannot itself be unit tested, being a top-level
/// statements entry point — so the message-building and sanitising behaviour has direct test
/// coverage (see <c>PinFailureReportingTests</c>).
/// <para>
/// Two things are deliberate here, both security-relevant:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The message never includes the ENGINE_PIN file's full path — only the bare file name — so a
/// startup failure does not leak the host's directory layout into a log line.
/// <see cref="FileNotFoundException"/> is special-cased for this: rather than trust that
/// exception's own <see cref="Exception.Message"/> to already be path-free (<see cref="EnginePin.Load"/>
/// keeps that promise today, but nothing enforces it stays that way), this builds the reported
/// text itself from <see cref="FileNotFoundException.FileName"/> — a structured property, not
/// free text — via <see cref="Path.GetFileName(string?)"/>, so a future path-bearing message on
/// that exception type still cannot leak.
/// </description></item>
/// <item><description>
/// For every other exception type, <paramref name="exception"/>'s own <see cref="Exception.Message"/>
/// is passed through <see cref="EnginePin.SanitiseForDisplay"/> before being printed.
/// <see cref="EnginePin"/> already sanitises the raw pin values it splices into its own
/// exception messages, so this is defence in depth — a second, independent scrub at the point
/// where the message actually reaches a terminal — rather than a duplicate of that fix.
/// </description></item>
/// </list>
/// </remarks>
public static class PinFailureReporting
{
    /// <summary>
    /// Builds the one-line message to print to stderr for a failure to load or parse
    /// ENGINE_PIN. Never includes a file path or any unsanitised byte from
    /// <paramref name="exception"/>'s message.
    /// </summary>
    public static string DescribeLoadFailure(Exception exception)
    {
        var detail = exception is FileNotFoundException fileNotFound
            ? $"{ResolveFileName(fileNotFound.FileName)} not found beside the executable."
            : EnginePin.SanitiseForDisplay(exception.Message);

        return $"vouchfx-mcp: could not read ENGINE_PIN: {detail}";
    }

    private static string ResolveFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return "ENGINE_PIN";
        }

        var bareName = Path.GetFileName(fileName);
        return string.IsNullOrEmpty(bareName) ? "ENGINE_PIN" : bareName;
    }
}
