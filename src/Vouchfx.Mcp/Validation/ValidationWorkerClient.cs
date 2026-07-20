using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The <c>validate_suite</c> tool's process-isolation boundary: runs the parts of
/// <see cref="SuiteValidator"/>'s pipeline that touch untrusted YAML content inside a disposable
/// child process instead of this server's own long-lived one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a child process, not another in-process guard:</b> see <see cref="YamlSafetyGuard"/>'s
/// remarks for the four rounds of adversarial hardening that led to bounding nesting depth via
/// YamlDotNet's own <c>Scanner</c>. A final adversarial pass found that the Scanner itself — the
/// very mechanism that check relies on — can be driven into an unbounded, ~100%-CPU spin by a
/// tiny, otherwise well-formed input (a scalar <c>a: b</c> immediately followed by a
/// MORE-indented <c>a: b</c>). That is not a crash and not merely a slow parse: it is a genuinely
/// uninterruptible in-process hang. No <see cref="Task"/>/<see cref="CancellationToken"/>-based
/// timeout can recover from it, because the Scanner's loop has no cooperative cancellation check
/// anywhere in it to observe — cancelling the awaiting <see cref="Task"/> does not stop the
/// CPU-bound thread actually running inside the Scanner. Only OS-level process termination can.
/// Hence: run the whole existing, already-hardened <see cref="SuiteValidator"/> pipeline (every
/// check it makes stays fully in force — this class adds isolation, it does not replace any of
/// them) inside a separate, disposable child process, bounded by a wall-clock timeout, and kill
/// the entire process tree if it does not finish in time.
/// </para>
/// <para>
/// <b>What stays in-process, with no spawn at all:</b> <see cref="SuiteValidator.CheckFastRejects"/> —
/// a UNC/network path (M2) or a missing/inaccessible/oversized file needs no worker, since none of
/// those checks ever hand untrusted YAML text to YamlDotNet. Only a present, local, size-bounded
/// file's actual content reaches the child.
/// </para>
/// <para>
/// <b>The child is this SAME executable</b>, re-invoked with
/// <see cref="ValidationWorkerProtocol.WorkerModeArgument"/> (see <c>Program.cs</c>'s worker-mode
/// branch) — never the vouchfx CLI, and never a container. See <see cref="ResolveWorkerLaunch"/>
/// for how its path is found.
/// </para>
/// <para>
/// <b>Boundary hardening beyond the timeout/kill itself:</b> the child's stdin is redirected and
/// closed immediately (it never reads it, and this server's own real stdin — the MCP protocol's
/// read side, in production — must never be inherited into a disposable child); the child's
/// stdout/stderr are each read under an explicit <see cref="MaxWorkerOutputBytes"/> cap rather than
/// buffered without limit (the 5&#160;MB suite-size cap upstream bounds the INPUT, not the output a
/// misbehaving or compromised worker could in principle produce); and a kill is followed by a
/// short bounded wait to CONFIRM the process actually exited before the result claims it did.
/// </para>
/// </remarks>
public static class ValidationWorkerClient
{
    /// <summary>
    /// How long <see cref="ValidateAsync"/> waits for the worker child process before killing its
    /// entire process tree and reporting <c>validation-timeout</c>. A real <c>.e2e.yaml</c> suite
    /// validates in well under a second even on modest hardware (schema evaluation over a
    /// small/medium document, no network I/O); 10 seconds gives generous headroom for process
    /// start-up on a slow CI runner while still bounding how long a hung worker can occupy a slot
    /// before this server reclaims it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum bytes read from EITHER of the worker's stdout or stderr streams before it is
    /// treated as misbehaving: killed, and reported as <c>validation-worker-failed</c>.
    /// </summary>
    /// <remarks>
    /// The worker's only legitimate output is one serialised <see cref="ValidateSuiteResult"/> —
    /// at most one entry per genuine schema/YAML problem in a suite already capped at
    /// <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/> (5&#160;MB) input, plus modest JSON/pointer
    /// overhead per entry. 50&#160;MB is a generous 10x margin over that worst realistic case —
    /// large enough that no legitimate result is ever affected, small enough that this server
    /// never buffers an unbounded amount of a misbehaving or compromised child's output in its own
    /// memory. This is a defence at THIS boundary, independent of (not a substitute for) the
    /// upstream input cap: the two bound different things.
    /// </remarks>
    public const long MaxWorkerOutputBytes = 50L * 1024 * 1024;

