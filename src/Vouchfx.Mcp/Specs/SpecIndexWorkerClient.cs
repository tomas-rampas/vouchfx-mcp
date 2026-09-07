using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Specs;

/// <summary>
/// <c>vouchfx://workspace/specs</c>' process-isolation boundary: parses every enumerated suite
/// inside disposable child processes instead of this server's own long-lived one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all</b> — the uninterruptible YamlDotNet Scanner spin, and why
/// <c>YamlSafetyGuard</c> is not a defence against it because the guard itself runs the Scanner — is
/// recorded once, in <see cref="SpecIndexWorkerProtocol"/>'s header, together with the streaming and
/// stdin-transport decisions. Read that first.
/// </para>
/// <para>
/// <b>This is the SECOND process boundary in this server, and it is deliberately not a copy of the
/// first.</b> <see cref="ValidationWorkerClient"/> answers "one suite, whole verdict or nothing" and
/// its hardening is shaped around that: one JSON document, <see cref="BoundedStreamReader.ReadUpToAsync"/>,
/// a timeout that turns the whole call into a structured <c>validation-timeout</c>. This one answers
/// "many suites, keep what arrived" and needs the opposite output discipline. What the two DO share
/// is reused rather than re-implemented: the launch resolution shape, the stdin redirect-and-close
/// rule, the bounded readers, the whole-tree kill with a confirmed exit,
/// <see cref="ValidationWorkerProtocol.JsonOptions"/> for the wire, and — since a review found this
/// class collapsing them — that class's own separation of a WORKER FAILURE from a TIMEOUT.
/// </para>
/// <para>
/// <b>THE BUDGET IS TWO ALLOWANCES, NOT ONE WALL CLOCK, and that distinction is a bug fix rather
/// than a refinement.</b> The first version gave each spawn a single 10-second wall clock covering
/// process start, JIT, scheduling and every file's parse together. Under the full test suite's
/// parallelism — validation workers, real spawned-server tests and this class's own spin tests all
/// competing — a spawn could lose enough of its slice that a batch of PERFECTLY HEALTHY suites blew
/// the clock, and every one of them was then published as <c>readable: false</c> with text accusing
/// it of driving the parser into a spin. Two product defects (a false accusation, and a lost index)
/// from one mis-attributed measurement. So:
/// <list type="bullet">
/// <item><description>
/// <see cref="SpecIndexWorkerBudget.Startup"/> covers spawn to FIRST OUTPUT. It is generous, because
/// what it measures is this machine's load, not the suite's content.
/// </description></item>
/// <item><description>
/// <see cref="SpecIndexWorkerBudget.Stall"/> covers the gap BETWEEN outputs, reset on every line.
/// It is the only clock that can accuse a file, because it is the only one that measures a file.
/// </description></item>
/// </list>
/// A worker that never speaks is an ENVIRONMENTAL failure
/// (<see cref="WorkerAttemptOutcome.Unavailable"/>) and is reported as such — never as N innocent
/// suites that "could not be parsed".
/// </para>
/// </remarks>
public static class SpecIndexWorkerClient
{
    /// <summary>
    /// The production budget. <b>Total</b> bounds one whole index build; <b>Startup</b> and
    /// <b>Stall</b> bound one spawn, separately, for the reason this type's remarks give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Startup at 30 s</b>: it covers <see cref="Process.Start(ProcessStartInfo)"/>, the child's
    /// runtime start-up and JIT, however long this machine takes to schedule it, <b>and the stdin
    /// write that hands it the path list</b>. On an unloaded developer machine that is well under a
    /// second; the number is sized for a CI runner executing this repository's own suite in parallel,
    /// where it was MEASURED to matter.
    /// <para>
    /// The write was NOT covered until a peer review found it: <see cref="WatchAsync"/> starts its
    /// clock only after the write returns, and the write itself was bounded by nothing. A child that
    /// never reads its stdin blocked the parent on the OS pipe buffer — measured at 8 KB, against a
    /// 500-path payload of roughly 35 KB. It is now raced against this same allowance (clamped to what
    /// remains of <see cref="SpecIndexWorkerBudget.Total"/>), so this paragraph describes the window it
    /// always claimed to.
    /// </para>
    /// </para>
    /// <para>
    /// <b>Stall at 10 s</b>: the gap between one suite's result line and the next. A real suite parses
    /// in low single-digit milliseconds — no schema pass runs here, only YAML→JSON and the summary
    /// walk — so ten seconds of silence is three orders of magnitude past normal and is the signal
    /// this clock exists to catch.
    /// </para>
    /// <para>
    /// <b>Total at 90 s</b>, and stated honestly: it bounds the SLICES this class hands out, not the
    /// wall clock end to end. Two per-attempt costs sit OUTSIDE the deadline arithmetic, and both are
    /// counted here:
    /// <list type="bullet">
    /// <item><description><see cref="KillConfirmationTimeout"/> — up to 2 s per attempt confirming the
    /// process tree actually died.</description></item>
    /// <item><description><see cref="DrainCompletionTimeout"/> — up to 5 s per attempt letting the
    /// stdout drain finish, which is what stops a fast-exiting worker's output being missed
    /// entirely.</description></item>
    /// </list>
    /// So the true worst case is about <b>90 s + 3 × (2 s + 5 s) = 111 s</b>, not 90.
    /// <para>
    /// <b>The stdin write is INSIDE the 90 s</b>, not an addend: its allowance is
    /// <see cref="SpecIndexWorkerBudget.Startup"/> clamped to what remains of Total, so it can consume
    /// budget but never extend it. A write that expires does trigger a kill, and that kill's
    /// confirmation is the same 2 s already counted above — an attempt cannot both time out its write
    /// and separately time out its watchdog, because the first returns from the attempt.
    /// </para>
    /// <b>This paragraph exists because THREE earlier versions of it were false</b> — the first
    /// claimed the design "cannot take three timeouts' worth of time"; the second added the kill
    /// addend and omitted the drain; the third added the drain while an entirely unbounded stdin write
    /// sat outside all of it. Anything added to a per-attempt path that can block belongs in this sum,
    /// or the sum is wrong again.
    /// </para>
    /// </remarks>
    public static readonly SpecIndexWorkerBudget DefaultBudget = new(
        Startup: TimeSpan.FromSeconds(30),
        Stall: TimeSpan.FromSeconds(10),
        Total: TimeSpan.FromSeconds(90));

