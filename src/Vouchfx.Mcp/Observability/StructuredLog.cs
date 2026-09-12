using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vouchfx.Mcp.Observability;

/// <summary>
/// THE structured-log record writer (US-S6-05): one JSON object per line, on stderr, in the shape
/// <c>{ timestamp, level, message, runId?, seq?, errorType? }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>stderr, exclusively, and that is the whole safety story.</b> stdout is the JSON-RPC channel and
/// a stray byte there corrupts every frame a connected agent reads (invariant 11). This type writes
/// to <see cref="Console.Error"/> and names <see cref="Console.Out"/> nowhere;
/// <c>StructuredLogHygieneSourceGuardTests</c> asserts both structurally, so the property cannot be
/// lost by an edit that looks harmless.
/// </para>
/// <para>
/// <b>Two entry points, one record shape — and the DIRECT one is now the primary.</b>
/// <see cref="StructuredConsoleFormatter"/> renders anything logged through an
/// <see cref="ILogger"/> and funnels it into <see cref="WriteTo"/> here; since US-S6-06 deleted
/// <c>Log.cs</c>, that means the HOSTING, SDK and Kestrel output this repository does not author, and
/// nothing this server says on its own behalf.
/// </para>
/// <para>
/// Everything this server does say goes through <see cref="Write"/>/<see cref="ForRun"/> directly,
/// for two structural reasons rather than convenience. <c>RunSuiteOrchestrator</c> has no logger at
/// all: it is constructed EAGERLY by <c>VouchfxMcpServerRegistration.AddVouchfxMcpServer</c>,
/// outside the DI graph and before the host that owns the logging providers exists, so nothing can
/// inject one without moving that construction. And the startup banners and failure lines run before
/// any host exists on EITHER transport — which is what lets the banners sit above the transport
/// branch, where both stdio and HTTP operators see them. Both paths produce byte-identical record
/// shapes because both go through the same private writer.
/// </para>
/// <para>
/// <b>Exactly what "one JSON object per line" covers, scoped so it is checkable.</b> It covers this
/// process's SERVER path: the three startup banners, the eight startup-FAILURE writes in
/// <c>Program.cs</c>, and the run-lifecycle records. It does NOT cover the <c>--validate-worker</c>
/// and <c>--spec-index-worker</c> branches, which run in a separate CHILD process whose stderr the
/// parent captures and relays as bounded data — their format is that child's contract with its
/// parent, not this server's contract with a host's log shipper.
/// </para>
/// <para>
/// <b>What may appear in a message, stated as a rule rather than left to judgement.</b> At the DIRECT
/// <see cref="Write"/>/<see cref="ForRun"/> call sites every message is a literal composed at the
/// call site, into which only these may be interpolated: a server-minted run id (<c>run-</c> plus 32
/// hex — never caller text), a COUNT, a <see cref="Run.RunVerdict"/> enum name, a duration, and an
/// exception's TYPE name. Never suite or event content, never an environment value, never an
/// exception's <c>Message</c> (BCL filesystem exceptions routinely embed a full path).
/// </para>
/// <para>
/// One documented EXCEPTION to the "no path" half, because it is real and is better named than
/// quietly relied upon: the workspace-configured startup banner carries the workspace ROOT. That is
/// operator-supplied <c>argv</c> rather than caller- or suite-supplied text, it is capped and
/// control-character-sanitised upstream by <c>PathSafetyGuard.CapAndSanitisePathForDisplay</c>, and
/// telling an operator which root is in force is the entire point of that banner — a peer review
/// added it precisely because stderr was previously silent on the question. The same applies to the
/// startup-failure lines, which are already-sanitised single lines composed by
/// <c>PinFailureReporting</c> and friends.
/// </para>
/// <para>
/// <b><c>seq</c> is THIS SERVER's per-run record ordinal and is NOT an engine event ordinal.</b> The
/// name matters: the story originally specified <c>eventSeq</c>, which was withdrawn at the premise
/// check because it implied a correlation that does not exist — the engine's JSON Lines events carry
/// no sequence field at all (measured envelope: <c>v</c>, <c>schemaVersion</c>, <c>type</c>,
/// <c>ts</c>, <c>runId</c>, <c>stepId</c>). This counter exists for one modest, real purpose:
/// ordering two records that share a millisecond timestamp. Likewise the <c>runId</c> here is the
/// SERVER-minted id — the one <c>get_run_status</c> reports — and NOT the engine's own per-event
/// runId, which lives in a different namespace and which <c>SuiteEventParser</c> has never parsed.
/// The join from a log record to the engine's event stream is the registry's <c>eventsFilePath</c>,
/// exactly as every reader tool already does it.
/// </para>
/// </remarks>
internal static class StructuredLog
{
    /// <summary>Writes one record with no run correlation — a startup or process-level line.</summary>
    public static void Write(LogLevel level, string message, string? errorType = null) =>
        WriteTo(level, message, runId: null, seq: null, errorType);

