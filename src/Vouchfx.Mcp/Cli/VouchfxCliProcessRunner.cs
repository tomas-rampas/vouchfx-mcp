using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Vouchfx.Mcp.Cli;

/// <summary>
/// The production <see cref="IVouchfxCli"/>: runs the real <c>vouchfx</c> CLI as a child process
/// for version probes, live catalogue export (<c>list --json</c>), schema export
/// (<c>schema</c>), and catalogue-driven suite scaffold (<c>scaffold --intent</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never a bare command name (CWE-427):</b> the executable path is always resolved to an
/// ABSOLUTE path via <see cref="VouchfxCliPathResolver"/> BEFORE <see cref="Process.Start(ProcessStartInfo)"/>
/// is ever called — see that type's remarks for why handing <c>ProcessStartInfo.FileName</c> a
/// bare <c>"vouchfx"</c> would let the OS process loader search this server's own current working
/// directory (plausibly an untrusted, agent-opened workspace) BEFORE PATH, and how a resolved
/// absolute path sidesteps that entirely rather than merely reordering the search.
/// </para>
/// <para>
/// Mirrors <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>'s already-reviewed
/// process-spawn hardening: <see cref="ProcessStartInfo.ArgumentList"/> (never a shell command
/// line), stdin redirected then immediately closed (never inherits this server's own real stdin —
/// the MCP protocol's read side, in production), stdout/stderr redirected (never inherited
/// either), a bounded wall-clock timeout with a kill-and-confirm on expiry, and — via the shared
/// <see cref="BoundedStreamReader"/> — a bounded read of both output streams rather than an
/// unbounded <c>ReadToEndAsync</c>: a hostile or misbehaving binary resolved from PATH could
/// otherwise emit unbounded output within the timeout window and exhaust this server's own memory,
/// or produce an oversized agent-facing message.
/// </para>
/// </remarks>
public sealed class VouchfxCliProcessRunner : IVouchfxCli
{
    /// <summary>
    /// How the engine CLI's redirected stdout/stderr bytes are DECODED — resolved once for the
    /// process lifetime (the console output code page does not change under a live server).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The issue-#70 fix.</b> A .NET child writes its redirected stdout in the console's active
    /// OUTPUT code page (<c>GetConsoleOutputCP</c>), not UTF-8. Decoding those bytes as UTF-8 — the
    /// old hardcoded behaviour — corrupts every non-ASCII engine byte on any non-65001 console, which
    /// is what made <c>get_schema</c>'s live cross-verification report a false-positive VFX-D-1106.
    /// Decoding with the actual console code page instead recovers the characters that code page can
    /// represent. See <see cref="BoundedStreamReader.ReadUpToAsync"/>'s remarks for the honest limit
    /// of this: a character the console code page cannot represent is best-fit-mapped by the child
    /// BEFORE any byte reaches this process, so it cannot be recovered here (MEASURED: under cp852 the
    /// schema's <c>—</c> and <c>…</c> are lost at the source; its <c>§</c> is recovered). On a console
    /// whose code page represents every character the engine emits (e.g. Windows-1252) the fix is
    /// complete.
    /// </para>
    /// <para>
    /// <b>Fallback to UTF-8 is the correct pairing, not a giveup.</b> A code page of 0 means no
    /// console is attached (a stdio server launched detached, with redirected pipes) — in which case
    /// the <em>child</em> also has no console and .NET defaults ITS redirected stdout to UTF-8, so
    /// UTF-8 here matches. 65001 IS UTF-8. Non-Windows has no console-code-page notion and .NET uses
    /// UTF-8 for redirected output there too. Any failure resolving the code page also falls back to
    /// UTF-8 — the previous behaviour — so this can only ever improve on it, never regress it.
    /// </para>
    /// <para>
    /// <b>Coupling to the engine, stated so it is not forgotten.</b> This decode assumes the engine
    /// writes its redirected stdout in the console code page. If the engine is ever fixed to emit
    /// UTF-8 when redirected (the engine-side half of #70), that change arrives with a new
    /// <c>ENGINE_PIN</c>, and THIS resolver must be revisited at the same time — decoding a
    /// UTF-8-emitting engine's output with cp852 would then corrupt it. The two halves of #70 are
    /// coordinated through the pin. The same latent coupling exists in <c>run_suite</c>'s
    /// <c>VouchfxCliSuiteRunner</c>, which still decodes the engine's redirected event bytes as UTF-8
    /// and was deliberately NOT switched here (#70 was scoped to the <c>get_schema</c>
    /// cross-verification false positive) — so when the engine-side UTF-8-on-redirect fix lands, that
    /// path must be revisited alongside this one.
    /// </para>
    /// </remarks>
    private static readonly Encoding EngineOutputEncoding = ResolveEngineOutputEncoding();