    /// <summary>
    /// How many worker processes one index build will start. Three means a build survives TWO
    /// spinning suites and still reports every other file correctly.
    /// </summary>
    /// <remarks>
    /// Bounded rather than "resume until done" because each resume is a process start, and a
    /// workspace whose every file is hostile would otherwise turn one resource read into
    /// <see cref="WorkspaceSpecIndexer.MaxSpecsIndexed"/> process starts. The Total budget bounds the
    /// TIME; this bounds the process CHURN, which the time bound alone does not.
    /// </remarks>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Maximum bytes read from either of a worker's stdout or stderr before it is treated as
    /// misbehaving and killed — the same defence, at the same boundary, as
    /// <see cref="ValidationWorkerClient.MaxWorkerOutputBytes"/>.
    /// </summary>
    /// <remarks>
    /// Far smaller than that constant's 50 MB, because this worker's legitimate output is far smaller
    /// and precisely bounded: at most <see cref="WorkspaceSpecIndexer.MaxSpecsIndexed"/> lines, each
    /// capped by <see cref="SpecIndexParser"/>'s per-entry field caps. 16 MB leaves roughly two orders
    /// of magnitude of headroom over the realistic worst case while still bounding what a misbehaving
    /// child can make this server buffer.
    /// </remarks>
    public const long MaxWorkerOutputBytes = 16L * 1024 * 1024;

    /// <summary>How often the watchdog re-checks the startup and stall clocks.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>See <c>ValidationWorkerClient.KillConfirmationTimeout</c> — same role, same reasoning.</summary>
    private static readonly TimeSpan KillConfirmationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Parses each of <paramref name="paths"/> in a child process, returning one entry per input
    /// path, in input order, with no gaps.
    /// </summary>
    /// <param name="paths">
    /// Absolute suite paths, already enumerated and containment-checked BY THE CALLER. This method
    /// performs no path safety of its own and must never be handed a path that has not been through
    /// <see cref="PathSafetyGuard"/> — see <see cref="SpecIndexWorkerProtocol"/>'s header for why that
    /// split is where it is.
    /// </param>
    /// <param name="budget">
    /// Overrides <see cref="DefaultBudget"/>. <b>Reachable from the resource's own entry point</b>
    /// (<see cref="WorkspaceSpecIndexer.BuildAsync"/> forwards it) rather than being an inert
    /// parameter — a review found the first version's equivalent seam unreachable, which is what left
    /// the healthy-path tests running against the production clock and therefore against whatever
    /// else the machine was doing.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelling this kills the current worker's process tree and rethrows as an ordinary
    /// <see cref="OperationCanceledException"/> — distinct from this method's own budget expiring,
    /// which produces degraded entries rather than an exception.
    /// </param>
    public static async Task<SpecIndexParseOutcome> ParseAsync(
        IReadOnlyList<string> paths,
        SpecIndexWorkerBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return new SpecIndexParseOutcome([], WorkerUnavailable: false, UnavailableDetail: null);
        }

        var effective = budget ?? DefaultBudget;
        var results = new SpecIndexWorkerEntry?[paths.Count];
        var deadline = DateTimeOffset.UtcNow + effective.Total;
        var nextIndex = 0;
        string? unavailableDetail = null;

