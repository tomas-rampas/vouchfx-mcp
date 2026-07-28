using System.Diagnostics;

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
    /// How long to wait for a short, Docker-free CLI invocation (<c>--version</c>, <c>list</c>,
    /// <c>schema</c>, <c>scaffold</c>) before giving up and killing it. These do no I/O against
    /// containers and should return in well under a second; 15 seconds is generous headroom for a
    /// slow CI runner while still bounding how long a hung or misbehaving binary on PATH can occupy
    /// a slot.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

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
        var result = await RunAsync(arguments, maxStreamBytes, cancellationToken).ConfigureAwait(false);
        return result is { Launched: true, ExitCode: 0 } ? result.Stdout : null;
    }

    public async Task<CliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        long maxStreamBytes,
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
            timeoutCts.CancelAfter(Timeout);

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
            var stdoutTask = BoundedStreamReader.ReadUpToAsync(process.StandardOutput.BaseStream, maxStreamBytes, MarkOutputCapExceeded);
            var stderrTask = BoundedStreamReader.ReadUpToAsync(process.StandardError.BaseStream, maxStreamBytes, MarkOutputCapExceeded);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await KillAndConfirmExitAsync(process).ConfigureAwait(false);
                BoundedStreamReader.ObserveQuietly(stdoutTask);
                BoundedStreamReader.ObserveQuietly(stderrTask);

                // The caller's own cancellation is rethrown as-is; only THIS method's own timeout
                // OR an output-cap breach resolves to "not usable" rather than an exception.
                cancellationToken.ThrowIfCancellationRequested();
                return CliInvocationResult.NotLaunched;
            }

            // Cap race: either stream hit the bound just as the process exited — treat as not usable
            // so TryRunStdoutAsync keeps returning null and scaffold fails closed.
            if (outputCapExceeded != 0)
            {
                BoundedStreamReader.ObserveQuietly(stdoutTask);
                BoundedStreamReader.ObserveQuietly(stderrTask);
                return CliInvocationResult.NotLaunched;
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
