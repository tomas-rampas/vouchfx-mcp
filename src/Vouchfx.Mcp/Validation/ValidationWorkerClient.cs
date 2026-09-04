using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Normalization;

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
/// file's actual content reaches the child. An INLINE source (US-S2-02) has no path to check, so its
/// analogue is <see cref="YamlSafetyGuard.CheckSize"/> alone — counting bytes likewise hands nothing
/// to YamlDotNet. Neither is an exemption: the worker re-runs every one of these checks on whatever
/// does reach it.
/// </para>
/// <para>
/// <b>The child is this SAME executable</b>, re-invoked with
/// <see cref="ValidationWorkerProtocol.WorkerModeArgument"/> (see <c>Program.cs</c>'s worker-mode
/// branch) — never the vouchfx CLI, and never a container. See <see cref="ResolveWorkerLaunch"/>
/// for how its path is found.
/// </para>
/// <para>
/// <b>Boundary hardening beyond the timeout/kill itself:</b> the child's stdin is always redirected
/// — this server's own real stdin (the MCP protocol's read side, in production) must never be
/// inherited into a disposable child — and is then closed, either immediately unused (a file
/// source, which never reads it) or after the inline suite text has been written to it (US-S2-02;
/// see <see cref="ValidationWorkerProtocol.InlineYamlArgument"/> for why that transport was chosen
/// over a temp file, and <see cref="WriteStandardInputAsync"/> for the deadlock-free ordering); the child's
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
    /// The worker's only legitimate output is one serialised <see cref="SuiteAnalysis"/> — at most
    /// one entry per genuine schema/YAML problem in a suite already capped at
    /// <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/> (5&#160;MB) input, plus modest JSON/pointer
    /// overhead per entry — or, since US-S2-04, that analysis wrapped in a
    /// <see cref="Vouchfx.Mcp.Normalization.SuiteNormalization"/> whose <c>normalizedYaml</c> is a
    /// JSON-escaped copy of the whole suite. <b>That copy is what sets the real margin, and the
    /// escaping is not free:</b> <see cref="ValidationWorkerProtocol.JsonOptions"/> deliberately
    /// escapes every non-ASCII character as <c>\uXXXX</c> (see its remarks — that is the defect-#70
    /// mitigation), so the expansion is measured at 1.0x for ASCII, 2.0x for CJK (a 3-byte UTF-8
    /// character becomes 6 ASCII bytes) and 3.0x for non-BMP text such as emoji (4 bytes becoming 12,
    /// as a surrogate pair). A worst-case 5&#160;MB all-non-BMP suite therefore returns about
    /// 15&#160;MB, leaving 50&#160;MB a ~3x margin rather than the ~10x it was before this tool
    /// existed. Still large enough that no legitimate result is ever affected, still small enough
    /// that this server never buffers an unbounded amount of a misbehaving or compromised child's
    /// output in its own memory — but the headroom is now spoken for, so raising
    /// <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/> means revisiting this number. This is a
    /// defence at THIS boundary, independent of (not a substitute for) the upstream input cap: the
    /// two bound different things.
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
    /// <para>
    /// Since US-S2-02 this is <see cref="AnalyseAsync"/> narrowed to a file source, the schema pass,
    /// and the v1 result shape — the exact contract <c>run_suite</c>'s EDGE-003 pre-flight has
    /// always called, kept so that story's addition changed nothing for it.
    /// </para>
    /// </remarks>
    public static async Task<ValidateSuiteResult> ValidateAsync(
        string path, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var normalisation = await RunWorkerAsync(
            SuiteSource.FromPath(path), ValidationLevel.Schema, normalise: false, timeout,
            onWorkerStarted: null, cancellationToken)
            .ConfigureAwait(false);

        return normalisation.Validation.AsValidationResult();
    }

    /// <summary>
    /// Runs the passes <paramref name="level"/> selects over <paramref name="source"/> — a suite
    /// file or inline YAML text — inside the same isolated, killable child process, and returns the
    /// full analysis (US-S2-02).
    /// </summary>
    /// <param name="source">
    /// The suite to analyse. An inline source gets NO exemption from anything this class does: same
    /// worker process, same wall clock, same whole-tree kill, same output cap. Only the transport
    /// differs — see <see cref="ValidationWorkerProtocol.InlineYamlArgument"/> for why it is stdin.
    /// </param>
    /// <param name="level">Which passes to run; see <see cref="ValidationLevel"/>.</param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>; see <see cref="ValidateAsync"/>.</param>
    /// <param name="cancellationToken">See <see cref="ValidateAsync"/>.</param>
    /// <remarks>
    /// Never throws for a validation failure — identical contract to <see cref="ValidateAsync"/>,
    /// which is now simply this method narrowed to a file source and the schema pass.
    /// </remarks>
    public static async Task<SuiteAnalysis> AnalyseAsync(
        SuiteSource source,
        ValidationLevel level,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var normalisation = await RunWorkerAsync(
            source, level, normalise: false, timeout, onWorkerStarted: null, cancellationToken)
            .ConfigureAwait(false);

        return normalisation.Validation;
    }

    /// <summary>
    /// <see cref="AnalyseAsync"/> plus the suite's canonical text — <c>normalize_suite</c>'s crossing
    /// of this same boundary (US-S2-04).
    /// </summary>
    /// <param name="source">The suite to analyse and canonicalise.</param>
    /// <param name="level">Which passes to run.</param>
    /// <param name="normalise">
    /// Whether to ask the worker for canonical text at all. <see langword="false"/> makes this
    /// method exactly <see cref="AnalyseAsync"/> with the result rewrapped — including on the wire,
    /// where the worker then emits the unchanged <see cref="SuiteAnalysis"/> shape (see
    /// <see cref="ValidationWorkerProtocol.NormaliseArgument"/>). It is a parameter rather than
    /// always-on because normalization DROPS COMMENTS and is therefore opt-in at the tool boundary;
    /// there is no reason to pay for text the caller has said it does not want.
    /// </param>
    /// <param name="timeout">Overrides <see cref="DefaultTimeout"/>; see <see cref="ValidateAsync"/>.</param>
    /// <param name="cancellationToken">See <see cref="ValidateAsync"/>.</param>
    /// <remarks>
    /// Never throws for a validation failure — identical contract to <see cref="AnalyseAsync"/>. The
    /// canonical text is rendered INSIDE the worker for the same reason the verdict is: it comes off a
    /// parse of untrusted YAML, which must never happen in this long-lived process (see this type's
    /// own remarks for the uninterruptible-Scanner-spin threat that boundary exists for).
    /// </remarks>
    public static Task<SuiteNormalization> NormaliseAsync(
        SuiteSource source,
        ValidationLevel level,
        bool normalise,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        RunWorkerAsync(source, level, normalise, timeout, onWorkerStarted: null, cancellationToken);

    /// <summary>
    /// Test-only variant of <see cref="AnalyseAsync"/> that additionally invokes
    /// <paramref name="onWorkerStarted"/> with the worker's OS process ID the instant it starts.
    /// </summary>
    /// <remarks>
    /// Exists so a test can assert DIRECTLY that a killed worker process has actually exited
    /// (<c>Process.GetProcessById</c> throwing, or <see cref="Process.HasExited"/>) rather than
    /// only inferring "no orphan" from elapsed timing. Deliberately <see langword="internal"/>, not
    /// an extra public parameter on <see cref="AnalyseAsync"/>: visible to the test assembly only,
    /// via this assembly's <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal static async Task<SuiteAnalysis> AnalyseAsyncForTesting(
        SuiteSource source,
        ValidationLevel level,
        TimeSpan? timeout,
        Action<int> onWorkerStarted,
        CancellationToken cancellationToken)
    {
        var normalisation = await RunWorkerAsync(
            source, level, normalise: false, timeout, onWorkerStarted, cancellationToken)
            .ConfigureAwait(false);

        return normalisation.Validation;
    }

    /// <summary>
    /// The single hardened path every entry point above goes through: spawn, bound, read, kill,
    /// deserialise.
    /// </summary>
    /// <remarks>
    /// <b>One core, two response shapes, and deliberately not two cores.</b> Everything this method
    /// does — the fast rejects, the stdin ordering, the two bounded readers, the wall clock, the
    /// whole-tree kill and its confirmation, the output cap — is the accumulated result of four
    /// rounds of adversarial hardening. A second copy of it for <c>normalize_suite</c> would be a
    /// second copy that can drift out of that hardening. <paramref name="normalise"/> therefore
    /// changes exactly two things: one extra argument on the child's command line, and which type the
    /// child's stdout is deserialised as.
    /// </remarks>
    private static async Task<SuiteNormalization> RunWorkerAsync(
        SuiteSource source,
        ValidationLevel level,
        bool normalise,
        TimeSpan? timeout,
        Action<int>? onWorkerStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        // The fast, bounded, in-process pre-checks: a missing file or a UNC/network path (M2)
        // never reaches this far — neither ever hands untrusted YAML text to YamlDotNet, so
        // neither can hang, and spawning a worker for either would only add latency for no safety
        // benefit. The inline source's analogue is the size cap: counting bytes likewise never
        // hands text to YamlDotNet, so it needs no worker either, and refusing to stream megabytes
        // into a child that would only reject them is the same short-circuit. NOT an exemption —
        // the worker still runs the identical check (YamlSafetyGuard.Check, inside
        // SuiteValidator.AnalyseYaml) on everything that does reach it.
        var fastRejectError = source.IsInline
            ? YamlSafetyGuard.CheckSize(source.InlineYaml!)
            : SuiteValidator.CheckFastRejects(source.Path!);
        if (fastRejectError is not null)
        {
            return SuiteNormalization.WithoutCanonicalYaml(
                SuiteAnalysis.FromValidation(new ValidateSuiteResult(false, [fastRejectError]), level));
        }

        var (fileName, arguments) = ResolveWorkerLaunch(source, level, normalise);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // UTF-8 explicitly, never the ambient console encoding. Left unset, .NET writes a
            // redirected stdin in Console.InputEncoding — the OEM code page on Windows (cp852,
            // cp1252, …), under which every non-ASCII character in an inline suite is best-fit
            // mapped or replaced with '?' before the worker ever sees it. A suite would then be
            // validated as text the caller did not send. MEASURED by removing this one line and
            // re-running AnalyseAsync_InlineYamlWithNonAsciiContent_CrossesTheBoundaryByteFaithfully
            // on this repo's Windows host: 'café' arrived as 'caf?' and '注文' as '??'.
            // This is the INPUT-side twin of the
            // tracked output-side defect in BoundedStreamReader's remarks (issue #70); the input
            // side is fixable here alone because both ends of this pipe are ours, so it is fixed
            // rather than tracked. The worker's matching read is in Program.cs's ReadInlineYaml.
            // No BOM: a byte-order mark is not part of the suite text.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
            return WorkerFailed($"Could not start the validation worker process ({ex.GetType().Name}).", level);
        }

        using (process)
        {
            onWorkerStarted?.Invoke(process.Id);

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
            var stdoutTask = BoundedStreamReader.ReadUpToAsync(process.StandardOutput.BaseStream, MaxWorkerOutputBytes, MarkOutputCapExceeded);
            var stderrTask = BoundedStreamReader.ReadUpToAsync(process.StandardError.BaseStream, MaxWorkerOutputBytes, MarkOutputCapExceeded);

            // stdin is handled AFTER the two readers are running and BEFORE the exit wait, and both
            // halves of that ordering are load-bearing (US-S2-02):
            //
            //   * After the readers, because an inline suite can be up to
            //     YamlSafetyGuard.MaxSuiteSizeBytes and the OS pipe buffer is a few dozen KB. A
            //     parent blocked writing stdin while the child is blocked writing stdout — with
            //     nothing draining either — is a classic two-pipe deadlock. With the readers already
            //     draining, only one side can ever block, and it is bounded by timeoutCts.
            //   * Before the exit wait, because the worker reads stdin to EOF before it does
            //     anything at all; that EOF only arrives when this handle closes.
            //
            // Honest about what bounds this write, since "bounded by timeoutCts" above is only half
            // true: cancelling a write that is ALREADY BLOCKED inside the OS is platform-dependent.
            // On Windows the token can abort an in-flight pipe write (CancelIoEx); on Unix a
            // FileStream write is typically only cancellable BETWEEN operations, so a token
            // cancelled mid-write may not be observed until the current syscall returns. What makes
            // the timeout reachable here regardless is a WORKER property, not cancellation
            // plumbing: the worker drains stdin to EOF before it does any work (see Program.cs's
            // RunValidateWorker), so it is always the reader on the other end and this write cannot
            // stay blocked indefinitely against a live child. If that worker behaviour ever changes
            // — a worker that starts validating before draining stdin — this ordering comment stops
            // being sufficient and the write needs its own hard bound.
            //
            // For a PATH source there is nothing to write — the handle is redirected and closed
            // unused, exactly as it always was, for its own separate reason: it stops this server's
            // OWN stdin (the MCP protocol's read side, in the real server) from being inherited into
            // a disposable child, which is what would happen without RedirectStandardInput.
            await WriteStandardInputAsync(process, source, timeoutCts.Token).ConfigureAwait(false);

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
                BoundedStreamReader.ObserveQuietly(stdoutTask);
                BoundedStreamReader.ObserveQuietly(stderrTask);

                if (Volatile.Read(ref outputCapExceeded) != 0)
                {
                    return WorkerFailed(
                        $"The validation worker produced more than {MaxWorkerOutputBytes:N0} bytes " +
                        "of output and was terminated.",
                        level);
                }

                // The caller's own cancellation is rethrown as-is (ordinary MCP request
                // cancellation); only THIS method's own timeout is reported as a structured result.
                cancellationToken.ThrowIfCancellationRequested();

                var terminationClause = confirmedExit
                    ? "and the worker process was terminated"
                    : "and the worker process was asked to terminate but its exit could not be confirmed";
                return SuiteNormalization.WithoutCanonicalYaml(SuiteAnalysis.FromValidation(
                    new ValidateSuiteResult(false, [new SuiteValidationError(
                        VfxCodeCatalogue.ValidationTimeout,
                        null,
                        $"Validation did not complete within {effectiveTimeout.TotalSeconds:N0} seconds " +
                        $"{terminationClause}.",
                        null,
                        null)]),
                    level));
            }

            if (process.ExitCode != 0)
            {
                BoundedStreamReader.ObserveQuietly(stdoutTask);
                var stderrExcerpt = await ReadExcerptQuietlyAsync(stderrTask);
                return WorkerFailed(
                    $"The validation worker exited with code {process.ExitCode}." +
                    (stderrExcerpt is null ? string.Empty : $" {stderrExcerpt}"),
                    level);
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
                return WorkerFailed($"Could not read the validation worker's output ({ex.GetType().Name}).", level);
            }

            // The result already has everything needed; stderr is only useful when something went
            // wrong, which the exit-code branch above already handled — observe it here so it is
            // never left as an unawaited task on the success path.
            BoundedStreamReader.ObserveQuietly(stderrTask);

            if (stdout is null)
            {
                // A rare race: the worker exceeded the output cap and exited 0 fast enough that
                // WaitForExitAsync above observed the exit before timeoutCts's cancellation from
                // MarkOutputCapExceeded was itself observed. Handled the same way regardless.
                return WorkerFailed(
                    $"The validation worker produced more than {MaxWorkerOutputBytes:N0} bytes of output.",
                    level);
            }

            try
            {
                // The shape the worker wrote is decided by the SAME flag that put --normalize on its
                // command line (see ValidationWorkerProtocol.NormaliseArgument), so the two sides
                // cannot disagree about it without disagreeing about the argument list too.
                var result = normalise
                    ? JsonSerializer.Deserialize<SuiteNormalization>(stdout, ValidationWorkerProtocol.JsonOptions)
                    : Wrap(JsonSerializer.Deserialize<SuiteAnalysis>(stdout, ValidationWorkerProtocol.JsonOptions));

                if (result is null || result.Validation is null)
                {
                    return WorkerFailed("The validation worker produced no result.", level);
                }

                return result;
            }
            catch (JsonException)
            {
                return WorkerFailed("The validation worker's output could not be parsed as a result.", level);
            }
            catch (ArgumentException)
            {
                // A Diagnostic in the semanticDiagnostics array validates its own code and severity
                // in its constructor (see Contracts/Diagnostic), and that constructor is what
                // System.Text.Json calls on deserialisation — so a worker that somehow emitted a
                // malformed finding throws here rather than returning a bad object. Treated exactly
                // like unparseable output: this is untrusted text from another process (the same
                // reasoning ValidationOutcomeRenderer.IsDiagnostic records), and validate_suite's
                // "never throws" promise must not depend on the child's honesty.
                return WorkerFailed("The validation worker's output could not be parsed as a result.", level);
            }
        }
    }

    /// <summary>
    /// Writes an inline source's suite text to the worker's stdin, then closes the handle — the
    /// EOF the worker is waiting on. A file source writes nothing and simply closes.
    /// </summary>
    /// <remarks>
    /// <b>Never throws</b>, deliberately: every way this can fail is already a condition the caller
    /// handles better one step later. A cancelled write means the timeout fired (or the caller
    /// cancelled), and the <c>WaitForExitAsync</c> immediately below observes the same token and
    /// runs the kill-and-report path; a broken pipe means the worker already exited, and its exit
    /// code and stderr are the honest diagnosis, not "the write failed". Rethrowing either would
    /// bypass the kill path and leave a worker running.
    /// </remarks>
    private static async Task WriteStandardInputAsync(
        Process process, SuiteSource source, CancellationToken cancellationToken)
    {
        try
        {
            if (source.IsInline)
            {
                await process.StandardInput.WriteAsync(source.InlineYaml.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate; see the remarks
        // above. OperationCanceledException (the timeout), IOException (a broken pipe when the
        // worker has already exited), and ObjectDisposedException (a racing close) all mean "the
        // worker's own outcome is the answer", and anything else here must not be allowed to escape
        // ahead of the kill path either.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            // Best-effort, and unconditional: the inline worker blocks reading stdin until EOF, so
            // failing to close this handle would turn a write failure into a hang — the one outcome
            // this whole class exists to make impossible.
            try
            {
                process.StandardInput.Close();
            }
#pragma warning disable CA1031 // Do not catch general exception types — closing an already-broken
            // or already-disposed handle must not become the reported failure.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private static SuiteNormalization WorkerFailed(string message, ValidationLevel level) =>
        SuiteNormalization.WithoutCanonicalYaml(SuiteAnalysis.FromValidation(
            new ValidateSuiteResult(
                false, [new SuiteValidationError(VfxCodeCatalogue.ValidationWorkerFailed, null, message, null, null)]),
            level));

    /// <summary>
    /// Rewraps a bare <see cref="SuiteAnalysis"/> — the response shape a worker invoked WITHOUT
    /// <see cref="ValidationWorkerProtocol.NormaliseArgument"/> writes — as the envelope this class's
    /// single core returns, propagating a <see langword="null"/> deserialisation so the caller's own
    /// "produced no result" check sees it.
    /// </summary>
    private static SuiteNormalization? Wrap(SuiteAnalysis? analysis) =>
        analysis is null ? null : SuiteNormalization.WithoutCanonicalYaml(analysis);

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
    private static (string FileName, IReadOnlyList<string> Arguments) ResolveWorkerLaunch(
        SuiteSource source, ValidationLevel level, bool normalise)
    {
        // An inline source names no file: the positional argument becomes the --yaml-stdin marker
        // and the suite text follows on stdin (see ValidationWorkerProtocol.InlineYamlArgument for
        // the transport decision and its rationale). Caller-supplied YAML therefore never reaches a
        // command line at all — no length limit, no quoting, no shell to mis-parse it, and nothing
        // for a process listing to expose.
        var sourceArgument = source.IsInline
            ? ValidationWorkerProtocol.InlineYamlArgument
            : source.Path!;

        // Appended last and only when asked for, so a non-normalising launch produces the exact same
        // argument list it always has — see ValidationWorkerProtocol.NormaliseArgument.
        IReadOnlyList<string> WorkerArguments(string? assemblyLocation)
        {
            var arguments = new List<string>(4);
            if (assemblyLocation is not null)
            {
                arguments.Add(assemblyLocation);
            }

            arguments.Add(ValidationWorkerProtocol.WorkerModeArgument);
            arguments.Add(sourceArgument);
            arguments.Add(ValidationWorkerProtocol.LevelArgumentFor(level));

            if (normalise)
            {
                arguments.Add(ValidationWorkerProtocol.NormaliseArgument);
            }

            return arguments;
        }

        var assembly = typeof(ValidationWorkerClient).Assembly;
        var expectedApphostName = assembly.GetName().Name;
        var processPath = Environment.ProcessPath;

        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), expectedApphostName, StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, WorkerArguments(assemblyLocation: null));
        }

        return ("dotnet", WorkerArguments(assembly.Location));
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