        for (var attempt = 0; attempt < MaxAttempts && nextIndex < paths.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            var startIndex = nextIndex;
            var result = await RunOneWorkerAsync(
                paths, startIndex, results, effective, deadline, cancellationToken).ConfigureAwait(false);

            if (result.Outcome == WorkerAttemptOutcome.Unavailable)
            {
                // The worker never produced a single line — it failed to start, died before speaking,
                // or its startup allowance expired. NOTHING about any suite has been learned, so no
                // suite may be blamed. Recorded once, reported as the index's own reason, and the
                // attempt loop stops: a second spawn on a machine that could not run the first is
                // very unlikely to differ and would only add latency to an already-failing read.
                unavailableDetail = result.Detail;
                break;
            }

            nextIndex = result.ReportedThrough;

            if (nextIndex >= paths.Count)
            {
                break;
            }

            if (result.Outcome == WorkerAttemptOutcome.StalledAfterOutput)
            {
                // The worker reported N lines and THEN went quiet, so file N is the one it was on.
                // This is the only path that may describe a file as unparseable, and it is the only
                // path with evidence for it: the worker demonstrably worked, then stopped on this
                // input. Charged explicitly, and the next attempt resumes AFTER it.
                results[nextIndex] ??= Degraded(nextIndex, StalledEntryDetail);
                nextIndex++;
            }
            else if (result.Outcome == WorkerAttemptOutcome.Completed)
            {
                // Exited without reporting everything and without stalling — a crash, or output this
                // parent could not parse. Neither is evidence about the file, so the wording does not
                // pretend otherwise; the next attempt still resumes past it so one bad file cannot
                // loop.
                results[nextIndex] ??= Degraded(nextIndex, IncompleteEntryDetail);
                nextIndex++;
            }
        }