    private static Encoding ResolveEngineOutputEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Encoding.UTF8;
        }

        try
        {
            int codePage = GetConsoleOutputCP();
            if (codePage is 0 or 65001)
            {
                return Encoding.UTF8;
            }

            // Encoding.GetEncoding(852 | 1252 | …) throws NotSupportedException in the in-box runtime
            // without this provider (MEASURED); registering it is process-global and idempotent.
            // Registered only past the UTF-8 short-circuit, so the common no-console/UTF-8 case never
            // touches the global provider — it runs solely when a non-zero, non-65001 page is resolved.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            return Encoding.GetEncoding(codePage);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: any failure
        // resolving the console code page (no console, an unknown/unsupported page, a provider
        // problem) must fall back to the previous UTF-8 behaviour rather than break every CLI relay.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Encoding.UTF8;
        }
    }

    // DllImport rather than [LibraryImport]: the source-generated variant requires
    // <AllowUnsafeBlocks>, which this project deliberately does not enable, and a blittable int
    // return needs no marshalling generation anyway.
    // DefaultDllImportSearchPaths(System32) pins the load to the system directory (defense-in-depth
    // for CA5392): kernel32 is always preloaded so this is not a live hijack risk, but it is one line
    // of hygiene that also silences the analyzer if it is ever promoted to an error.
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetConsoleOutputCP();

    /// <summary>
    /// Maximum bytes read from EITHER of the CLI's stdout or stderr streams for a <c>--version</c>
    /// probe before it is treated as misbehaving.
    /// </summary>
    /// <remarks>
    /// A genuine <c>vouchfx --version</c> reply is a single short line (well under 100 bytes even
    /// with the build-metadata suffix — see <c>CliVersionNormaliser</c>'s remarks for the real,
    /// verified example). 64&#160;KB is a generous margin over any plausible legitimate output while
    /// bounding how much of a hostile or broken binary's output this server ever buffers in memory —
    /// far smaller than <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient.MaxWorkerOutputBytes"/>
    /// because this process's legitimate version output is orders of magnitude smaller than a
    /// validation result can legitimately be.
    /// </remarks>
    public const long MaxCliOutputBytes = 64L * 1024;

    /// <summary>
    /// Maximum bytes read from either stream for <c>vouchfx list --json</c> (shape-level catalogue).
    /// </summary>
    /// <remarks>
    /// A full Core catalogue is well under 100&#160;KB even with field lists; 1&#160;MB leaves headroom
    /// for additional registered community providers without allowing unbounded growth.
    /// </remarks>
    public const long MaxListJsonOutputBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Maximum bytes read from either stream for <c>vouchfx schema</c> (composed JSON Schema).
    /// </summary>
    /// <remarks>
    /// The composed Core schema is currently ~65&#160;KB; 2&#160;MB leaves headroom for additional
    /// provider fragments without allowing unbounded growth.
    /// </remarks>
    public const long MaxSchemaOutputBytes = 2L * 1024 * 1024;

    /// <summary>
    /// Maximum bytes read from either stream for <c>vouchfx scaffold</c> (YAML suite skeleton).
    /// </summary>
    /// <remarks>
    /// A multi-step Core skeleton is well under 100&#160;KB; 1&#160;MB matches the list-json bound and
    /// keeps a misbehaving binary from filling memory while still accepting large environment outlines.
    /// </remarks>
    public const long MaxScaffoldOutputBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Maximum bytes read from either stream for <c>vouchfx plan --json</c> (the schema-versioned
    /// coverage-and-gap report document).
    /// </summary>
    /// <remarks>
    /// A plan report scales with the analysed suite/step/dependency/finding counts, not just the
    /// registration's shape, so it can legitimately be larger than the shape-level exports above —
    /// 4&#160;MB leaves generous headroom for a large suite set and history while still bounding how
    /// much of a misbehaving binary's output this server ever buffers in memory.
    /// </remarks>
    public const long MaxPlanOutputBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Default wall-clock budget for a short, Docker-free CLI invocation (<c>--version</c>,
    /// <c>list</c>, <c>schema</c>, <c>scaffold</c>) before giving up and killing it, used whenever
    /// <see cref="RunAsync"/>'s own <c>timeout</c> parameter is <see langword="null"/>. These do no
    /// I/O beyond a fixed catalogue/schema export or a single scaffold document and should return in
    /// well under a second; 15 seconds is generous headroom for a slow CI runner while still bounding
    /// how long a hung or misbehaving binary on PATH can occupy a slot. <c>plan</c> deliberately does
    /// NOT share this budget — see <see cref="PlanTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Wall-clock budget for <c>vouchfx plan</c> specifically, passed explicitly by
    /// <c>PlanCoverageOrchestrator</c>. Unlike <c>--version</c>/<c>list</c>/<c>schema</c>/<c>scaffold</c>,
    /// <c>plan</c> walks the FULL analysed suite tree plus an optional JSON Lines event-history
    /// directory — I/O that scales with the caller's own suite/history size, not a fixed export (the
    /// exact reasoning <see cref="MaxPlanOutputBytes"/> above already documents for the output cap).
    /// 60 seconds — 4x <see cref="DefaultTimeout"/>, mirroring <see cref="MaxPlanOutputBytes"/>'s own
    /// 4x-larger-than-<see cref="MaxScaffoldOutputBytes"/> proportion — leaves headroom for a large
    /// suite set and history while still bounding how long a hung or misbehaving binary can occupy a
    /// slot.
    /// </summary>
    public static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long <see cref="KillAndConfirmExitAsync"/> waits, after asking the OS to kill the
    /// process, for that exit to actually be observed. Mirrors
    /// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>'s identical constant/rationale.
    /// </summary>
    private static readonly TimeSpan KillConfirmationTimeout = TimeSpan.FromSeconds(2);

    public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
        TryRunStdoutAsync(["--version"], MaxCliOutputBytes, cancellationToken);

    public async Task<string?> TryRunStdoutAsync(
        IReadOnlyList<string> arguments,
        long maxStreamBytes,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(arguments, maxStreamBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result is { Launched: true, ExitCode: 0 } ? result.Stdout : null;
    }

    public async Task<CliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        long maxStreamBytes,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (maxStreamBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStreamBytes), maxStreamBytes, "Must be positive.");
        }

        // Resolved to an ABSOLUTE path, PATH-only, never including the current working directory —
        // see VouchfxCliPathResolver's remarks for the full CWE-427 threat model this closes.
        var resolvedPath = VouchfxCliPathResolver.ResolveAbsolutePath();
        if (resolvedPath is null)
        {
            return CliInvocationResult.NotLaunched;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedPath,
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
            // The resolved path could not be launched (removed between resolution and Start, a
            // permissions problem, …) — not usable, same as "not installed".
            return CliInvocationResult.NotLaunched;
        }

        using (process)
        {
            // Never inherits this server's own real stdin (the MCP protocol's read side, in
            // production): redirected, then closed immediately — version/list/schema/scaffold
            // never need it (scaffold --intent - would, but this server always writes a temp file).
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

            // Guards the cap-exceeded state transition the same way ValidationWorkerClient does:
            // stdout and stderr are read concurrently below, so either (or both, at once) could
            // breach maxStreamBytes — CompareExchange guarantees the 0->1 transition, and so the
            // timeoutCts.Cancel() call that rides on it, happens exactly once.
            var outputCapExceeded = 0;
            void MarkOutputCapExceeded()
            {
                if (Interlocked.CompareExchange(ref outputCapExceeded, 1, 0) == 0)
                {
                    timeoutCts.Cancel();
                }
            }

            // Reading is started BEFORE waiting for exit, not after: the child's stdout/stderr
            // pipes have a finite OS buffer, and a process that produced enough output to fill one
            // while nothing was draining it would deadlock against a parent that is only blocked on
            // WaitForExitAsync. Each stream is bounded independently at maxStreamBytes via the
            // shared BoundedStreamReader — never buffered without limit.
            // Both streams are engine-written under the same console output code page, so both are
            // decoded with EngineOutputEncoding (the #70 fix) rather than the old hardcoded UTF-8.
            var stdoutTask = BoundedStreamReader.ReadUpToAsync(process.StandardOutput.BaseStream, maxStreamBytes, MarkOutputCapExceeded, EngineOutputEncoding);
            var stderrTask = BoundedStreamReader.ReadUpToAsync(process.StandardError.BaseStream, maxStreamBytes, MarkOutputCapExceeded, EngineOutputEncoding);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await KillAndConfirmExitAsync(process).ConfigureAwait(false);
                BoundedStreamReader.ObserveQuietly(stdoutTask);
                BoundedStreamReader.ObserveQuietly(stderrTask);

                // The caller's own cancellation is rethrown as-is; only THIS method's own timeout OR
                // an output-cap breach resolves to a (distinguishable) "not usable" result rather
                // than an exception. outputCapExceeded is checked FIRST: MarkOutputCapExceeded is
                // what cancelled timeoutCts in that case, independently of the wall-clock budget, so
                // it must win over the generic "ran out of time" classification below.
                cancellationToken.ThrowIfCancellationRequested();
                return outputCapExceeded != 0 ? CliInvocationResult.OutputCapExceeded : CliInvocationResult.TimedOut;
            }

            // Cap race: either stream hit the bound just as the process exited — treat as not usable
            // so TryRunStdoutAsync keeps returning null and scaffold/plan fail closed.
            if (outputCapExceeded != 0)
            {
                BoundedStreamReader.ObserveQuietly(stdoutTask);
                BoundedStreamReader.ObserveQuietly(stderrTask);
                return CliInvocationResult.OutputCapExceeded;
            }

            string? stdout;
            string? stderr;
            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate defensive boundary.
            catch (Exception)
#pragma warning restore CA1031
            {
                stdout = null;
            }

            try
            {
                stderr = await stderrTask.ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                stderr = null;
            }

            return CliInvocationResult.Completed(process.ExitCode, stdout, stderr);
        }
    }


    private static async Task KillAndConfirmExitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate, best-effort:
        // mirrors ValidationWorkerClient's identical KillAndConfirmExitAsync — the process may
        // already have exited, or the OS may refuse to kill one that is already gone.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            using var confirmCts = new CancellationTokenSource(KillConfirmationTimeout);
            await process.WaitForExitAsync(confirmCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort confirmation only; there is nothing further to do if it does not
            // confirm within the bound.
        }
    }
}
