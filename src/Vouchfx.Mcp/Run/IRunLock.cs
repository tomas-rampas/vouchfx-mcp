namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — IRunLock / RunLockResult (Sprint 3 / US-S3-04; spec §4.6, plan §2.5).
//
// "Runs are serialized per workspace with a file lock (<outputDir>/.lock)" (spec §4.6). This is the
// seam that claim travels through: RunSuiteOrchestrator asks for the claim, the implementation
// decides, and the orchestrator never learns how. The only implementation that ships is
// WorkspaceRunLock; with no --workspace there is no <outputDir> to put a lock file in, so the
// orchestrator is handed `null` and its in-process flag remains the only guard — see
// VouchfxMcpServerRegistration for that choice and RunSuiteOrchestrator for what `null` means there.

/// <summary>
/// The per-workspace "a run is in progress" claim (spec §4.6) — acquired for the duration of one
/// run and released when it ends, however it ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-blocking by contract.</b> <see cref="TryAcquire"/> answers immediately: a caller that
/// cannot have the claim is REJECTED (<c>VFX-E-1501 RunInProgress</c>, <c>retryable: true</c>), never
/// queued. Spec §4.6 states the rejection, not a wait, and the MCP tool call that provokes it has a
/// host on the other end that can retry on its own schedule — a server-side wait would hold an MCP
/// request open for the whole of somebody else's run.
/// </para>
/// <para>
/// <b>The claim is about STARTING a run, never about reading one.</b> Nothing on the read side —
/// <c>explain_run</c>, <c>diagnose_run</c>, or the registry's own <see cref="IRunRegistry.ListRuns"/>
/// / <see cref="IRunRegistry.TryGetRun"/> — takes this lock, which is what makes spec §4.6's
/// "read-only tools are safe to call concurrently" true structurally rather than by convention. The
/// registry's one-document-per-run layout is what allows that: a registry write touches only its own
/// run's file, so it never needed serialising against anything (see <see cref="FileRunRegistry"/>'s
/// format-choice remarks, which chose that layout with this story in mind).
/// </para>
/// </remarks>
public interface IRunLock
{
    /// <summary>
    /// Attempts to claim the workspace for one run, without waiting.
    /// </summary>
    /// <returns>
    /// <see cref="RunLockResult.Acquired"/> — whose <see cref="RunLockResult.Acquired.Release"/> MUST
    /// be disposed when the run ends — or <see cref="RunLockResult.HeldByAnotherRun"/>, or
    /// <see cref="RunLockResult.Unavailable"/>.
    /// </returns>
    RunLockResult TryAcquire();
}

/// <summary>
/// The outcome of <see cref="IRunLock.TryAcquire"/> — a closed discriminated union (a private
/// constructor confines derivation to the cases nested here), mirroring
/// <see cref="RunSuiteOutcome"/>'s and <see cref="Cli.CliPinResult"/>'s shape for the same reason:
/// "the claim was refused because somebody has it" and "the claim could not be evaluated at all" are
/// different facts that map to different <c>VFX-E-</c> codes, and a bare <see langword="bool"/> would
/// have collapsed them into one.
/// </summary>
public abstract record RunLockResult
{
    private RunLockResult()
    {
    }

    /// <summary>The claim is held by this caller until <see cref="Release"/> is disposed.</summary>
    /// <param name="Release">
    /// Releases the claim. Disposal is idempotent in every implementation here, so a caller may
    /// dispose it from a <c>finally</c> without tracking whether an earlier path already did.
    /// </param>
    public sealed record Acquired(IDisposable Release) : RunLockResult;

    /// <summary>
    /// Another run holds the claim — the <c>VFX-E-1501 RunInProgress</c> case. Carries no run id:
    /// WHICH run is active is the run registry's question, not the lock's (see
    /// <see cref="WorkspaceRunLock"/>'s remarks on why the lock file itself deliberately holds
    /// nothing).
    /// </summary>
    public sealed record HeldByAnotherRun : RunLockResult;

    /// <summary>
    /// The claim could not be evaluated because the workspace's output directory refused the
    /// operation — a read-only location, a denied ACL, an exhausted volume. NOT a statement that a
    /// run is in progress, and deliberately not reported as one: a host told <c>RunInProgress</c>
    /// (<c>retryable: true</c>) would poll forever against a directory that will never accept a run.
    /// </summary>
    /// <param name="Failure">
    /// The underlying fault. Only its TYPE ever leaves this process — BCL filesystem exceptions
    /// routinely embed a full path, and this codebase's standing policy (see
    /// <c>PinFailureReporting</c>) is never to forward their <c>Message</c>.
    /// </param>
    public sealed record Unavailable(Exception Failure) : RunLockResult;
}