        // Anything still unreported never got a worker at all — the budget or the attempt count ran
        // out before reaching it, or the worker was unavailable. Reported as unparsed WITH A REASON
        // THAT DOES NOT BLAME THE FILE: a suite missing from the index is indistinguishable from one
        // that does not exist, and a suite falsely described as unparseable is worse than either.
        var neverReachedDetail = unavailableDetail is null ? NotReachedEntryDetail : WorkerUnavailableEntryDetail;
        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= Degraded(i, neverReachedDetail);
        }

        return new SpecIndexParseOutcome(
            [.. results.Select(entry => Sanitise(entry!))],
            WorkerUnavailable: unavailableDetail is not null,
            UnavailableDetail: unavailableDetail);
    }

    /// <summary>What one worker attempt established.</summary>
    internal enum WorkerAttemptOutcome
    {
        /// <summary>The process exited. Whatever it reported has been applied.</summary>
        Completed,

        /// <summary>It reported at least one line, then produced nothing for a whole stall allowance.</summary>
        StalledAfterOutput,

        /// <summary>
        /// It never produced a single line — failed to start, died silently, or exceeded its startup
        /// allowance. <b>Says nothing about any suite.</b>
        /// </summary>
        Unavailable,
    }

    private sealed record WorkerAttemptResult(WorkerAttemptOutcome Outcome, int ReportedThrough, string? Detail);

    /// <summary>
    /// Runs ONE worker over <c>paths[startIndex..]</c>, writing every entry it reports into
    /// <paramref name="results"/>.
    /// </summary>
    /// <remarks>
    /// Never throws for a worker failure. It CAN throw <see cref="OperationCanceledException"/>, but
    /// only for the CALLER's own token.
    /// </remarks>
    private static async Task<WorkerAttemptResult> RunOneWorkerAsync(
        IReadOnlyList<string> paths,
        int startIndex,
        SpecIndexWorkerEntry?[] results,
        SpecIndexWorkerBudget budget,
        DateTimeOffset buildDeadline,
        CancellationToken cancellationToken)
    {
        var batch = paths.Skip(startIndex).Take(SpecIndexWorkerProtocol.MaxPaths).ToArray();

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // UTF-8 explicitly, for ValidationWorkerClient's fully-recorded reason: left unset, .NET
            // writes a redirected stdin in Console.InputEncoding — an OEM code page on Windows —
            // under which a non-ASCII character in a PATH is best-fit mapped before the worker sees
            // it, and the worker would then open a file the parent never named.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        var (fileName, arguments) = ResolveWorkerLaunch();
        startInfo.FileName = fileName;
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
            // ENVIRONMENTAL, and now says so. The first version returned `startIndex` here, which the
            // caller could not tell apart from a parse timeout — so a machine that could not spawn a
            // process published every suite in the workspace as unparseable (a review's finding).
            return new WorkerAttemptResult(
                WorkerAttemptOutcome.Unavailable,
                startIndex,
                $"The suite-index worker process could not be started ({ex.GetType().Name}).");
        }

        var stdout = new StringBuilder();
        var lastOutputTicks = 0L;
        var sawOutput = 0;

        using (process)
        {
            using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var outputCapExceeded = 0;
            void MarkOutputCapExceeded()
            {
                if (Interlocked.CompareExchange(ref outputCapExceeded, 1, 0) == 0)
                {
                    abortCts.Cancel();
                }
            }

            void MarkOutput()
            {
                Volatile.Write(ref lastOutputTicks, DateTimeOffset.UtcNow.UtcTicks);
                Interlocked.Exchange(ref sawOutput, 1);
            }

            // Draining starts BEFORE the exit wait, for ValidationWorkerClient's documented reason: a
            // child that filled the OS pipe buffer while nothing drained it would deadlock against a
            // parent blocked only on WaitForExitAsync. The difference from that class is the SINK —
            // this drain accumulates into a StringBuilder the abort path can still read, which is what
            // makes partial results survive the kill — and the LIVENESS callback, which is what lets
            // the watchdog below tell "working" from "gone quiet".
            var stdoutTask = BoundedStreamReader.DrainAsciiIntoAsync(
                process.StandardOutput.BaseStream, stdout, MaxWorkerOutputBytes, MarkOutputCapExceeded, MarkOutput);
            var stderrTask = BoundedStreamReader.ReadUpToAsync(
                process.StandardError.BaseStream, MaxWorkerOutputBytes, MarkOutputCapExceeded);

            // ── THE STDIN WRITE IS BOUNDED, AND ITS OWN CLOCK STARTS HERE ────────────────────────
            //
            // This write used to sit OUTSIDE every budget: WatchAsync — which owns Startup, Stall and
            // Total — only begins once the write has returned, and the write itself was bounded by
            // nothing but the caller's token. A child that does not read its stdin therefore blocked
            // the parent on the OS pipe buffer indefinitely. MEASURED by a peer review: a 4 KB payload
            // completes, 8 KB blocks, and the 500-path JSON array is around 35 KB — crossing 4 KB at
            // roughly 60 suites, so any realistic workspace was exposed.
            //
            // This class's header claimed it reused ValidationWorkerClient's ordering rule. It did
            // not: that class arms its CancelAfter BEFORE its write, which is exactly the property
            // that was missing here. Now armed.
            //
            // The allowance is Startup, clamped to what remains of the build's Total — so the write
            // lives inside the same envelope as everything else and adds nothing to the worst case.
            // Startup is the right clock for it: a slow write measures the machine and the child's
            // scheduling, never a suite, which is why its expiry can only ever produce an
            // environmental verdict.
            var writeAllowance = Clamp(budget.Startup, buildDeadline);
            if (!await TryWriteStandardInputAsync(process, batch, writeAllowance, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                BoundedStreamReader.ObserveQuietly(stdoutTask);
                BoundedStreamReader.ObserveQuietly(stderrTask);

                // Nothing was sent, so the child was never told what to parse and cannot have examined
                // any suite. A machine fact; no file is blamed.
                return new WorkerAttemptResult(
                    WorkerAttemptOutcome.Unavailable,
                    startIndex,
                    "The suite-index worker did not accept its input within the start-up allowance, so "
                    + "no suite could be examined.");
            }

            var watchdog = await WatchAsync(process, budget, buildDeadline, () => Volatile.Read(ref sawOutput) != 0,
                () => Volatile.Read(ref lastOutputTicks), abortCts).ConfigureAwait(false);

            if (watchdog != WatchdogVerdict.Exited)
            {
                await KillAndConfirmExitAsync(process).ConfigureAwait(false);

                // The caller's own cancellation is an ordinary MCP request abort and is rethrown as
                // such; only THIS class's own clocks produce the partial-results paths below.
                cancellationToken.ThrowIfCancellationRequested();
            }

            // ── DRAIN THE PIPE BEFORE READING THE BUFFER — a MEASURED race, not a precaution ──────
            //
            // Process exit and "everything the process wrote has been read out of the pipe" are two
            // DIFFERENT events, and the first can precede the second. WaitForExitAsync returns as soon
            // as the child is gone; the bytes it wrote are still in the OS pipe buffer until this
            // side's read loop copies them into `stdout`. Snapshotting the StringBuilder immediately
            // after the wait therefore raced the drain — and on a loaded machine it LOST: a worker
            // that had done its job perfectly appeared to have produced nothing at all, and the batch
            // was published as "worker unavailable" with every healthy suite in it unexamined.
            //
            // Measured: an intermittent full-suite failure on a healthy single-suite workspace,
            // 375 ms end to end (far too fast for any clock here), reporting exactly that. It survived
            // one round of fixes aimed at the timeouts because the timeouts were never the cause.
            //
            // The drain ends on its own when the pipe closes, which the child's death guarantees on
            // every path above — so this await is short by construction. It is bounded anyway: a
            // never-closing handle must not become the hang this whole class exists to prevent.
            await AwaitDrainQuietlyAsync(stdoutTask).ConfigureAwait(false);
            BoundedStreamReader.ObserveQuietly(stderrTask);

            string snapshot;
            lock (stdout)
            {
                snapshot = stdout.ToString();
            }

            var reportedThrough = ApplyReportedEntries(snapshot, startIndex, batch.Length, results);

            // "Produced no LINE", not "produced no byte": a worker that emitted a torn fragment and
            // died has told us nothing usable about any suite either, so it belongs on the same path.
            if (reportedThrough == startIndex && watchdog != WatchdogVerdict.Stalled)
            {
                // Each arm NAMES THE CLOCK (or the absence of one) that actually ended the attempt.
                // They all mean "no suite was examined and no suite is to blame", but they are
                // different situations pointing an operator at different things — the machine, the
                // budget, or the worker itself — and one wording for three would send two of the three
                // to the wrong place.
                return new WorkerAttemptResult(
                    WorkerAttemptOutcome.Unavailable,
                    startIndex,
                    watchdog switch
                    {
                        WatchdogVerdict.StartupExpired =>
                            "The suite-index worker did not produce any output within its start-up "
                            + "allowance, so no suite could be examined.",
                        WatchdogVerdict.TotalExpired =>
                            "The index build's overall time budget ran out before the suite-index "
                            + "worker produced any output, so no suite could be examined.",
                        WatchdogVerdict.Aborted =>
                            "The suite-index worker was stopped before producing any usable output, so "
                            + "no suite could be examined.",
                        _ =>
                            "The suite-index worker exited without producing any usable output, so no "
                            + "suite could be examined.",
                    });
            }

            return new WorkerAttemptResult(
                watchdog == WatchdogVerdict.Stalled
                    ? WorkerAttemptOutcome.StalledAfterOutput
                    : WorkerAttemptOutcome.Completed,
                reportedThrough,
                Detail: null);
        }
    }

    /// <summary>
    /// How long the drain is given to finish after the child is gone — see the call site for the race
    /// this closes and the measurement behind it.
    /// </summary>
    /// <remarks>
    /// Generous relative to the work (copying at most a few hundred KB already sitting in a pipe
    /// buffer) and bounded because "wait for a stream to end" is exactly the shape of hang this class
    /// exists to prevent. Exceeding it is not an error condition: whatever HAS arrived is still used,
    /// which is the same partial-results posture every other path here takes.
    /// </remarks>
    private static readonly TimeSpan DrainCompletionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Awaits the drain, bounded, swallowing every failure — the caller uses whatever arrived.</summary>
    private static async Task AwaitDrainQuietlyAsync(Task drainTask)
    {
        try
        {
            await drainTask.WaitAsync(DrainCompletionTimeout).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: a timeout, a
        // faulted read, or a stream disposed under us all mean the same thing here ("no more is
        // coming"), and none of them may become the reported failure when partial output is a
        // first-class, correct answer on this boundary.
        catch (Exception)
#pragma warning restore CA1031
        {
            BoundedStreamReader.ObserveQuietly(drainTask);
        }
    }

    private enum WatchdogVerdict
    {
        /// <summary>The process exited on its own.</summary>
        Exited,

        /// <summary>It never produced output within <see cref="SpecIndexWorkerBudget.Startup"/>.</summary>
        StartupExpired,

        /// <summary>It produced output, then went quiet for a whole <see cref="SpecIndexWorkerBudget.Stall"/>.</summary>
        Stalled,

        /// <summary>
        /// The BUILD's <see cref="SpecIndexWorkerBudget.Total"/> ran out while this attempt was still
        /// running.
        /// </summary>
        /// <remarks>
        /// <b>Split out of <see cref="Aborted"/> so the message can name the clock that actually
        /// expired</b> (a review's finding). It is reachable only on a late attempt whose remaining
        /// share is shorter than <see cref="SpecIndexWorkerBudget.Startup"/> — at which point the
        /// worker may not have spoken yet, and the previous wording told the operator it "exited
        /// without producing any usable output". That conflated "the build ran out of time" with "the
        /// worker is unavailable": no file is blamed either way, but only one of the two is true, and
        /// the difference is exactly what an operator needs to decide whether to look at their machine
        /// or at their budget.
        /// </remarks>
        TotalExpired,

        /// <summary>The output cap was breached, or the caller cancelled.</summary>
        Aborted,
    }

    /// <summary>
    /// Waits for the process to exit, or for one of the two allowances to expire — <b>the mechanism
    /// that stops start-up cost being charged to a suite.</b>
    /// </summary>
    /// <remarks>
    /// A polling loop rather than a single <c>CancelAfter</c> because the deadline MOVES: every line
    /// the worker emits resets the stall clock, so the thing being waited on is "has this child gone
    /// quiet", not "has a fixed instant passed". <see cref="WatchdogInterval"/> is 200 ms, so the
    /// worst-case over-run past an allowance is one interval — irrelevant against a 10-second stall
    /// clock, and far cheaper than a timer per line.
    /// </remarks>
    private static async Task<WatchdogVerdict> WatchAsync(
        Process process,
        SpecIndexWorkerBudget budget,
        DateTimeOffset buildDeadline,
        Func<bool> sawOutput,
        Func<long> lastOutputTicks,
        CancellationTokenSource abortCts)
    {
        var startedAt = DateTimeOffset.UtcNow;

        while (true)
        {
            try
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(abortCts.Token);
                tick.CancelAfter(WatchdogInterval);
                await process.WaitForExitAsync(tick.Token).ConfigureAwait(false);
                return WatchdogVerdict.Exited;
            }
            catch (OperationCanceledException)
            {
                if (abortCts.IsCancellationRequested)
                {
                    return WatchdogVerdict.Aborted;
                }
            }

            var now = DateTimeOffset.UtcNow;

            if (now >= buildDeadline)
            {
                return WatchdogVerdict.TotalExpired;
            }

            if (!sawOutput())
            {
                // STARTUP clock. Measures process start, runtime start-up, JIT and this machine's
                // scheduling — none of which is a property of any suite, which is exactly why
                // exceeding it can never produce a per-file accusation.
                if (now - startedAt >= budget.Startup)
                {
                    return WatchdogVerdict.StartupExpired;
                }

                continue;
            }

            // STALL clock, reset by every line. This is the only clock with evidence about a file:
            // the worker demonstrably parsed the ones before it and then stopped on this one.
            var last = new DateTimeOffset(lastOutputTicks(), TimeSpan.Zero);
            if (now - last >= budget.Stall)
            {
                return WatchdogVerdict.Stalled;
            }
        }
    }

    /// <summary>
    /// Parses the worker's JSON Lines output into <paramref name="results"/> and returns the index of
    /// the first path left unreported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The LAST line is dropped unless the output ends in a newline.</b> A kill can land mid-write,
    /// leaving a half-serialised object; the worker writes a trailing newline after every flush, so
    /// "ends with a newline" is exactly the test for "the last line is complete".
    /// </para>
    /// <para>
    /// <b>An entry whose index is outside the batch is DISCARDED, not clamped.</b> The child is a
    /// process this server started, but it is still a separate process producing text, and an index
    /// that does not correspond to a path this batch sent is either a bug or a lie; writing it
    /// anywhere in <paramref name="results"/> would let one suite's parse result be attributed to
    /// another suite's path.
    /// </para>
    /// <para>
    /// <b>A DUPLICATE index does not move the high-water mark backwards, and a repeat does not
    /// overwrite.</b> Both are properties a well-behaved worker never exercises and a misbehaving one
    /// might: <c>reportedThrough</c> only ever advances, so out-of-order or repeated lines cannot make
    /// the caller resume over files that were already reported.
    /// </para>
    /// </remarks>
    internal static int ApplyReportedEntries(
        string workerStdout, int startIndex, int batchLength, SpecIndexWorkerEntry?[] results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var reportedThrough = startIndex;

        foreach (var line in EnumerateCompleteLines(workerStdout))
        {
            SpecIndexWorkerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<SpecIndexWorkerEntry>(
                    line, ValidationWorkerProtocol.JsonOptions);
            }
            catch (JsonException)
            {
                // Untrusted text from another process — a malformed line is skipped, never fatal.
                continue;
            }

            if (entry is null || entry.Index < 0 || entry.Index >= batchLength)
            {
                continue;
            }

            var absoluteIndex = startIndex + entry.Index;

            // First writer wins. A second line claiming an already-reported index is a contract
            // violation by the child; the first answer is no less trustworthy than the second, and
            // "??=" makes the choice deterministic instead of last-write-wins.
            results[absoluteIndex] ??= entry with { Index = absoluteIndex };

            if (absoluteIndex >= reportedThrough)
            {
                reportedThrough = absoluteIndex + 1;
            }
        }

        return reportedThrough;
    }

    /// <summary>
    /// Re-applies the field bounds on RECEIPT, in the parent.
    /// </summary>
    /// <remarks>
    /// <b>Caps that live only in the child trust the process this codebase documents as untrusted</b>
    /// (a security review's finding). <see cref="SpecIndexParser"/> applies every one of these before
    /// serialising, and that is where they belong — but the parent already re-derives the <c>path</c>
    /// itself rather than accepting the child's, and the same posture has to hold for the remaining
    /// fields or the asymmetry is just an oversight. Re-capping is idempotent for a well-behaved
    /// worker (the strings are already within bounds and already sanitised, so this is a length check
    /// and a copy) and is the only thing standing between a misbehaving one and an unbounded string in
    /// a resource body.
    /// <para>
    /// The null-coalescing on the collections is not defensive padding either: <c>{"index":0}</c> —
    /// a syntactically valid line omitting them — deserialises <see langword="null"/> into
    /// non-nullable <see cref="SpecIndexWorkerEntry.Tags"/>/<see cref="SpecIndexWorkerEntry.StepTypes"/>,
    /// which would then throw on serialisation into the response.
    /// </para>
    /// </remarks>
    internal static SpecIndexWorkerEntry Sanitise(SpecIndexWorkerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry with
        {
            Name = entry.Name is null ? null : SpecIndexParser.CapAndSanitise(entry.Name, SpecIndexParser.MaxNameChars),
            Tags =
            [
                .. (entry.Tags ?? [])
                    .Where(tag => tag is not null)
                    .Take(SpecIndexParser.MaxTagsPerSpec)
                    .Select(tag => SpecIndexParser.CapAndSanitise(tag, SpecIndexParser.MaxTagChars))
            ],
            StepTypes =
            [
                .. (entry.StepTypes ?? [])
                    .Where(stepType => stepType is not null)
                    .Take(SpecIndexParser.MaxStepTypesPerSpec)
                    .Select(stepType => SpecIndexParser.CapAndSanitise(stepType, SpecIndexParser.MaxStepTypeChars))
            ],
            Steps = entry.Steps < 0 ? 0 : entry.Steps,
            ParseError = entry.ParseError is null
                ? null
                : SpecIndexParser.CapAndSanitise(entry.ParseError, SpecIndexParser.MaxParseErrorChars),
        };
    }

    /// <summary>Every newline-terminated, non-blank line in <paramref name="text"/> — see <see cref="ApplyReportedEntries"/>.</summary>
    private static IEnumerable<string> EnumerateCompleteLines(string text)
    {
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield break;
            }

            var line = text[start..newline].TrimEnd('\r');
            if (line.Length > 0)
            {
                yield return line;
            }

            start = newline + 1;
        }
    }

    /// <summary>
    /// What a suite gets when the worker demonstrably worked and then stopped on it.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately does NOT assert a parser spin</b> (a review's finding on the first version,
    /// whose text told the operator their suite "could not be parsed" and pointed at an
    /// unrecoverable spin). A spin is one cause; a heavily loaded machine is another, and this server
    /// cannot tell them apart from the outside. So the message states the OBSERVATION — the budget was
    /// exceeded — names both explanations, and hands the operator the tool that can actually decide.
    /// </remarks>
    internal const string StalledEntryDetail =
        "Parsing this suite exceeded the index build's time budget, so it was skipped. That can mean "
        + "the suite drives the YAML parser into an unrecoverable spin, or simply that this machine "
        + "was heavily loaded. Run validate_suite on this file to tell the two apart.";

    /// <summary>What a suite gets when the worker exited without reporting it and without stalling.</summary>
    internal const string IncompleteEntryDetail =
        "The suite-index worker stopped before reporting this suite. Nothing was established about "
        + "the file itself; run validate_suite on it if you need its details.";

    /// <summary>What suites past the budget/attempt limit get. <b>Blames no file.</b></summary>
    internal const string NotReachedEntryDetail =
        "This suite was not examined: the index build ran out of its time or process budget before "
        + "reaching it. Nothing was established about the file itself.";

    /// <summary>What every suite gets when no worker ever produced output. <b>Blames no file.</b></summary>
    internal const string WorkerUnavailableEntryDetail =
        "This suite was not examined: the suite-index worker process was unavailable, so no suite in "
        + "this workspace could be parsed. Nothing was established about the file itself.";

    private static SpecIndexWorkerEntry Degraded(int index, string reason) =>
        new(index, Name: null, Tags: [], StepTypes: [], Steps: 0, Readable: false, ParseError: reason);

    /// <summary>
    /// <paramref name="allowance"/>, but never past <paramref name="deadline"/>, and never negative.
    /// </summary>
    private static TimeSpan Clamp(TimeSpan allowance, DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < allowance ? remaining : allowance;
    }

    /// <summary>
    /// Writes the batch's paths to the worker's stdin as JSON and closes the handle — the EOF the
    /// worker waits on — bounded by <paramref name="allowance"/>. Returns whether the payload was
    /// delivered. Never throws except for the CALLER's own cancellation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The timeout is enforced by KILLING THE CHILD, not by cancelling the write, and that is the
    /// load-bearing detail.</b> Cancelling a write already blocked inside the OS is
    /// platform-dependent: on Windows the token can abort an in-flight pipe write (CancelIoEx), but on
    /// Unix a FileStream write is typically only cancellable BETWEEN operations, so a token cancelled
    /// mid-write may not be observed until the syscall returns — which, against a child that never
    /// reads, is never. <c>ValidationWorkerClient</c> can rely on its token because its worker drains
    /// stdin to EOF before doing any work; that is a property of a WELL-BEHAVED child, and the case
    /// this bound exists for is a child that is not. So the write is raced against a delay and the
    /// process tree is killed on expiry: killing it closes the read end, which breaks the pipe, which
    /// completes the blocked write with an <see cref="IOException"/>. The kill is the mechanism; the
    /// token is only the fast path.
    /// </para>
    /// <para>
    /// <b>Close() is reached only after the write has given up, for the same reason.</b>
    /// <see cref="StreamWriter.Close"/> flushes synchronously and uncancellably, so calling it while
    /// the pipe is still full would reintroduce the exact unbounded block one line after removing it.
    /// Ordering it after the kill means the handle it closes is already broken, and a broken-pipe
    /// close is swallowed exactly as before.
    /// </para>
    /// </remarks>
    private static async Task<bool> TryWriteStandardInputAsync(
        Process process, IReadOnlyList<string> batch, TimeSpan allowance, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(batch, ValidationWorkerProtocol.JsonOptions);

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeCts.CancelAfter(allowance);

        var writeTask = WriteQuietlyAsync(process, payload, writeCts.Token);
        var delayTask = Task.Delay(allowance, cancellationToken);
        var delivered = await Task.WhenAny(writeTask, delayTask).ConfigureAwait(false) == writeTask
            && await writeTask.ConfigureAwait(false);

        if (!delivered)
        {
            // Unblocks a write the token could not reach — see this method's remarks.
            await KillAndConfirmExitAsync(process).ConfigureAwait(false);
            BoundedStreamReader.ObserveQuietly(writeTask);
        }

        try
        {
            process.StandardInput.Close();
        }
#pragma warning disable CA1031 // Closing an already-broken or already-disposed handle must not become
        // the reported failure — the caller's outcome is the answer.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return delivered;
    }

    /// <summary>The write itself, reporting success as a bool rather than by throwing.</summary>
    private static async Task<bool> WriteQuietlyAsync(Process process, string payload, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: a cancelled write
        // (the allowance), a broken pipe (the child already exited or was just killed), and a disposed
        // handle all mean the same thing to the caller — the payload did not arrive — and none of them
        // may escape ahead of the kill-and-report path.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the executable and arguments needed to re-invoke THIS SAME vouchfx-mcp build in
    /// <see cref="SpecIndexWorkerProtocol.WorkerModeArgument"/> mode.
    /// </summary>
    /// <remarks>
    /// The identical apphost-vs-muxer detection <c>ValidationWorkerClient.ResolveWorkerLaunch</c>
    /// documents in full: <see cref="Environment.ProcessPath"/> is this server's own entry point under
    /// the packaged tool and the <c>dotnet</c> muxer (or a test host) otherwise, in which case this
    /// assembly is launched explicitly via <see cref="System.Reflection.Assembly.Location"/> — so a
    /// worker spawned from a test is just as real a child process as one spawned in production.
    /// </remarks>
    private static (string FileName, IReadOnlyList<string> Arguments) ResolveWorkerLaunch()
    {
        var assembly = typeof(SpecIndexWorkerClient).Assembly;
        var expectedApphostName = assembly.GetName().Name;
        var processPath = Environment.ProcessPath;

        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), expectedApphostName, StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, [SpecIndexWorkerProtocol.WorkerModeArgument]);
        }

        return ("dotnet", [assembly.Location, SpecIndexWorkerProtocol.WorkerModeArgument]);
    }

    /// <summary>
    /// Kills <paramref name="process"/>'s entire tree and waits, bounded, for the exit to be observed
    /// — <c>ValidationWorkerClient.KillAndConfirmExitAsync</c>'s twin, and the ONLY thing that can
    /// stop a Scanner spin (see <see cref="SpecIndexWorkerProtocol"/>'s header).
    /// </summary>
    private static async Task KillAndConfirmExitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