    /// <summary>
    /// How long <see cref="KillAndConfirmExitAsync"/> waits, after asking the OS to kill the
    /// worker's process tree, for that exit to actually be observed before giving up on
    /// confirming it. Killing a process is normally near-instantaneous; a couple of seconds is
    /// generous headroom for a slow CI runner without materially extending how long an aborted
    /// call takes overall.
    /// </summary>
    private static readonly TimeSpan KillConfirmationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Validates the suite at <paramref name="path"/>, running the parts of the pipeline that
    /// touch untrusted YAML content inside an isolated, killable child process.
    /// </summary>
    /// <param name="path">Path to the <c>.e2e.yaml</c> suite file to validate.</param>
    /// <param name="timeout">
    /// Overrides <see cref="DefaultTimeout"/>. Exposed for tests that need a short bound rather
    /// than production's 10 seconds; callers outside this assembly's test suite should omit it.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelling this aborts the wait and kills the worker's process tree, then rethrows as an
    /// ordinary <see cref="OperationCanceledException"/> — distinct from this method's own
    /// <paramref name="timeout"/> firing, which is reported as a structured
    /// <c>validation-timeout</c> result rather than an exception.
    /// </param>
    /// <remarks>
    /// Never throws for a validation failure — every failure mode (fast reject, timeout, worker
    /// crash, unparseable worker output) is reported as a structured <see cref="ValidateSuiteResult"/>,
    /// exactly like every other <see cref="SuiteValidator"/> entry point. It CAN throw
    /// <see cref="OperationCanceledException"/>, but only when <paramref name="cancellationToken"/>
    /// itself was cancelled by the caller — not for this method's own timeout.
    /// </remarks>
    public static Task<ValidateSuiteResult> ValidateAsync(
        string path, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        ValidateAsyncCore(path, timeout, onWorkerStarted: null, cancellationToken);

    /// <summary>
    /// Test-only variant of <see cref="ValidateAsync"/> that additionally invokes
    /// <paramref name="onWorkerStarted"/> with the worker's OS process ID the instant it starts.
    /// </summary>
    /// <remarks>
    /// Exists so a test can assert DIRECTLY that a killed worker process has actually exited
    /// (<c>Process.GetProcessById</c> throwing, or <see cref="Process.HasExited"/>) rather than
    /// only inferring "no orphan" from elapsed timing. Deliberately <see langword="internal"/>, not
    /// an extra public parameter on <see cref="ValidateAsync"/>: visible to the test assembly only,
    /// via this assembly's <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal static Task<ValidateSuiteResult> ValidateAsyncForTesting(
        string path, TimeSpan? timeout, Action<int> onWorkerStarted, CancellationToken cancellationToken) =>
        ValidateAsyncCore(path, timeout, onWorkerStarted, cancellationToken);

    private static async Task<ValidateSuiteResult> ValidateAsyncCore(
        string path, TimeSpan? timeout, Action<int>? onWorkerStarted, CancellationToken cancellationToken)
    {
        // The fast, bounded, in-process pre-checks: a missing file or a UNC/network path (M2)
        // never reaches this far — neither ever hands untrusted YAML text to YamlDotNet, so
        // neither can hang, and spawning a worker for either would only add latency for no safety
        // benefit.
        var fastRejectError = SuiteValidator.CheckFastRejects(path);
        if (fastRejectError is not null)
        {
            return new ValidateSuiteResult(false, [fastRejectError]);
        }

        var (fileName, arguments) = ResolveWorkerLaunch(path);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
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
            return WorkerFailed($"Could not start the validation worker process ({ex.GetType().Name}).");
        }

