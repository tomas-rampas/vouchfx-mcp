using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Observability;

/// <summary>
/// Wraps an <see cref="McpServerTool"/> so every invocation emits exactly one span (US-S6-04),
/// delegating everything else — name, description, schema, metadata — to the tool it wraps.
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE interception point for all eighteen tools, and for every tool added later.</b> Every tool
/// is assembled in <c>ToolRegistry.CreateAll</c>, so wrapping the list there instruments the whole
/// surface without touching a single tool factory. The alternative — a span in each handler — would
/// be eighteen edits today, a nineteenth silently missing tomorrow, and eighteen places for the
/// attribute allowlist to drift. It also keeps this orthogonal to the tool-count lock: adding a tool
/// changes that test and nothing here.
/// </para>
/// <para>
/// <b>This type is a pass-through in every other respect, and that is load-bearing.</b> It derives
/// from the SDK's own <see cref="DelegatingMcpServerTool"/> rather than from
/// <see cref="McpServerTool"/> directly, so <c>ProtocolTool</c>, <c>Metadata</c> and every member
/// added to that base in a future SDK version forward to the wrapped tool automatically. That makes
/// "<c>tools/list</c> is byte-identical whether or not instrumentation is present" structural: there
/// is no hand-written pass-through here to fall out of date when the SDK grows a member, which a
/// direct subclass would have silently done. Only <see cref="InvokeAsync"/> is overridden, and the
/// result is returned exactly as the inner tool produced it — nothing is inspected for content, only
/// for the two facts the span records.
/// </para>
/// <para>
/// <b>Failure handling is deliberately narrow.</b> A throwing tool is re-thrown after the span is
/// completed as an error — this wrapper never converts an exception into a result, because that would
/// change behaviour the moment instrumentation was added, which AC 3 forbids. An unparseable
/// structured payload and a missing argument are both ordinary and simply yield no <c>runId</c>. A
/// null result is NOT tolerated and is not meant to be: <see cref="CallToolResult"/> is non-nullable
/// on this SDK, so a null would be a contract violation by the inner tool, and the NullReferenceException
/// it produces on the recording path is the correct, loud outcome rather than something to swallow.
/// </para>
/// <para>
/// <b>One honest consequence of wrapping, recorded as a conditional rather than a known problem.</b>
/// Wrapping hides the concrete SDK tool type behind this one, and the SDK's
/// <c>AIFunctionMcpServerTool.BuildLegacyWireProtocolTool</c> path keys off that concrete type to
/// rewrite a tool's OUTPUT SCHEMA for clients predating the 2026-07-28 wire revision. A client old
/// enough to need that rewrite would therefore not receive it for these tools. Measured today this is
/// a NO-OP: no tool in this server declares an output schema, so there is nothing for that path to
/// rewrite. It is written down because the day a tool declares one, this becomes real — and the fix
/// then is to forward the legacy build rather than to unwrap.
/// </para>
/// </remarks>
internal sealed class InstrumentedMcpServerTool : DelegatingMcpServerTool
{
    private readonly string? _workspaceHash;

    private InstrumentedMcpServerTool(McpServerTool inner, string? workspaceHash)
        : base(inner) => _workspaceHash = workspaceHash;

    /// <summary>Wraps every tool in <paramref name="tools"/>. The one call site is ToolRegistry.</summary>
    public static IReadOnlyList<McpServerTool> WrapAll(IEnumerable<McpServerTool> tools, Workspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var workspaceHash = WorkspaceHash.Of(workspace);

        return [.. tools.Select(tool => (McpServerTool)new InstrumentedMcpServerTool(tool, workspaceHash))];
    }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        using var span = ToolTelemetry.Start(ProtocolTool.Name, _workspaceHash);

        // Nothing is listening: skip the result inspection entirely rather than paying for a runId
        // nobody will read. This is the ordinary production path on a host with no collector.
        if (!span.IsRecording)
        {
            return await base.InvokeAsync(request, cancellationToken);
        }

        var requestRunId = TryReadRunIdArgument(request);

