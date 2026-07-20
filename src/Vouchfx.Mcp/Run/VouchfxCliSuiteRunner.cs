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
/// side-effecting network call the operator did not explicitly opt into).
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
/// </remarks>
public sealed class VouchfxCliSuiteRunner : ISuiteRunner
{
    /// <summary>
    /// How long the exit-confirmation wait after a force-kill is allowed to take. Mirrors
    /// <see cref="Cli.VouchfxCliProcessRunner"/>'s identical constant/rationale.
    /// </summary>
    private static readonly TimeSpan KillConfirmationTimeout = TimeSpan.FromSeconds(5);

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
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(spec.SuitePath);
        startInfo.ArgumentList.Add("--events");
        startInfo.ArgumentList.Add(spec.EventsFilePath);
        startInfo.ArgumentList.Add("--fail-on-env-error");
        startInfo.ArgumentList.Add("--fail-on-inconclusive");
        startInfo.ArgumentList.Add("--no-decorations");
        startInfo.ArgumentList.Add("--no-telemetry");
        foreach (var tag in spec.Tags)
        {
            startInfo.ArgumentList.Add("--tag");
            startInfo.ArgumentList.Add(tag);
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

        using (process)
        {
            // Never inherits this server's own real stdin (the MCP protocol's read side, in
            // production) — mirrors every other process-spawn boundary in this codebase.
            process.StandardInput.Close();

            // Reading starts BEFORE waiting for exit: a suite run can produce far more console
            // output over its lifetime than fits in the OS pipe buffer, and nothing would drain it
            // otherwise, deadlocking against WaitForExitAsync — the same rationale as every other
            // process-spawn boundary in this codebase. Neither task is EVER awaited unboundedly
            // below — see this type's remarks on the BLOCKER this fixes.
            var stdoutTask = RelayAsync(process.StandardOutput, onOutputLine, linePrefix: null);
            var stderrTask = RelayAsync(process.StandardError, onOutputLine, linePrefix: "[stderr] ");

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await ForceKillEntireProcessTreeAsync(process);

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
    }

    /// <summary>
    /// EDGE-002's termination: force-kills the ENTIRE process tree the OS can see hanging off the
    /// vouchfx CLI process, then confirms the exit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A known, documented limitation — corrected during review.</b> An earlier version of this
    /// method killed only the IMMEDIATE process, then waited up to 30 seconds "to give the engine's
    /// own DCP teardown a chance to run" before a full-tree force-kill "backstop". That framing was
    /// misleading: killing the immediate process makes it exit within milliseconds, so the wait
    /// always returned almost immediately regardless of the 30-second bound, and the documented
    /// "backstop" was consequently unreachable dead code — the comment described a behaviour the
    /// code never actually performed. Corrected here: there is no separate "graceful" phase and no
    /// grace period at all — the WHOLE process tree is force-killed immediately.
    /// </para>
    /// <para>
    /// <b>What this does NOT do, and why:</b> a genuine OS-level graceful-shutdown SIGNAL — the thing
    /// that would let the CLI's own <c>System.CommandLine</c> cancellation-token plumbing observe the
    /// request and run <c>HeadlessTopology.DisposeAsync</c>'s real teardown, cleanly stopping
    /// containers and removing the <c>aspire-session-network</c> — is NOT IMPLEMENTED. It would
    /// require platform-specific P/Invoke this server does not attempt: on Windows,
    /// <c>GenerateConsoleCtrlEvent</c> only targets a distinct console PROCESS GROUP, which
    /// <see cref="Process.Start(ProcessStartInfo)"/> has no supported way to request (it would require
    /// bypassing <see cref="Process"/> entirely via a raw <c>CreateProcess</c> P/Invoke with
    /// <c>CREATE_NEW_PROCESS_GROUP</c>, then further P/Invoke at signal time); on Unix there is no
    /// BCL-exposed "send SIGTERM to an arbitrary child" either. Implementing either correctly — with
    /// NO way to exercise the Windows half in this repo's Linux CI — was judged higher-risk than the
    /// honestly-documented gap here: a P/Invoke mistake could destabilise THIS SERVER's own process,
    /// not just fail to signal the child.
    /// </para>
    /// <para>
    /// <b>The consequence:</b> Docker containers and the <c>aspire-session-network</c> are NOT torn
    /// down by this method — they are not OS child processes reachable via
    /// <c>entireProcessTree: true</c> in the first place. Their cleanup relies ENTIRELY on
    /// Testcontainers' own Ryuk reaper, which independently reaps orphaned containers regardless of
    /// how or whether the parent process exited. This is a KNOWN LIMITATION; verifying that reaping
    /// actually happens in practice is an integration concern, deferred to real end-to-end
    /// verification (todo 13). This method's own contract — and what
    /// <see cref="RunSuiteOrchestrator"/>'s unit tests verify against a fake runner — is only that it
    /// returns promptly and the caller's eventual result is classified as cancelled/timed-out
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