    /// <summary>
    /// Opens a run-correlated logging scope. Records written through it carry
    /// <paramref name="runId"/> and a per-scope ordinal starting at 1.
    /// </summary>
    /// <remarks>
    /// A scope rather than a <c>runId</c> parameter on <see cref="Write"/> because the ordinal has to
    /// live somewhere, and hanging it off a static keyed by run id would leak an entry per run for
    /// the process lifetime. The scope is owned by the one call that owns the run.
    /// </remarks>
    public static RunLogScope ForRun(string runId) => new(runId);

    /// <summary>
    /// Renders and emits one record. The single place a log line reaches stderr.
    /// </summary>
    /// <remarks>
    /// Written with <see cref="Utf8JsonWriter"/> over a pooled buffer rather than
    /// <c>JsonSerializer.Serialize</c>: no reflection, no per-call serializer lookup, and the escaping
    /// of control characters and newlines in <paramref name="message"/> is the writer's own — which is
    /// what keeps a record on ONE physical line whatever a message contains. A single
    /// <see cref="Console.Error"/> write per record keeps a record atomic at line granularity against
    /// any other writer on THIS process's stderr. That deliberately does not mention the engine: its
    /// stderr is redirected and captured by this server, so it never shares this stream.
    /// </remarks>
    private static void WriteTo(LogLevel level, string message, string? runId, int? seq, string? errorType = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Round-trip ("O") on a UTC DateTime: renders the Zulu form (…T12:34:56.7890123Z),
            // unambiguous, sortable as text, and what every log backend parses without
            // configuration. Deliberately NOT DateTimeOffset, whose "O" renders a numeric "+00:00"
            // offset instead of "Z" — the same instant, but two spellings of UTC across a log
            // estate is exactly the sort of thing that breaks someone's query at 3am.
            writer.WriteString("timestamp", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("level", RenderLevel(level));

            // Sanitised, not merely JSON-escaped. JSON escaping alone would keep the RECORD on one
            // line (\n inside a string literal), but the decoded message would still carry raw
            // control characters into whatever reads it — a log viewer, a terminal, an alert body.
            // TextSanitiser is the same helper every other outward-facing surface in this server uses
            // for that, so this field cannot be the one place a control character survives.
            writer.WriteString("message", TextSanitiser.SanitiseForDisplay(message));

            // Both optional fields are OMITTED rather than written null when absent: a null runId
            // reads in an aggregator as "a run whose id is unknown", which is a different and false
            // claim than "this line is not about a run".
            if (runId is not null)
            {
                writer.WriteString("runId", runId);
            }

            if (seq is not null)
            {
                writer.WriteNumber("seq", seq.Value);
            }

            // The exception TYPE name, when a record has a cause. Same omit-rather-than-null rule as
            // the two fields above, and the same content policy as everywhere else in this server:
            // the type, never the Message or the StackTrace.
            //
            // Sanitised for SYMMETRY with `message` rather than because framing is at risk — the
            // JSON writer already escapes whatever it is handed, so a hostile type name could not
            // break out of the record. What this buys is that a non-ASCII type name (a generic
            // argument from a localised assembly, say) renders as \uXXXX exactly like every other
            // field of this shape, instead of being the one field with different rules.
            if (errorType is not null)
            {
                writer.WriteString("errorType", TextSanitiser.SanitiseForDisplay(errorType));
            }

            writer.WriteEndObject();
        }

        Console.Error.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// The stable lower-case token for <paramref name="level"/>.
    /// </summary>
    /// <remarks>
    /// Explicit rather than <c>level.ToString().ToLowerInvariant()</c>: these values are grouped on in
    /// a log backend, so a future enum rename must be a deliberate decision here rather than an
    /// automatic relabelling of everybody's saved queries.
    /// </remarks>
    private static string RenderLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };

    /// <summary>
    /// A run-correlated logging scope: every record it writes carries the run's id and the next
    /// ordinal. See <see cref="StructuredLog"/>'s remarks for what <c>seq</c> does and does not mean.
    /// </summary>
    internal sealed class RunLogScope
    {
        private readonly string _runId;
        private int _seq;

        internal RunLogScope(string runId) => _runId = runId;

        /// <summary>Writes one run-correlated record.</summary>
        public void Write(LogLevel level, string message, string? errorType = null) =>
            WriteTo(level, message, _runId, Interlocked.Increment(ref _seq), errorType);
    }
}
