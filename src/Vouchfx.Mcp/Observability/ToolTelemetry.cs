using System.Diagnostics;

namespace Vouchfx.Mcp.Observability;

/// <summary>How a tool call ended, as reported on a span's <c>outcome</c> attribute.</summary>
/// <remarks>
/// An enum rather than a string parameter, deliberately: it is the difference between an attribute
/// whose value set is closed by the compiler and one a future call site can widen to anything —
/// including, by the obvious accident, a <c>VfxError</c>'s message.
/// </remarks>
internal enum ToolCallOutcome
{
    Success,
    Error,
}

/// <summary>
/// THE span-emission point for tool calls (US-S6-04). Every span this server produces is started
/// here and nowhere else, and the four allowlisted attributes are the only ones it is possible to
/// set through this API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Instrumentation is <see cref="ActivitySource"/>, not an OpenTelemetry SDK</b> (US-S6-04 as
/// amended 2026-09-12, after measurement). Three reasons, in the order they decided it. First, a
/// host spawns this server as a stdio CHILD PROCESS with no OTLP endpoint and MCP carries no trace
/// context, so a product-side exporter would export nothing on every real host while adding startup
/// cost and failure modes to all of them; the process attacher or embedding host chooses an
/// exporter, exactly as a library leaves that choice to its application. Second,
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns <see langword="null"/>
/// when nothing is listening, so "a host with no collector sees no behaviour change, no new
/// configuration and no new failure mode" is a property of the BCL rather than a claim this code has
/// to defend. Third, this type performs NO I/O of any kind — which matters more here than in an
/// ordinary service, because stdout is the JSON-RPC channel (invariant 11) and an exporter would add
/// a background write path to audit.
/// </para>
/// <para>
/// <b>The attribute allowlist is held BY CONSTRUCTION, which is the AC.</b> There is no
/// <c>SetTag(string, object)</c> exposed anywhere on this API: <see cref="Start"/> takes the workspace
/// hash, <see cref="ToolSpan.Complete"/> takes the outcome and the run id, and the duration is
/// computed here rather than accepted. A future call site that wants a fifth attribute cannot add one
/// without editing this file and changing a signature — a compile error, not a silent leak.
/// <c>ToolTelemetrySourceGuardTests</c> additionally pins that no <c>StartActivity</c> call exists in
/// <c>src/</c> outside this type, so the choke point cannot be bypassed by starting a span elsewhere.
/// </para>
/// <para>
/// <b>duration_ms is computed, not passed.</b> The AC asks for four typed parameters; making the
/// duration one of them would let a call site report a number unrelated to the call it wrapped. It is
/// read from the <see cref="Activity"/>'s own stopwatch instead, so it cannot disagree with the span
/// it is on.
/// </para>
/// <para>
/// <b>Parenting.</b> The MCP C# SDK emits its own span per <c>tools/call</c> from its own source
/// (measured: <c>Experimental.ModelContextProtocol</c> on 2.2.0 — see
/// <c>RealToolSpanMcpTests</c>, which fails if that moves). Starting ours through an
/// <see cref="ActivitySource"/> makes it a child of whatever is current, so the two form one trace
/// rather than two disconnected roots, with no explicit context plumbing.
/// </para>
/// </remarks>
internal static class ToolTelemetry
{
    /// <summary>
    /// This server's activity source name — what a listener or exporter filters on to select this
    /// server's spans specifically, and what "exactly one span per call" is scoped to.
    /// </summary>
    public const string SourceName = "Vouchfx.Mcp";

    /// <summary>The attribute keys this server may set. Exhaustive, and the guard test asserts it.</summary>
    public const string WorkspaceHashAttribute = "workspace.hash";

    /// <inheritdoc cref="WorkspaceHashAttribute"/>
    public const string RunIdAttribute = "runId";

    /// <inheritdoc cref="WorkspaceHashAttribute"/>
    public const string DurationMsAttribute = "duration_ms";

    /// <inheritdoc cref="WorkspaceHashAttribute"/>
    public const string OutcomeAttribute = "outcome";

    /// <summary>
    /// The single <see cref="ActivitySource"/> for this server's tool spans. Versioned with the
    /// server's own advertised version so a backend can tell which build produced a span.
    /// </summary>
    /// <remarks>
    /// <b>PRIVATE, deliberately.</b> Exposing the source would hand any type in this assembly a
    /// <c>StartActivity</c> it could call directly, which is a compile-time route straight past this
    /// type's four-parameter allowlist — the one thing the AC asks to be closed by construction.
    /// <c>ToolTelemetrySourceGuardTests</c> catches such a call site in source, but a private field
    /// means it cannot be written in the first place. <see cref="HasListeners"/> exposes the only
    /// fact callers outside this type legitimately need.
    /// </remarks>
    private static readonly ActivitySource Source = new(SourceName, ServerIdentity.Version);

