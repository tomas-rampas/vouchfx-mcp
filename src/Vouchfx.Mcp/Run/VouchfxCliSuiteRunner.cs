using System.Diagnostics;
using System.Text;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// The production <see cref="ISuiteRunner"/>: runs the real <c>vouchfx run</c> as a child process
/// (REQ-006).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never a bare command name (CWE-427), same as the version-handshake boundary:</b> the
/// executable path is resolved to an ABSOLUTE path via <see cref="VouchfxCliPathResolver"/> before
/// <see cref="Process.Start(ProcessStartInfo)"/> is ever called — see that type's remarks. By the
/// time <see cref="RunAsync"/> is reached, <see cref="Cli.CliPinVerifier"/> has already resolved and
/// verified this exact CLI once (<see cref="RunSuiteOrchestrator"/>'s gate ordering), but the path
/// is re-resolved here rather than threaded through, so this type has no dependency on
/// <see cref="Cli.CliPinResult"/> at all — it only needs "where is vouchfx", which
/// <see cref="VouchfxCliPathResolver"/> already answers safely and cheaply (a PATH scan, no I/O
/// beyond that).
/// </para>
/// <para>
/// <b>Flags always passed, and why:</b> <c>--events &lt;file&gt;</c> (the JSON Lines event stream
/// <see cref="SuiteEventParser"/> reads once this returns), <c>--fail-on-env-error
/// --fail-on-inconclusive</c> (turns the exit code into a clean 1:1 map of the four verdicts —
/// 0=Pass, 1=Fail, 3=EnvironmentError, 4=Inconclusive — used as <see cref="RunSuiteOrchestrator"/>'s
/// fallback classifier when the events file yields no <c>scenario-completed</c> event at all, e.g.
/// EDGE-001's "failed before any scenario could start"), <c>--no-decorations</c> (no ANSI escape
/// codes in relayed stdout — <see cref="TextSanitiser"/> would neutralise them into <c>\uXXXX</c>
/// noise anyway, but asking the CLI not to emit them in the first place keeps relayed lines
/// readable), <c>--no-telemetry</c> (an automated, agent-driven invocation must never trigger a
/// side-effecting network call the operator did not explicitly opt into), <c>--shutdown-on-stdin-eof</c>
/// (todo 17, graceful teardown — see the dedicated remarks paragraph below for what it does and why
/// passing it unconditionally is safe).
/// </para>
/// <para>
/// <b>Live progress is coarse, not per-step (see <see cref="SuiteEventParser"/>'s remarks for the
/// full finding):</b> the engine buffers its ENTIRE JSON Lines event stream in memory and writes it
/// to <c>--events</c>'s file exactly once, after the whole scenario loop completes — confirmed both
/// from <c>ScenarioRunner.RunSuiteAsync</c>'s source (a single <c>FileReportWriter.WriteFileReports</c>
/// call after the loop) and empirically (a real run's events file did not exist until near the very
/// end of a 17-second run). Genuine per-step live progress from that file is therefore architecturally
/// impossible with the current engine. What IS genuinely live is the child's own stdout/stderr —
/// Aspire/DCP startup diagnostics ("Starting DCP...", health-gate waits, etc.) — which this type
/// relays line-by-line, sanitised, as the best-effort progress signal while the run is actually in
/// flight. The rich, structured per-step breakdown only becomes available in the FINAL result, built
/// by <see cref="RunSuiteOrchestrator"/> from the events file after this method returns.
/// </para>
/// <para>
/// <b>This method's return is NEVER gated on the child's stdout/stderr pipes reaching EOF</b> — a
/// BLOCKER found in review: a surviving grandchild process (DCP/Testcontainers infrastructure that
/// inherited the write end of a redirected pipe) can keep a pipe open indefinitely even after the
/// <c>vouchfx</c> process itself has exited or been killed, so a relay loop reading until EOF may
/// never return. Earlier code awaited that relay directly to build a stderr excerpt, which meant a
/// single surviving grandchild could hang <see cref="RunAsync"/> forever — and because
/// <see cref="RunSuiteOrchestrator"/> awaits this call inside its single-flight gate, THAT would wedge
/// the gate permanently (every subsequent <c>run_suite</c> call would report "already in progress"
/// until the server itself restarted), and would also bypass the caller's own timeout budget (which
/// only wraps <see cref="Process.WaitForExitAsync(CancellationToken)"/>, not this relay). Relay tasks
/// are therefore NEVER awaited unboundedly in either branch below: a short, bounded wait
/// (<see cref="RelayDrainGrace"/>) is given for the excerpt in the common case (the relay usually
/// finishes almost immediately once the process itself has exited), and past that bound the task is
/// abandoned via <see cref="BoundedStreamReader.ObserveQuietly"/> — fire-and-forget, never blocking
/// this method's return regardless of what a surviving child does with the pipe.
/// </para>
/// <para>
/// <b>Graceful teardown (todo 17) — delivered, not aspirational:</b> the engine half of this
/// feature shipped in <c>v1.0.0-alpha.10</c> (see <c>ENGINE_PIN</c>'s pin history): a CLI started
/// with <c>--shutdown-on-stdin-eof</c> watches its OWN stdin, and on EOF cancels its internal token,
/// runs its DCP/Testcontainers teardown (<c>HeadlessTopology.DisposeAsync</c>) to completion — up to
/// its own ~30-second internal budget — and force-exits itself via an internal
/// <c>ShutdownBackstop</c> if that doesn't finish in time. This type's half: keep the child's
/// redirected stdin OPEN for the run's duration (never closed at spawn, unlike every other
/// process-spawn boundary in this codebase) so that, on cancellation/timeout,
/// <see cref="RunAgainstProcessAsync"/> can close it as the graceful-stop SIGNAL, then wait a
/// bounded <see cref="GracefulShutdownGrace"/> for the engine to act on it before ever falling back
/// to <see cref="ForceKillEntireProcessTreeAsync"/>. Holding the pipe open is safe precisely because
/// <c>RedirectStandardInput=true</c> gives the child its OWN, separate redirected pipe — never this
/// server's actual MCP stdin — so the child can never read anything this server itself receives
/// over the real protocol channel; nothing is ever WRITTEN to it either, only closed.
/// </para>
/// <para>
/// <b>Version safety of passing <c>--shutdown-on-stdin-eof</c> unconditionally (no runtime
/// feature-sniffing):</b> confirmed safe by tracing the gate ordering one layer up. This type is
/// only ever reached via <see cref="RunSuiteOrchestrator.ExecuteRunAsync"/>, which runs only after
/// <c>RunSuiteOrchestrator.RunAsync</c>'s <see cref="Cli.CliPinVerifier.VerifyAsync"/> gate returned
/// <see cref="Cli.CliPinResult.Ok"/> — every OTHER outcome (<c>NotFound</c>, <c>VersionMismatch</c>,
/// <c>Unparseable</c>) short-circuits into <c>RunSuiteOutcome.CliUnavailable</c> BEFORE the
/// single-flight gate, let alone a runner, is ever reached. <c>Ok</c> means the resolved CLI's own
/// reported version matched <c>ENGINE_PIN</c> EXACTLY (via <see cref="Cli.CliVersionNormaliser"/>),
/// so by the time <see cref="RunAsync"/> builds an argument list, the spawned CLI is GUARANTEED to
/// be the exact pinned build — currently <c>v1.0.0-alpha.10</c>, which supports the flag. The
/// handshake therefore GATES execution rather than merely warning on a mismatch, which is what makes
/// passing the flag unconditionally (rather than conditionally, based on a parsed version number)
/// the safe choice: there is no code path by which an older, flag-ignorant CLI ever reaches this
/// method. (Were the gate ever weakened to a warn-only check, this flag would need to become
/// conditional on the resolved version — noted here so that coupling is not lost if that ever
/// changes.)
/// </para>
/// </remarks>
public sealed class VouchfxCliSuiteRunner : ISuiteRunner
{
    /// <summary>
    /// How long the exit-confirmation wait after a force-kill is allowed to take. Mirrors
    /// <see cref="Cli.VouchfxCliProcessRunner"/>'s identical constant/rationale.
    /// </summary>
    private static readonly TimeSpan KillConfirmationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long EDGE-002's cancellation/timeout path waits, after requesting a GRACEFUL stop by
    /// closing the child's stdin, for the child to exit on its own before falling back to
    /// <see cref="ForceKillEntireProcessTreeAsync"/> (todo 17, graceful teardown). MUST exceed the
    /// engine's own internal teardown budget (~30 seconds — its DCP/Testcontainers teardown, itself
    /// bounded by an internal <c>ShutdownBackstop</c> that force-exits the engine if teardown alone
    /// would overrun it) so that budget always gets a genuine chance to run to completion — or hit
    /// its OWN backstop — before this server gives up and force-kills instead: 30s plus a 5s margin
    /// for process-exit-signal propagation and OS scheduling jitter on a loaded CI runner.
    /// </summary>
    /// <remarks>
    /// A consequence, accepted by design (todo 17's brief): a cancelled/timed-out <c>run_suite</c>
    /// call can now take up to this long LONGER to return than it did before this grace existed —
    /// e.g. a 1-second <c>timeoutSeconds</c> request can still take on the order of 35 seconds wall
    /// clock if the engine's own teardown genuinely needs that long. This is strictly better than
    /// the previous behaviour (an immediate whole-tree kill that left containers and the
    /// <c>aspire-session-network-*</c> orphaned for Ryuk to reap, typically ~16 seconds later — see
    /// <c>docs/validation/live-validation-2026-07-21.md</c>): the ordinary case now tears down
    /// cleanly, deterministically, and under this server's own observable timing rather than
    /// depending on a SEPARATE reaper process the caller cannot see or bound.
    /// </remarks>
    internal static readonly TimeSpan GracefulShutdownGrace = TimeSpan.FromSeconds(35);

