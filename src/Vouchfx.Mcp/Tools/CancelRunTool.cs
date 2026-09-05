using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>cancel_run</c> tool (Sprint 3 / US-S3-03; spec §5.8): asks an in-flight run to stop,
/// through the same graceful-stop mechanism <c>run_suite</c> already uses.
/// </summary>
/// <remarks>
/// <para>
/// A thin MCP-facing wrapper: the resolution, the signal, and the honest refusals live in
/// <see cref="CancelRunOrchestrator"/>.
/// </para>
/// <para>
/// <b>This is the one tool in this file's neighbourhood that is NOT <c>ReadOnly</c>.</b> It changes a
/// run's lifecycle — that is its whole purpose — so the MCP <c>readOnlyHint</c> would be a false
/// declaration, and it is the reason this tool may touch the workspace run lock where
/// <c>get_run_status</c>/<c>list_runs</c> may not (see <see cref="CancelRunOrchestrator"/>). It is
/// still not <c>destructive</c> in the MCP sense: <b>nothing is ever deleted or overwritten</b>, and
/// a run stopped this way reports the verdict it reached rather than losing it.
/// </para>
/// <para>
/// <b>It is not quite "writes nothing", and that claim has been corrected</b> (a gatekeeper and a
/// security review, independently). The liveness probe goes through
/// <see cref="Vouchfx.Mcp.Run.WorkspaceRunLock"/>, which creates the workspace's output directory
/// (<see cref="Directory.CreateDirectory(string)"/>) and then opens <c>&lt;outputDir&gt;/.lock</c>
/// with <see cref="FileMode.OpenOrCreate"/> — so a probe against a workspace whose output directory
/// does not exist yet leaves an empty directory and an empty, zero-byte lock file behind. Both are
/// inert: the directory is this server's own artefact root, which the next <c>run_suite</c> call
/// would have created anyway, and the lock file carries no payload by construction (it IS the lock).
/// Nothing of the caller's is touched, and the read-only invariant — this server never writes,
/// modifies, or deletes a SUITE file — is untouched. Stated rather than glossed because the tool
/// description used to say "no file is written", which was simply false.
/// </para>
/// </remarks>
internal static class CancelRunTool
{
    public const string Name = "cancel_run";

    private const string Description =
        "Asks a run that is still in flight to stop, and reports what could be done about it. A run " +
        "this server process is running is signalled through exactly the mechanism run_suite already " +
        "uses to stop the engine — its stdin is closed so the CLI shuts down gracefully, and only " +
        "then, after the grace period, is the process tree killed — so containers are torn down " +
        "rather than orphaned. The result is { runId, status }: 'cancelled' means the stop has been " +
        "REQUESTED and the run is winding down (this call does not wait for it; poll get_run_status " +
        "until the status is TERMINAL — 'completed' or 'cancelled' — because a cancellation " +
        "delivered in the instant the run was already composing its completing record ends it as " +
        "'completed'), and 'already_finished' means the run was over before " +
        "you asked, which is a normal answer and NOT an error. Cancelling a test is never a test " +
        "failure: what changes is the run's STATUS ('cancelled' rather than 'completed'), while its " +
        "outcome stays whatever the run genuinely reached — Inconclusive for the ordinary case, and " +
        "still Fail for a multi-suite run in which an earlier suite had already failed, because " +
        "erasing a verdict the engine demonstrably produced would be its own kind of lie. Two " +
        "refusals are " +
        "possible and both are honest rather than pretended: VFX-E-1507 when the entry says " +
        "'running' but this server process is not the one holding it — which means another server " +
        "process against this workspace is running it (there is no cross-process cancel; cancel it " +
        "from the server that started it), or its completing record was lost, or this server is " +
        "busy with a DIFFERENT run whose lock masks the answer; the message says which of those " +
        "this server could establish — and VFX-E-1508 when the entry says 'running' " +
        "but nothing holds the workspace, meaning it is residue: a server killed mid-run, or a run " +
        "whose completing record failed to be written. That second answer is also how you tell a " +
        "phantom 'running' entry from a real one. " +
        "The optional 'reason' is context for the operator only: it is never stored, never returned, " +
        "and never appears in any other tool's output.";

    public static McpServerTool Create(CancelRunOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // `reason` carries an explicit '= null' default for the reason every other tool's optional
        // parameters do: it is what makes the SDK's generated schema mark it optional, so a caller can
        // omit it without parameter binding failing before Handle runs.
        CallToolResult Handle(
            [Description("The run to stop — the 'runId' run_suite returned on its result, or one list_runs reported.")]
            string runId,
            [Description("Optional free-form context for why this run is being cancelled. Recorded in this server's memory for the operator's benefit only: it is never persisted, never echoed back, and never appears in any other tool's result.")]
            string? reason = null) =>
            Render(orchestrator.Cancel(new CancelRunRequest(runId, reason)));

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,

            // Deliberately NOT ReadOnly — see this type's remarks. Every other run-lifecycle tool in
            // this sprint is; this one changes what a run does.
            ReadOnly = false,
        });
    }

    private static CallToolResult Render(CancelRunOutcome outcome) => outcome switch
    {
        // Both `cancelled` and `already_finished` are SUCCESSES — the story's AC and its own Gherkin
        // require isError: false for the second one specifically, since a polling host loses that race
        // routinely and "the run had already finished" is an answer, not a failure to answer.
        CancelRunOutcome.Answered answered =>
            StructuredToolResult.Success(answered.Result),

        CancelRunOutcome.InvalidArgument invalidArgument =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),
        CancelRunOutcome.RunNotFound runNotFound =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.RunNotFound, runNotFound.Message)),
        CancelRunOutcome.NotCancellable notCancellable =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.RunNotCancellable, notCancellable.Message)),
        CancelRunOutcome.StaleEntry staleEntry =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.StaleRunEntry, staleEntry.Message)),

        _ =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.UnrecognisedOutcome, "cancel_run produced an unrecognised outcome.")),
    };
}
