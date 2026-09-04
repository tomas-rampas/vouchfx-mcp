using System.Diagnostics;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="ValidationWorkerClient"/> directly: the process-isolation boundary
/// <c>validate_suite</c> now runs through. Every call here spawns a REAL child process (see
/// <see cref="ValidationWorkerClient"/>'s remarks on <c>ResolveWorkerLaunch</c> — its
/// executable-resolution logic correctly launches the real, built <c>Vouchfx.Mcp.dll</c> via
/// <c>dotnet</c> even when called from this test host), so these tests assume the solution was
/// already built at the same configuration as this test run.
/// </summary>
/// <remarks>
/// The full end-to-end MCP-level proof — including the production 10-second
/// <see cref="ValidationWorkerClient.DefaultTimeout"/> and "the server stays responsive
/// afterwards" — lives in <c>RealToolsMcpTests</c> alongside the other <c>validate_suite</c>
/// coverage. These tests instead use a short, explicit <c>timeout</c> override so the
/// timeout/kill mechanism itself can be proven quickly and deterministically.
/// </remarks>
public class ValidationWorkerClientTests
{
    // ── The definitive regression: the Scanner-hang shape, via the real spawn path ─────────────

    [Fact]
    public async Task ValidateAsync_DegenerateHangShape_ReturnsValidationTimeoutAndConfirmsTheWorkerProcessExited()
    {
        // The exact degenerate shape found during YamlSafetyGuard's Scanner-based hardening: a
        // scalar "a: b" immediately followed by a MORE-indented "a: b". This does not fail fast —
        // it drives YamlDotNet's Scanner itself into an unbounded, ~100%-CPU spin with no
        // cooperative cancellation point anywhere in its loop. No in-process guard or
        // CancellationToken can recover from that; only killing the OS process that is spinning
        // can, which is exactly what this proves.
        const string degenerateHangShape = "a: b\n  a: b\n";
        var suitePath = WriteTempSuite(degenerateHangShape);

        try
        {
            var shortTimeout = TimeSpan.FromSeconds(2);
            var stopwatch = Stopwatch.StartNew();

            // ValidateAsyncForTesting is the internal-only test seam (see its remarks): it captures
            // the worker's OS process ID without adding that observability to the public
            // ValidateAsync surface, so "no orphan" below can be verified DIRECTLY against the OS,
            // not merely inferred from how long the call took.
            int? capturedPid = null;
            var result = await ValidationWorkerClient.ValidateAsyncForTesting(
                suitePath, shortTimeout, pid => capturedPid = pid, CancellationToken.None);

            stopwatch.Stop();

            Assert.False(result.Valid);
            var error = Assert.Single(result.Errors);
            Assert.Equal("VFX-E-1150", error.Code);

            // Proves the timeout actually elapsed and the worker was killed at (not well before,
            // and not well after) the requested bound — not that some unrelated fast-fail path
            // happened to also return this same error code.
            Assert.True(
                stopwatch.Elapsed >= shortTimeout,
                $"Expected the call to take at least the {shortTimeout.TotalSeconds}s timeout, took {stopwatch.Elapsed}.");
            Assert.True(
                stopwatch.Elapsed < shortTimeout + TimeSpan.FromSeconds(8),
                $"Expected the call to return soon after the {shortTimeout.TotalSeconds}s timeout " +
                $"(the worker should have been killed promptly), took {stopwatch.Elapsed}.");

            // THE definitive proof of "no orphan": the worker process the client itself started is
            // actually gone at the OS level, not merely assumed gone because enough time passed.
            Assert.NotNull(capturedPid);
            Assert.True(
                IsProcessGone(capturedPid!.Value),
                $"Expected the killed worker process (PID {capturedPid}) to have exited, but the OS " +
                "still reports it as running.");

            // The defining property under test: the CALLER (this test process) was never blocked
            // by the hung child — it is free to make another call immediately.
            var followUpPath = WriteTempSuite("""
                steps:
                  - id: check-health
                    type: http.rest
                    target: orders-api
                    method: GET
                    path: /health
                """);
            try
            {
                var followUpResult = await ValidationWorkerClient.ValidateAsync(followUpPath, timeout: TimeSpan.FromSeconds(10));
                Assert.True(followUpResult.Valid);
            }
            finally
            {
                File.Delete(followUpPath);
            }
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    /// <summary>
    /// Whether the OS confirms process <paramref name="pid"/> has exited: either it no longer
    /// exists at all (<see cref="Process.GetProcessById(int)"/> throws), or it exists but reports
    /// <see cref="Process.HasExited"/>.
    /// </summary>
    private static bool IsProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this ID exists any more — the original worker is gone. (PID reuse
            // by an unrelated process within this test's short window is not a realistic concern.)
            return true;
        }
    }

    // ── Fast, in-process rejects: no worker is ever spawned for these ──────────────────────────

    [Fact]
    public async Task ValidateAsync_MissingFile_ReturnsFileNotFoundQuicklyWithoutSpawningAWorker()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        var stopwatch = Stopwatch.StartNew();

        var result = await ValidationWorkerClient.ValidateAsync(missingPath);

        stopwatch.Stop();

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("VFX-E-1002", error.Code);

        // A spawned dotnet child process takes materially longer than a pure in-process check —
        // this generous bound (well under any plausible process-start time, let alone the
        // 10-second default worker timeout) is only reachable if CheckFastRejects short-circuited
        // before ResolveWorkerLaunch/Process.Start ever ran.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Expected the fast-reject path (no process spawn) to complete quickly, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ValidateAsync_UncPath_ReturnsInvalidPathQuicklyWithoutSpawningAWorker()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await ValidationWorkerClient.ValidateAsync(@"\\attacker-host\share\suite.e2e.yaml");

        stopwatch.Stop();

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("VFX-E-1001", error.Code);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Expected the fast-reject path (no process spawn) to complete quickly, took {stopwatch.Elapsed}.");
    }

    // ── A crashing/deep-nesting input must never hang the parent, even via the real worker ────

    [Fact]
    public async Task ValidateAsync_DeeplyNestedFlowBracketFile_ReturnsTooDeepThroughTheWorkerWithoutHanging()
    {
        // The proven native-StackOverflowException shape from the security review. The worker's
        // own YamlSafetyGuard (running the Scanner-based depth bound — safe against this proven
        // shape, see YamlSafetyGuard's remarks) must reject it well within the timeout, and this
        // call must return promptly either way: if the guard somehow failed to catch it, the
        // worker process itself would be the one to crash/hang, which ValidateAsync must still
        // resolve to a structured result (too-deep on success, validation-timeout/
        // validation-worker-failed if the worker itself misbehaved) rather than hanging this test.
        var suitePath = WriteTempSuite(new string('[', 20_000) + new string(']', 20_000));

        try
        {
            var result = await ValidationWorkerClient.ValidateAsync(suitePath, timeout: TimeSpan.FromSeconds(10));

            Assert.False(result.Valid);
            var error = Assert.Single(result.Errors);
            Assert.Equal("VFX-D-1104", error.Code);
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    private static string WriteTempSuite(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