    /// <summary>
    /// The MAXIMUM this method ever waits for a relay task before giving up on its excerpt and
    /// abandoning it in the background (see this type's remarks on why the relay is never awaited
    /// unboundedly). In the ordinary case — the process has genuinely exited and nothing else holds
    /// its stdout/stderr pipes open — the relay finishes within milliseconds of
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/> returning; this bound exists purely
    /// to cap the ABNORMAL case (a surviving grandchild holding a pipe open) rather than to be
    /// routinely hit.
    /// </summary>
    private static readonly TimeSpan RelayDrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum bytes of stdout/stderr ACTIVELY relayed as progress and captured for the EDGE-001
    /// fallback excerpt. Unlike <see cref="Cli.VouchfxCliProcessRunner.MaxCliOutputBytes"/> and
    /// <see cref="Validation.ValidationWorkerClient.MaxWorkerOutputBytes"/>, breaching this does NOT
    /// abort the run: a suite's own console chatter (health-gate retries, provider diagnostics) is
    /// secondary to the authoritative events-file result, and killing an otherwise-healthy long-running
    /// suite over verbose-but-harmless stdout would be a strictly worse outcome than simply capping
    /// how much of it this server bothers to relay. Reading continues, unbounded, past this cap — it
    /// is simply no longer relayed or retained — so the child's stdout/stderr pipes are still always
    /// drained (never risking the pipe-buffer deadlock the bounded-read pattern elsewhere in this
    /// codebase exists to avoid), just without unbounded memory growth on this server's side.
    /// </summary>
    private const long MaxRelayedOutputBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Maximum characters accumulated for a SINGLE "line" with no terminating <c>\n</c> before it is
    /// flushed as its own segment. Without this bound, a child emitting one huge line (no newline at
    /// all) would make the character-by-character accumulator underneath <see cref="RelayAsync"/>
    /// grow without limit even though <see cref="MaxRelayedOutputBytes"/> is checked only once a
    /// line is complete — this is what actually enforces "no single line balloons memory on its own",
    /// independent of the overall relay cap.
    /// </summary>
    private const int MaxSingleLineChars = 64 * 1024;