    /// <summary>
    /// Whether anything is currently listening to this server's spans.
    /// </summary>
    /// <remarks>
    /// Exists so tests can assert the no-listener precondition (and the additive AC's mechanism)
    /// without <see cref="Source"/> having to be public — see that field's remarks. Production reads
    /// it on the fast path in <see cref="Start"/>.
    /// </remarks>
    public static bool HasListeners() => Source.HasListeners();

    /// <summary>The span name for <paramref name="toolName"/> — <c>vouchfx.mcp.tool/&lt;toolName&gt;</c>.</summary>
    public static string SpanName(string toolName) => $"vouchfx.mcp.tool/{toolName}";

    /// <summary>
    /// Starts the span for one tool call. Returns a scope that is inert when nothing is listening.
    /// </summary>
    /// <param name="toolName">The advertised tool name, used to build the span name.</param>
    /// <param name="workspaceHash">
    /// <see cref="WorkspaceHash.Of"/>'s result — never a raw path, and <see langword="null"/> when the
    /// host configured no workspace, in which case the attribute is omitted rather than blanked.
    /// </param>
    public static ToolSpan Start(string toolName, string? workspaceHash)
    {
        // THE no-collector fast path, checked before anything else so the "no work at all" claim is
        // literal rather than nearly true. Without it this method would still interpolate the span
        // name and read a timestamp on every tool call of every host that has no collector — small,
        // but not nothing, and the AC's promise is that instrumentation costs such a host zero.
        // HasListeners is cheap and allocation-free; its internal mechanism is the BCL's business and
        // is deliberately not characterised here.
        if (!Source.HasListeners())
        {
            return default;
        }

        // Internal: this span represents work this process did on its own behalf, not an inbound
        // remote call (the SDK's own span already models the request).
        var activity = Source.StartActivity(SpanName(toolName), ActivityKind.Internal);

        if (activity is not null && workspaceHash is not null)
        {
            activity.SetTag(WorkspaceHashAttribute, workspaceHash);
        }

        return new ToolSpan(activity, Stopwatch.GetTimestamp());
    }
}

/// <summary>
/// One tool call's span, or nothing at all when no listener is attached. Completing it stamps the
/// outcome, the computed duration and — for a run-lifecycle call — the run id.
/// </summary>
/// <remarks>
/// A struct wrapping a nullable <see cref="Activity"/> so the no-listener path allocates nothing and
/// every member is a null check. <see cref="Complete"/> is the ONLY way to set the remaining
/// attributes, which is what keeps the allowlist closed.
/// </remarks>
internal readonly struct ToolSpan : IDisposable
{
    private readonly Activity? _activity;
    private readonly long _startTimestamp;

    internal ToolSpan(Activity? activity, long startTimestamp)
    {
        _activity = activity;
        _startTimestamp = startTimestamp;
    }

    /// <summary>Whether a listener was attached and a span actually exists.</summary>
    public bool IsRecording => _activity is not null;

    /// <summary>
    /// Stamps the terminal attributes. Call exactly once, before disposal.
    /// </summary>
    /// <param name="outcome">
    /// Success or error — a closed enum, never the <c>VfxError</c>'s message. The message is the
    /// caller's answer; the span records only that there was one.
    /// </param>
    /// <param name="runId">
    /// The run this call concerned, for run-lifecycle tools only; <see langword="null"/> everywhere
    /// else, in which case the attribute is omitted. A runId is an opaque identifier this server
    /// minted and already returned to the host in plain sight, so it discloses nothing the host does
    /// not have.
    /// </param>
    public void Complete(ToolCallOutcome outcome, string? runId = null)
    {
        if (_activity is null)
        {
            return;
        }

        _activity.SetTag(ToolTelemetry.OutcomeAttribute, outcome == ToolCallOutcome.Success ? "success" : "error");

        if (runId is not null)
        {
            _activity.SetTag(ToolTelemetry.RunIdAttribute, runId);
        }

        // Measured from a Stopwatch timestamp captured at Start, not taken as a parameter — see
        // ToolTelemetry's remarks on why duration is computed rather than accepted. Deliberately NOT
        // Activity.Duration: that is only populated when the Activity is STOPPED, which happens in
        // Dispose, i.e. after this method has already had to write the attribute — reading it here
        // would silently record 00:00:00 on every span. Rounded to whole milliseconds, since
        // sub-millisecond precision on a span that already carries start and end timestamps is noise.
        _activity.SetTag(
            ToolTelemetry.DurationMsAttribute,
            (long)Math.Round(
                Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds,
                MidpointRounding.AwayFromZero));

        _activity.SetStatus(
            outcome == ToolCallOutcome.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    /// <summary>Stops the span. Safe when no listener was attached and safe to call twice.</summary>
    public void Dispose() => _activity?.Dispose();
}