#pragma warning disable CA1031 // Best-effort: the process may already have exited between the check
        // and the kill, or the OS may refuse to kill one that is already gone.
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
        }
    }
}

/// <summary>
/// The three clocks one index build runs under. <b>Two of them bound a single spawn and measure
/// different things</b> — see <see cref="SpecIndexWorkerClient"/>'s remarks for why collapsing them
/// into one wall clock was a defect rather than a simplification.
/// </summary>
/// <param name="Startup">
/// Spawn to FIRST OUTPUT. Measures the machine (process start, JIT, scheduling), never a suite —
/// which is why exceeding it can only ever produce an environmental verdict.
/// </param>
/// <param name="Stall">
/// The gap BETWEEN output lines, reset by each one. Measures one suite's parse, and is therefore the
/// only clock whose expiry may be attributed to a file.
/// </param>
/// <param name="Total">The whole build's budget across all <see cref="SpecIndexWorkerClient.MaxAttempts"/> spawns.</param>
public sealed record SpecIndexWorkerBudget(TimeSpan Startup, TimeSpan Stall, TimeSpan Total);

/// <summary>What one <see cref="SpecIndexWorkerClient.ParseAsync"/> call produced.</summary>
/// <param name="Entries">Exactly one entry per input path, in order, with no gaps.</param>
/// <param name="WorkerUnavailable">
/// <see langword="true"/> when no worker ever produced output — an ENVIRONMENTAL failure, not a
/// statement about any suite. The caller surfaces this as the index's own reason so a host is never
/// shown a directory full of suites falsely described as unparseable.
/// </param>
/// <param name="UnavailableDetail">One sentence saying what went wrong; non-null exactly when <paramref name="WorkerUnavailable"/> is.</param>
public sealed record SpecIndexParseOutcome(
    IReadOnlyList<SpecIndexWorkerEntry> Entries, bool WorkerUnavailable, string? UnavailableDetail);