    public async Task<SuiteProcessResult> RunAsync(
        SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(onOutputLine);

        var resolvedPath = VouchfxCliPathResolver.ResolveAbsolutePath()
            ?? throw new InvalidOperationException(
                "The vouchfx CLI could not be resolved on PATH. This should not happen here: " +
                "RunSuiteOrchestrator's CliPinVerifier gate already confirmed it was resolvable " +
                "before a runner was ever invoked.");

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in BuildArguments(spec))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null despite UseShellExecute=false.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new SuiteProcessResult(
                null, RunTermination.CompletedNormally, $"Could not start the vouchfx CLI ({ex.GetType().Name}).");
        }

        return await RunAgainstProcessAsync(process, onOutputLine, GracefulShutdownGrace, cancellationToken);
    }

    /// <summary>
    /// Builds the exact CLI argument list <see cref="RunAsync"/> spawns <c>vouchfx</c> with, as a
    /// pure function of <paramref name="spec"/> — factored out purely so a test can assert its
    /// contents (e.g. that <c>--shutdown-on-stdin-eof</c> is present) without needing the real
    /// <c>vouchfx</c> CLI on PATH to actually spawn anything. See this type's remarks for why each
    /// flag is always passed.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(SuiteRunSpec spec)
    {
        var arguments = new List<string>
        {
            "run",
            spec.SuitePath,
            "--events",
            spec.EventsFilePath,
            "--fail-on-env-error",
            "--fail-on-inconclusive",
            "--no-decorations",
            "--no-telemetry",
            "--shutdown-on-stdin-eof",
        };

        foreach (var tag in spec.Tags)
        {
            arguments.Add("--tag");
            arguments.Add(tag);
        }

        return arguments;
    }

    /// <summary>
    /// Drives an ALREADY-STARTED, stdin/stdout/stderr-redirected child process through the relay/
    /// wait/cancel/graceful-stop-then-force-kill sequence <see cref="RunAsync"/> uses in production
    /// — factored out as an internal seam, parameterised by an injectable
    /// <paramref name="gracePeriod"/>, so <c>VouchfxCliSuiteRunnerTests</c> can prove the sequence's
    /// real behaviour (a graceful exit within grace vs. the force-kill backstop firing at the grace
    /// deadline) against a REAL spawned test-fixture process and a small, fast grace bound, rather
    /// than needing the real <c>vouchfx</c> CLI on PATH and production's real ~35-second one.
    /// </summary>
    /// <remarks>
    /// Every BLOCKER-avoidance property this type's own remarks document for <see cref="RunAsync"/>
    /// applies identically here, because this IS the body <see cref="RunAsync"/> delegates to: the
    /// relay tasks are started before the wait, never awaited unboundedly in either branch, and the
    /// stdin-close-then-bounded-wait-then-force-kill sequence on the abort path is itself fully
    /// bounded — so <see cref="RunSuiteOrchestrator"/>'s single-flight gate can never be wedged by
    /// what this method does, only by what the child process itself does (which it cannot control
    /// beyond that same bound).
    /// </remarks>
    internal static async Task<SuiteProcessResult> RunAgainstProcessAsync(
        Process process, Action<string> onOutputLine, TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        using (process)
        {
            try
            {
                // Deliberately NOT closed here (a departure from every other process-spawn boundary
                // in this codebase, each of which closes stdin immediately): this pipe is this
                // child's OWN redirected stdin, never this server's real MCP stdin, so holding it
                // open is safe, and doing so is exactly what lets the cancellation branch below
                // request a graceful engine stop later by closing it as the EOF signal. See this
                // type's remarks on todo 17's graceful teardown for the full design.

                // Reading starts BEFORE waiting for exit: a suite run can produce far more console
                // output over its lifetime than fits in the OS pipe buffer, and nothing would drain
                // it otherwise, deadlocking against WaitForExitAsync — the same rationale as every
                // other process-spawn boundary in this codebase. Neither task is EVER awaited
                // unboundedly below — see this type's remarks on the BLOCKER this fixes.
                var stdoutTask = RelayAsync(process.StandardOutput, onOutputLine, linePrefix: null);
                var stderrTask = RelayAsync(process.StandardError, onOutputLine, linePrefix: "[stderr] ");

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await StopGracefullyThenForceKillAsync(process, gracePeriod);

                    // The stderr excerpt is UNUSED on the abort path (RunSuiteOrchestrator always
                    // classifies an aborted run as Inconclusive directly, never consulting it) — so
                    // there is no reason to wait for it at all, bounded or not. Both relays are
                    // abandoned in the background immediately.
                    BoundedStreamReader.ObserveQuietly(stdoutTask);
                    BoundedStreamReader.ObserveQuietly(stderrTask);
                    return new SuiteProcessResult(TryGetExitCode(process), RunTermination.Aborted, StderrExcerpt: null);
                }

                // The process exited on its own, but a surviving grandchild could still be holding a
                // pipe open (see this type's remarks) — bounded wait only, never unbounded.
                var stdoutExcerpt = await TryGetExcerptWithBoundedWaitAsync(stdoutTask);
                var stderrExcerpt = await TryGetExcerptWithBoundedWaitAsync(stderrTask);
                _ = stdoutExcerpt; // stdout's excerpt has no consumer today; stderr is what EDGE-001 inspects.

                return new SuiteProcessResult(process.ExitCode, RunTermination.CompletedNormally, stderrExcerpt);
            }
            finally
            {
                // Closes the handle on EVERY path, including normal completion (todo 17 point 5):
                // by the time either return above is reached the child has already exited (normal
                // completion: WaitForExitAsync returned; abort path: StopGracefullyThenForceKillAsync
                // always runs its own force-kill-and-confirm to completion before returning), so this
                // is pure cleanup — never a second stop signal raced against a still-running process.
                // Safe to call even when StopGracefullyThenForceKillAsync already closed it on the
                // abort path: TryCloseStandardInput swallows the resulting no-op/already-closed case.
                TryCloseStandardInput(process);
            }
        }
    }

    /// <summary>
    /// EDGE-002's cancellation/timeout stop sequence (todo 17, graceful teardown): first requests a
    /// GRACEFUL stop by closing the child's redirected stdin — the EOF signal an engine started with
    /// <c>--shutdown-on-stdin-eof</c> uses as its own cue to cancel its internal token and run its
    /// DCP/Testcontainers teardown to completion — then waits, BOUNDED by
    /// <paramref name="gracePeriod"/>, for the child to exit on its own. If the child is STILL alive
    /// once that bound elapses, falls back to <see cref="ForceKillEntireProcessTreeAsync"/> as the
    /// last-resort backstop (e.g. a genuine teardown hang past the engine's own internal budget).
    /// </summary>
    /// <remarks>
    /// ALWAYS returns — never unboundedly — regardless of what the child does: the wait for the
    /// graceful exit is itself bounded by <paramref name="gracePeriod"/>, and
    /// <see cref="ForceKillEntireProcessTreeAsync"/> already bounds its own kill-confirmation wait
    /// (see that method's remarks). This is what keeps <see cref="RunAgainstProcessAsync"/> — and
    /// therefore <see cref="RunSuiteOrchestrator"/>'s single-flight gate — safe from being wedged by
    /// a child that never cooperates at all.
    /// </remarks>
    private static async Task StopGracefullyThenForceKillAsync(Process process, TimeSpan gracePeriod)
    {
        TryCloseStandardInput(process);

        try
        {
            using var graceCts = new CancellationTokenSource(gracePeriod);
            await process.WaitForExitAsync(graceCts.Token);

            // Exited gracefully within the grace period — the engine's own teardown (or its
            // internal ShutdownBackstop, if teardown itself ran long) completed in time. Nothing
            // further to do; the force-kill backstop below is never reached.
            return;
        }
        catch (OperationCanceledException)
        {
            // The grace period elapsed and the child is still alive. This is never expected against
            // the real, pinned CLI in production (see this type's remarks on why the version
            // handshake makes passing --shutdown-on-stdin-eof unconditionally safe), so reaching
            // here means either a genuine teardown that outran even the engine's own internal
            // backstop, or — in a test — a fixture child that deliberately never exits. Either way,
            // the last-resort backstop below is what guarantees this method still returns.
        }

        await ForceKillEntireProcessTreeAsync(process);
    }

    /// <summary>
    /// Best-effort, idempotent close of the child's redirected stdin: safe to call more than once
    /// (e.g. once from <see cref="StopGracefullyThenForceKillAsync"/>'s graceful-stop signal, and
    /// again from <see cref="RunAgainstProcessAsync"/>'s cleanup <c>finally</c>) and safe to call on
    /// a process that has already exited — any failure is swallowed, since this is cleanup of this
    /// server's OWN side of a pipe it owns, never a signal whose delivery must be confirmed.
    /// </summary>
    private static void TryCloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate, best-effort:
        // mirrors every other kill-attempt/cleanup boundary in this codebase.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// EDGE-002's LAST-RESORT termination: force-kills the ENTIRE process tree the OS can see
    /// hanging off the vouchfx CLI process, then confirms the exit. Reached only when
    /// <see cref="StopGracefullyThenForceKillAsync"/>'s graceful-stop-then-bounded-wait did not
    /// observe the child exit on its own within <see cref="GracefulShutdownGrace"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No longer the whole story — todo 17 gave this a real graceful phase in front of it.</b> An
    /// earlier version of this file had NO grace period at all (an even earlier version's documented
    /// 30-second "backstop" wait was dead code, since it raced a kill of the immediate process that
    /// always won near-instantly regardless of the bound) — this method was reached unconditionally,
    /// immediately, on every cancellation/timeout. That is no longer true: since todo 17,
    /// <see cref="StopGracefullyThenForceKillAsync"/> now requests a genuine graceful stop first (by
    /// closing the child's stdin, which an engine built with <c>--shutdown-on-stdin-eof</c> uses to
    /// run its own teardown to completion) and only falls back to THIS method once a real,
    /// documented grace period (<see cref="GracefulShutdownGrace"/>) has elapsed with the child still
    /// alive. This method itself is UNCHANGED — still an immediate, unconditional whole-tree kill,
    /// with no grace period of its own — but it is now reached only as a genuine last resort rather
    /// than on every abort.
    /// </para>
    /// <para>
    /// <b>What this method itself does NOT do, and why:</b> a genuine OS-level graceful-shutdown
    /// SIGNAL — the thing that would let the CLI's own <c>System.CommandLine</c> cancellation-token
    /// plumbing observe an OS-level request directly — is still NOT IMPLEMENTED here, and still
    /// would require platform-specific P/Invoke this server does not attempt (see the previous
    /// revision of this remark for the full Windows/<c>GenerateConsoleCtrlEvent</c>-vs-Unix-SIGTERM
    /// analysis; it stands unchanged). Todo 17 sidesteps that gap entirely rather than closing it: the
    /// engine watches its OWN stdin for EOF as its graceful-shutdown cue instead of needing an OS
    /// signal at all, which is exactly what <see cref="StopGracefullyThenForceKillAsync"/> now
    /// exploits — a portable mechanism this server already had full, safe control over
    /// (<c>ProcessStartInfo.RedirectStandardInput</c>), with no P/Invoke risk whatsoever.
    /// </para>
    /// <para>
    /// <b>The consequence, now narrowed to the fallback case only:</b> when THIS method is reached
    /// (grace exceeded), Docker containers and the <c>aspire-session-network</c> are still NOT torn
    /// down by it — they are not OS child processes reachable via <c>entireProcessTree: true</c> in
    /// the first place. Their cleanup in that specific case still relies on Testcontainers' own Ryuk
    /// reaper, which independently reaps orphaned containers regardless of how or whether the parent
    /// process exited — confirmed to actually happen in practice (~16 seconds) by the real,
    /// documented live drill in <c>docs/validation/live-validation-2026-07-21.md</c>, run before
    /// todo 17's graceful phase existed. In the ORDINARY case — the engine honours
    /// <c>--shutdown-on-stdin-eof</c> and tears down within <see cref="GracefulShutdownGrace"/>, which
    /// this method is never even reached for — the engine's OWN teardown
    /// (<c>HeadlessTopology.DisposeAsync</c>) does the real cleanup, and Ryuk is demoted to a backstop
    /// for the rare fallback case rather than the sole mechanism. Re-running that live drill against
    /// the graceful path (todo 13-style, real Docker, real cancellation) would confirm this narrowing
    /// empirically; it has not been re-run since todo 17 landed. This method's own contract — and what
    /// <see cref="RunSuiteOrchestrator"/>'s unit tests verify against a fake runner — remains only that
    /// it returns promptly and the caller's eventual result is classified as cancelled/timed-out
    /// (Inconclusive), never Fail; never that any container was actually cleaned up.
    /// </para>
    /// </remarks>
    private static async Task ForceKillEntireProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate, best-effort:
        // mirrors every other kill-attempt boundary in this codebase.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            using var confirmCts = new CancellationTokenSource(KillConfirmationTimeout);
            await process.WaitForExitAsync(confirmCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Best-effort confirmation only.
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: reading ExitCode
        // on a process in an unexpected state must never itself throw out of this best-effort helper.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Relays each complete line read from <paramref name="reader"/> to <paramref name="onLine"/>,
    /// sanitised, up to <see cref="MaxRelayedOutputBytes"/> — after which lines are still read (to
    /// keep draining the pipe) but no longer relayed or retained. Returns the same capped, sanitised
    /// text that was relayed, joined by newlines, for <see cref="RunSuiteOrchestrator"/>'s EDGE-001
    /// fallback excerpt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads in fixed-size character CHUNKS and does its own line-splitting — deliberately NOT
    /// <see cref="TextReader.ReadLineAsync()"/>, which buffers an entire line internally before
    /// returning it: a child emitting one huge "line" with no terminating <c>\n</c> at all would
    /// otherwise let that internal buffer grow without limit, regardless of
    /// <see cref="MaxRelayedOutputBytes"/> (which is only ever checked once a line is already
    /// complete). <see cref="MaxSingleLineChars"/> bounds a single unterminated line by flushing what
    /// has accumulated as its own segment once that bound is hit, so a runaway single line is instead
    /// relayed as several bounded segments.
    /// </para>
    /// <para>
    /// <b>The cap is checked BEFORE a line is added, not after</b> (a review fix): checking only
    /// whether <c>relayedBytes</c> had ALREADY reached the cap from PRIOR lines would still let the
    /// line currently being flushed push the total past <see cref="MaxRelayedOutputBytes"/> — the
    /// stated bound would not actually hold for the one line that crosses it. Each candidate line's
    /// own byte size is computed first and added to the running total only if the sum still fits
    /// within the cap; once a line would not fit, <c>relayedBytes</c> is pinned at the cap so every
    /// subsequent line short-circuits on the cheap top check without needing sanitising or
    /// byte-counting at all.
    /// </para>
    /// </remarks>
    private static async Task<string> RelayAsync(StreamReader reader, Action<string> onLine, string? linePrefix)
    {
        var captured = new StringBuilder();
        var relayedBytes = 0L;
        var lineBuffer = new StringBuilder();

        void FlushLine()
        {
            if (lineBuffer.Length == 0)
            {
                return;
            }

            var raw = lineBuffer.ToString();
            lineBuffer.Clear();

            if (relayedBytes >= MaxRelayedOutputBytes)
            {
                return;
            }

            var sanitised = TextSanitiser.SanitiseForDisplay(raw);
            var lineBytes = Encoding.UTF8.GetByteCount(sanitised);

            if (relayedBytes + lineBytes > MaxRelayedOutputBytes)
            {
                // This line alone would push the running total past the cap: skip it (and, since
                // relayedBytes never decreases, every line after it too) rather than let the stated
                // bound be exceeded by the very line that crosses it.
                relayedBytes = MaxRelayedOutputBytes;
                return;
            }

            relayedBytes += lineBytes;
            captured.Append(sanitised).Append('\n');
            onLine(linePrefix is null ? sanitised : linePrefix + sanitised);
        }

        var chunk = new char[4096];
        int charsRead;
        while ((charsRead = await reader.ReadAsync(chunk.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < charsRead; i++)
            {
                var c = chunk[i];
                if (c == '\n')
                {
                    FlushLine();
                }
                else if (c != '\r')
                {
                    lineBuffer.Append(c);
                    if (lineBuffer.Length >= MaxSingleLineChars)
                    {
                        // A single "line" with no newline has grown past the bound: flush what has
                        // accumulated as its own segment rather than buffering it unboundedly, and
                        // keep draining — a child emitting one huge line without '\n' must not be
                        // able to balloon this server's memory one character at a time.
                        FlushLine();
                    }
                }
            }
        }

        FlushLine();

        return captured.ToString();
    }

    /// <summary>
    /// Waits AT MOST <see cref="RelayDrainGrace"/> for <paramref name="relayTask"/>'s excerpt; past
    /// that bound, abandons it via <see cref="BoundedStreamReader.ObserveQuietly"/> instead of
    /// waiting any further — see this type's remarks on why the relay must never be awaited
    /// unboundedly.
    /// </summary>
    private static async Task<string?> TryGetExcerptWithBoundedWaitAsync(Task<string> relayTask)
    {
        var winner = await Task.WhenAny(relayTask, Task.Delay(RelayDrainGrace, CancellationToken.None));
        if (winner != relayTask)
        {
            // Did not finish within the bound — most likely a surviving grandchild holds the pipe
            // open. Never awaited further: observed quietly in the background so a late fault does
            // not surface as an unobserved task exception, and this method returns without blocking
            // its caller on it.
            BoundedStreamReader.ObserveQuietly(relayTask);
            return null;
        }

        try
        {
            return await relayTask;
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: reading the
        // already-exited process's own redirected stream should not itself throw in practice, but
        // this is a defensive boundary so any unexpected I/O failure here never escapes as an
        // unhandled exception from the run_suite tool handler.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