        using (process)
        {
            onWorkerStarted?.Invoke(process.Id);

            // The worker never reads stdin. Redirecting and immediately closing it stops this
            // server's OWN stdin handle — the MCP protocol's read side, in the real server — from
            // ever being inherited into a disposable child process; without RedirectStandardInput,
            // the child would otherwise inherit the parent's real stdin handle unredirected.
            process.StandardInput.Close();

            var effectiveTimeout = timeout ?? DefaultTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            // Guards the cap-exceeded state transition: the stdout and stderr bounded readers run
            // concurrently (see below), so BOTH can breach MaxWorkerOutputBytes at roughly the same
            // time. CompareExchange guarantees exactly one of them wins the 0->1 transition and is
            // the one that calls timeoutCts.Cancel() — never both, and never a torn read of the
            // flag from the check further down.
            var outputCapExceeded = 0;
            void MarkOutputCapExceeded()
            {
                if (Interlocked.CompareExchange(ref outputCapExceeded, 1, 0) == 0)
                {
                    timeoutCts.Cancel();
                }
            }

            // Reading is started BEFORE waiting for exit, not after: the child's stdout/stderr
            // pipes have a finite OS buffer, and a worker that ever produced enough output to fill
            // one while nothing was draining it would deadlock against a parent that is only
            // blocked on WaitForExitAsync. Each read is itself capped at MaxWorkerOutputBytes
            // (never buffered without limit) — exceeding it cancels timeoutCts, reusing exactly
            // the same abort-and-kill path as an ordinary timeout below. Both readers share the
            // same MarkOutputCapExceeded callback, which is safe to call from either (or both,
            // concurrently) — see its own remarks.
            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MarkOutputCapExceeded);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, MarkOutputCapExceeded);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // One of three things cancelled timeoutCts: this method's own timeout, the
                // caller's own cancellationToken, or an output-cap breach. Whichever it was, the
                // worker must not be left running — kill it, and CONFIRM the exit rather than
                // just assuming the kill worked.
                var confirmedExit = await KillAndConfirmExitAsync(process);
                ObserveQuietly(stdoutTask);
                ObserveQuietly(stderrTask);

                if (Volatile.Read(ref outputCapExceeded) != 0)
                {
                    return WorkerFailed(
                        $"The validation worker produced more than {MaxWorkerOutputBytes:N0} bytes " +
                        "of output and was terminated.");
                }

                // The caller's own cancellation is rethrown as-is (ordinary MCP request
                // cancellation); only THIS method's own timeout is reported as a structured result.
                cancellationToken.ThrowIfCancellationRequested();

                var terminationClause = confirmedExit
                    ? "and the worker process was terminated"
                    : "and the worker process was asked to terminate but its exit could not be confirmed";
                return new ValidateSuiteResult(false, [new SuiteValidationError(
                    "validation-timeout",
                    null,
                    $"Validation did not complete within {effectiveTimeout.TotalSeconds:N0} seconds " +
                    $"{terminationClause}.",
                    null,
                    null)]);
            }

            if (process.ExitCode != 0)
            {
                ObserveQuietly(stdoutTask);
                var stderrExcerpt = await ReadExcerptQuietlyAsync(stderrTask);
                return WorkerFailed(
                    $"The validation worker exited with code {process.ExitCode}." +
                    (stderrExcerpt is null ? string.Empty : $" {stderrExcerpt}"));
            }

            string? stdout;
            try
            {
                stdout = await stdoutTask;
            }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: reading the
            // already-exited child's own redirected stream should not itself be able to throw in
            // practice, but this is a defensive boundary so any unexpected I/O failure here still
            // becomes a structured validation-worker-failed result rather than an unhandled
            // exception escaping the validate_suite tool handler.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return WorkerFailed($"Could not read the validation worker's output ({ex.GetType().Name}).");
            }

            // The result already has everything needed; stderr is only useful when something went
            // wrong, which the exit-code branch above already handled — observe it here so it is
            // never left as an unawaited task on the success path.
            ObserveQuietly(stderrTask);

            if (stdout is null)
            {
                // A rare race: the worker exceeded the output cap and exited 0 fast enough that
                // WaitForExitAsync above observed the exit before timeoutCts's cancellation from
                // MarkOutputCapExceeded was itself observed. Handled the same way regardless.
                return WorkerFailed(
                    $"The validation worker produced more than {MaxWorkerOutputBytes:N0} bytes of output.");
            }

            try
            {
                var result = JsonSerializer.Deserialize<ValidateSuiteResult>(stdout, ValidationWorkerProtocol.JsonOptions);
                if (result is null)
                {
                    return WorkerFailed("The validation worker produced no result.");
                }

                return result;
            }
            catch (JsonException)
            {
                return WorkerFailed("The validation worker's output could not be parsed as a result.");
            }
        }
    }

    private static ValidateSuiteResult WorkerFailed(string message) =>
        new(false, [new SuiteValidationError("validation-worker-failed", null, message, null, null)]);

    /// <summary>
    /// Resolves the executable and arguments needed to re-invoke THIS SAME vouchfx-mcp build in
    /// <see cref="ValidationWorkerProtocol.WorkerModeArgument"/> mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Environment.ProcessPath"/> is the executable that launched the CURRENT process:
    /// under the packaged/self-contained <c>Vouchfx.Mcp</c> apphost, that IS this server's own
    /// entry point, and it is re-invoked directly. Under <c>dotnet Vouchfx.Mcp.dll</c> (the common
    /// case for a source checkout, and how this repo's own tests start the real server process),
    /// <see cref="Environment.ProcessPath"/> instead names the <c>dotnet</c> muxer itself — not
    /// this assembly — so that case (detected simply as "the process path's file name does not
    /// match this assembly's own name") falls back to launching this assembly explicitly via the
    /// muxer.
    /// </para>
    /// <para>
    /// That same fallback also fires correctly when THIS method is called from within a unit test
    /// host (e.g. a test driving <c>validate_suite</c> through the in-memory MCP harness):
    /// <see cref="Environment.ProcessPath"/> there names the test host, not
    /// <c>Vouchfx.Mcp</c>, so the fallback path is taken — and it launches THIS ASSEMBLY (resolved
    /// via <see cref="System.Reflection.Assembly.Location"/>, which is always Vouchfx.Mcp.dll's own
    /// build output path, independent of whatever process happens to be executing this code), not
    /// whatever assembly the calling test process happens to be. A worker spawned from a test this
    /// way is therefore just as real a child process as one spawned from the production server.
    /// </para>
    /// </remarks>
    private static (string FileName, IReadOnlyList<string> Arguments) ResolveWorkerLaunch(string path)
    {
        var assembly = typeof(ValidationWorkerClient).Assembly;
        var expectedApphostName = assembly.GetName().Name;
        var processPath = Environment.ProcessPath;

        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), expectedApphostName, StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, [ValidationWorkerProtocol.WorkerModeArgument, path]);
        }

        return ("dotnet", [assembly.Location, ValidationWorkerProtocol.WorkerModeArgument, path]);
    }

    /// <summary>
    /// Kills <paramref name="process"/>'s entire process tree, then waits up to
    /// <see cref="KillConfirmationTimeout"/> for the exit to actually be observed, returning
    /// whether it was confirmed — so the caller's message never claims termination it did not
    /// verify.
    /// </summary>
    private static async Task<bool> KillAndConfirmExitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate, best-effort:
        // the process may already have exited between the check above and the kill call, or the
        // OS may refuse to kill a process that is already gone. Either way, fall through to the
        // same bounded confirmation wait below rather than assuming success or failure.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            using var confirmCts = new CancellationTokenSource(KillConfirmationTimeout);
            await process.WaitForExitAsync(confirmCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attaches a continuation that observes (and discards) any fault on <paramref name="task"/>
    /// without awaiting it, so an exception from a background read abandoned after a kill never
    /// surfaces as an unobserved task exception.
    /// </summary>
    private static void ObserveQuietly(Task task)
    {
        _ = task.ContinueWith(
            static t => t.Exception?.Handle(_ => true),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to the end as UTF-8 text, unless doing so would exceed
    /// <see cref="MaxWorkerOutputBytes"/> — in which case <paramref name="onExceeded"/> is invoked
    /// and this returns <see langword="null"/> without reading further.
    /// </summary>
    /// <remarks>
    /// This method calls <paramref name="onExceeded"/> at most once from ITS OWN read loop (it
    /// returns immediately after), but callers that share one callback across two concurrently
    /// running instances (as <c>ValidateAsyncCore</c> does, one per stream) must make that shared
    /// callback itself safe to invoke from either or both — see <c>MarkOutputCapExceeded</c>'s own
    /// remarks for how that is guaranteed.
    /// <para>
    /// Reads with <see cref="CancellationToken.None"/> deliberately: this keeps running in the
    /// background even after the caller has moved on (a kill, a cap breach elsewhere, or the
    /// caller's own cancellation) — <see cref="ObserveQuietly"/> is how a caller that no longer
    /// needs the result stops caring about it without forcibly aborting the read itself.
    /// </para>
    /// </remarks>
    private static async Task<string?> ReadBoundedAsync(Stream stream, Action onExceeded)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, CancellationToken.None)) > 0)
        {
            if (buffer.Length + bytesRead > MaxWorkerOutputBytes)
            {
                onExceeded();
                return null;
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Best-effort, sanitised, length-capped excerpt of the worker's stderr for a
    /// <c>validation-worker-failed</c> message — helpful for diagnosing a genuine crash without
    /// ever surfacing raw, unsanitised process output (M1).
    /// </summary>
    private static async Task<string?> ReadExcerptQuietlyAsync(Task<string?> stderrTask)
    {
        try
        {
            var stderr = await stderrTask;
            if (stderr is null)
            {
                return $"Worker stderr exceeded the {MaxWorkerOutputBytes:N0}-byte output cap and was truncated.";
            }

            if (string.IsNullOrWhiteSpace(stderr))
            {
                return null;
            }

            const int maxLength = 500;
            var trimmed = stderr.Trim();
            var excerpt = trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
            return $"Worker stderr: {TextSanitiser.SanitiseForDisplay(excerpt)}";
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: this is purely
        // a best-effort diagnostic addendum to an already-failing result; any problem reading it
        // must never itself become the reported failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