        try
        {
            var result = await base.InvokeAsync(request, cancellationToken);

            span.Complete(
                (result.IsError ?? false) ? ToolCallOutcome.Error : ToolCallOutcome.Success,
                requestRunId ?? TryReadRunIdFromResult(result));

            return result;
        }
        catch (Exception)
        {
            // The span records that the call failed; the exception itself is not touched, not
            // inspected, and not attached — its message is exactly the kind of text the hygiene AC
            // keeps off spans. Re-thrown unchanged so behaviour is identical with or without a
            // listener attached.
            span.Complete(ToolCallOutcome.Error, requestRunId);
            throw;
        }
    }

    /// <summary>
    /// The call's own <c>runId</c> argument, when it has one AND it is a well-formed run id.
    /// </summary>
    /// <remarks>
    /// <b>Which tools are "run-lifecycle" is DERIVED from the call, never hard-coded.</b> A tool is
    /// one exactly when its arguments carry a <c>runId</c> — measured at the time of writing:
    /// <c>cancel_run</c>, <c>get_run_artifacts</c>, <c>get_run_events</c>, <c>get_run_status</c> and
    /// <c>get_step_timeline</c>, each of which declares a <c>runId</c> handler parameter. Reading it
    /// from the request rather than from a list means a nineteenth run tool is covered the day it is
    /// added, and a list cannot rot against the schemas.
    /// <para>
    /// <b>The value is SHAPE-GATED, and that gate is load-bearing rather than defensive tidying.</b>
    /// Elsewhere this server can say a runId "is an opaque identifier this server minted and already
    /// returned to the host in plain sight, so it discloses nothing" — that is true of the RESULT
    /// path below, where the id is this server's own, and it was FALSE of this one: an argument is
    /// whatever the caller typed. Unbounded, unsanitised caller text on a span would put an arbitrary
    /// string — a suite path, a secret pasted into the wrong field, a megabyte of junk — into a shared
    /// tracing backend through the one attribute that looked safe. Running it through
    /// <see cref="RunRegistryCore.IsWellFormedRunId"/> (<c>run-</c> plus 32 lowercase hex) restores
    /// the original claim by making it true: what reaches the span is now necessarily an id of the
    /// shape this server mints, whoever supplied it.
    /// </para>
    /// <para>
    /// A malformed id yields NO tag rather than a sanitised one. That is the tighter of the two
    /// available choices and the right one here: a run id that cannot exist carries no debugging
    /// value as an attribute, whereas echoing a scrubbed version of caller input would preserve
    /// exactly the channel the gate exists to close. The tool still answers the caller normally —
    /// this affects the span alone.
    /// </para>
    /// <para>
    /// A well-formed id is still tagged even when the lookup then FAILS, which is deliberate: a span
    /// for "get_run_status on a run that does not exist" is exactly what an operator debugging a
    /// missing run wants, and withholding the id until success would lose it.
    /// </para>
    /// </remarks>
    private static string? TryReadRunIdArgument(RequestContext<CallToolRequestParams> request)
    {
        var arguments = request.Params?.Arguments;

        if (arguments is null || !arguments.TryGetValue(ToolTelemetry.RunIdAttribute, out var value))
        {
            return null;
        }

        var runId = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return RunRegistryCore.IsWellFormedRunId(runId) ? runId : null;
    }

    /// <summary>
    /// The <c>runId</c> a result announced, for the one tool that MINTS one rather than taking it.
    /// </summary>
    /// <remarks>
    /// <c>run_suite</c> receives no runId and returns one, so the argument path above cannot see it.
    /// Reading the result's own top-level <c>runId</c> covers it by the same derived rule rather than
    /// by naming the tool. Only a top-level string property is read — never a nested search, which
    /// would eventually find a field that merely happens to be called <c>runId</c> inside somebody's
    /// relayed engine event.
    /// <para>
    /// <c>list_runs</c> is excluded by this rule for free and correctly: it returns MANY runs and no
    /// single top-level <c>runId</c>, so there is no one value a span could honestly carry.
    /// </para>
    /// </remarks>
    private static string? TryReadRunIdFromResult(CallToolResult result)
    {
        if (result.StructuredContent is not JsonElement { ValueKind: JsonValueKind.Object } structured)
        {
            return null;
        }

        return structured.TryGetProperty(ToolTelemetry.RunIdAttribute, out var runId)
            && runId.ValueKind == JsonValueKind.String
            ? runId.GetString()
            : null;
    }
}
