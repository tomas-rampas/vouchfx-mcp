using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Vouchfx.Mcp.Observability;

/// <summary>
/// Renders <see cref="ILogger"/> output as US-S6-05's structured record shape — the second of
/// <see cref="StructuredLog"/>'s two entry points, covering the three startup
/// <c>[LoggerMessage]</c> banners.
/// </summary>
/// <remarks>
/// <para>
/// <b>A formatter rather than a replacement logging provider.</b> The console provider is already
/// configured to send everything to stderr (<c>LogToStandardErrorThreshold = Trace</c> in
/// <c>Program.cs</c>), which is the property that matters for invariant 11; swapping the provider
/// would mean re-establishing it. Replacing only the FORMATTER keeps the channel exactly as it was
/// and changes nothing but the rendering — which is precisely what AC2 asks for.
/// </para>
/// <para>
/// <b>Why it delegates to <see cref="StructuredLog"/> instead of writing to the supplied
/// <see cref="TextWriter"/>.</b> The AC is that ALL stderr output shares one record shape, and the
/// run lifecycle cannot reach a logger at all (see <see cref="StructuredLog"/>'s remarks). Two
/// independent renderers would be two shapes to keep in step, and the one that is harder to test —
/// the DI-less path — would be the one that drifted. Funnelling both into a single private writer
/// makes byte-identical output structural rather than a convention. The consequence is that this
/// formatter deliberately does NOT write to <paramref name="textWriter"/>; that is documented here
/// because it is the surprising half of the design.
/// </para>
/// <para>
/// <b>Scopes are deliberately not rendered.</b> A scope's state is arbitrary caller-supplied data,
/// and this server's log records carry an enumerable field set by design (AC3). Nothing in this
/// server opens a logging scope today, so rendering them would be adding an unbounded channel with
/// no consumer.
/// </para>
/// <para>
/// <b>An exception's TYPE NAME is forwarded; nothing else about it is.</b> This formatter sees more
/// than this server's own three banners: the Hosting infrastructure and the MCP SDK log through the
/// same pipeline, so a faulted background service or a throwing request handler arrives here. An
/// earlier revision dropped <c>logEntry.Exception</c> entirely, which silently deleted the CAUSE of
/// every such failure from an operator's only diagnostic channel — the default formatter this
/// replaced printed <c>exception.ToString()</c>. The type name restores the signal at the same
/// content policy every other surface in this server follows
/// (<c>RunSuiteOrchestrator.ReportCompletionNotRecorded</c>'s is identical): never <c>Message</c>,
/// which routinely embeds a full filesystem path, never <c>StackTrace</c>, which embeds the build
/// machine's, and never the inner exception chain.
/// </para>
/// <para>
/// <b>A BCL dependency worth naming, because the design silently relies on it.</b> This method writes
/// nothing to <paramref name="textWriter"/> and instead calls <see cref="StructuredLog"/>. That does
/// not produce a DOUBLE write only because <c>ConsoleLogger</c> checks whether the formatter wrote
/// anything to its buffer and skips the write when that buffer is empty. If a future
/// <c>Microsoft.Extensions.Logging.Console</c> stopped short-circuiting on empty formatter output,
/// every record here would be followed by a blank line on stderr — breaking the one-object-per-line
/// contract without breaking any assertion about the records themselves.
/// <c>StructuredConsoleFormatterTests</c> covers the early-return path deliberately for that reason.
/// </para>
/// </remarks>
internal sealed class StructuredConsoleFormatter : ConsoleFormatter
{
    /// <summary>The formatter name <c>Program.cs</c> selects by.</summary>
    public const string FormatterName = "vouchfx-structured";

    public StructuredConsoleFormatter()
        : base(FormatterName)
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        _ = scopeProvider;
        _ = textWriter;

        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // No runId/seq: this server's own messages reaching here are startup banners, emitted before
        // host.RunAsync() and therefore outside any run. That is not an assumption this code relies
        // on for correctness — a future logger-bearing component inside a run would simply get a
        // record with no correlation fields, which is honest — but it IS why the two optional fields
        // are absent here rather than plumbed.
        //
        // The exception's TYPE NAME only — see this type's remarks for why the type is forwarded and
        // why Message/StackTrace/InnerException are not.
        StructuredLog.Write(logEntry.LogLevel, message, logEntry.Exception?.GetType().Name);
    }
}
