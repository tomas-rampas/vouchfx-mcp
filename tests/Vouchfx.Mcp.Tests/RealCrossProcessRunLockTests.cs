using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-04 across REAL operating-system process boundaries: spec §4.6's
/// <c>&lt;outputDir&gt;/.lock</c> serialising runs per workspace, the <c>VFX-E-1501 RunInProgress</c>
/// rejection it produces (with the active <c>runId</c>), the reclaim of a lock whose holder was
/// killed, and the rule that read-only tools are never affected by either.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which two processes, and why that pairing.</b> The rejected party is a genuinely spawned,
/// built <c>vouchfx-mcp</c> driven over the real MCP wire protocol — everything asserted about the
/// rejection therefore comes out of production code taking the production path. The HOLDER is
/// <c>Vouchfx.Mcp.Tests.RunLockHolderFixture</c>, a separate process that enters exactly the state a
/// mid-run server is in by using the SAME production types (<see cref="WorkspaceRunLock"/> then
/// <see cref="FileRunRegistry"/>, claim first and record second, as
/// <c>RunSuiteOrchestrator</c> does). A second real SERVER cannot play that part: a server only holds
/// the lock while it is running a suite, and running a suite means the pinned engine CLI plus
/// Docker — which CLAUDE.md forbids any test in this repo from depending on. The fixture is the
/// closest thing to a held run that is reachable without them, and it is deliberately not a
/// reimplementation: it holds the very object the server holds, so a change to the lock's open flags
/// changes both sides at once.
/// </para>
/// <para>
/// <b>The kill is the load-bearing part of the staleness proof.</b>
/// <see cref="KilledHolderProcess_ReleasesTheClaimAtTheOperatingSystemLevel"/> terminates the holder
/// without letting a single line of its own cleanup run, which is the only way to demonstrate that
/// the claim is released by the OS (a closed handle on Windows, a dropped <c>flock</c> on Unix)
/// rather than by anything <see cref="WorkspaceRunLock"/> does on the way out. A test that merely
/// disposed the lock would pass against a design with no crash-safety at all.
/// </para>
/// <para>
/// <b><c>Real*</c> here means real MCP wire protocol and real spawned processes — never the real
/// <c>vouchfx</c> ENGINE CLI.</b> No case below reaches REQ-008's handshake: the rejected run is
/// refused at the concurrency gate, which US-S3-04 placed ahead of it, and every other tool used is
/// CLI-free. That is why these run identically on a machine with no engine installed.
/// </para>
/// </remarks>
public class RealCrossProcessRunLockTests : IDisposable
{
    private const string ValidSuiteYaml = """
        metadata:
          name: "Orders API health smoke test"
          owner: "platform-team"

        steps:
          - id: check-health
            type: http.rest
            description: "Confirms the health endpoint responds successfully."
            target: orders-api
            method: GET
            path: /health
        """;

    private const string PassingEventsFileContent = """
        {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
        """;

    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _suitePath;
    private readonly Workspace _workspace;
    private readonly List<Process> _spawnedHolders = [];

    public RealCrossProcessRunLockTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-runlock-xp-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "workspace");
        Directory.CreateDirectory(_root);

        _suitePath = Path.Combine(_root, "orders.e2e.yaml");
        File.WriteAllText(_suitePath, ValidSuiteYaml);

        _workspace = Workspace.Resolve(_root);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        foreach (var holder in _spawnedHolders)
        {
            try
            {
                if (!holder.HasExited)
                {
                    holder.Kill(entireProcessTree: true);
                    holder.WaitForExit(5_000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Teardown hygiene only — a holder that already exited is the normal case.
            }
            finally
            {
                holder.Dispose();
            }
        }

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    // ── Scenario 1: a second server process is rejected while a run is active ───────────────────

    /// <summary>
    /// The story's first Gherkin scenario, end to end: process A holds the lock at
    /// <c>&lt;outputDir&gt;/.lock</c>; process B — a real <c>vouchfx-mcp</c> that has never run
    /// anything and whose own in-process flag is untouched — calls <c>run_suite</c> and is refused
    /// with <c>VFX-E-1501</c>, <c>retryable: true</c>, carrying the active run's id.
    /// </summary>
    [Fact]
    public async Task SecondServerProcess_WhileAnotherProcessHoldsTheLock_IsRejectedWithTheActiveRunId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var (holder, activeRunId) = await StartHolderAsync(cts.Token);
        Assert.False(holder.HasExited);

        await using var serverB = await ConnectAsync(["--workspace", _root], cts.Token);

        var result = await serverB.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = _suitePath },
            cancellationToken: cts.Token);

        Assert.True(result.IsError ?? false);

        var error = GetStructuredContent(result);
        Assert.Equal(VfxCodeCatalogue.RunInProgress, error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());

        // AC-003's substance: not "an id" but THE id of the run holding the claim, so a host can
        // correlate the refusal with the run it must wait for.
        Assert.Equal(activeRunId, error.GetProperty("details").GetProperty("runId").GetString());

        // The rejection is a statement about concurrency, not about the engine: process B never
        // reached REQ-008's handshake, so nothing here depends on a `vouchfx` CLI being installed.
        Assert.DoesNotContain(VfxCodeCatalogue.EngineCliUnavailable, TextOf(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// Anti-vacuity for the case above, and the release leg of AC-001: once the holder lets go, the
    /// identical claim succeeds. Without this, a lock that was permanently unavailable — or a
    /// <c>run_suite</c> broken for some unrelated reason — would pass the rejection test.
    /// </summary>
    [Fact]
    public async Task AfterTheHolderReleasesCleanly_TheClaimIsAvailableAgain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var (holder, _) = await StartHolderAsync(cts.Token);

        // Contended while it is held — measured here rather than assumed, so the assertion after the
        // release is a genuine before/after rather than a single observation.
        Assert.IsType<RunLockResult.HeldByAnotherRun>(NewProductionLock().TryAcquire());

        holder.StandardInput.Close();

        // The fixture's own statement that it took the release path — printed once stdin reached EOF,
        // immediately before the `using` around the claim drops it (the exit code 0 asserted next is
        // what proves that block completed). Asserting the marker is what distinguishes "the holder
        // released the claim" from "the holder died for some unrelated reason and the OS released
        // it", which is the KILL scenario below and must not be able to pass as this one.
        var released = await holder.StandardOutput.ReadLineAsync(cts.Token);
        Assert.Equal(RunLockHolderFixture.Program.ReleasedMarker, released);

        await holder.WaitForExitAsync(cts.Token);
        Assert.Equal(0, holder.ExitCode);

        var acquired = Assert.IsType<RunLockResult.Acquired>(NewProductionLock().TryAcquire());
        acquired.Release.Dispose();
    }

    // ── Scenario 2: a stale lock from a crashed process is reclaimed ────────────────────────────

    /// <summary>
    /// The story's second Gherkin scenario at the mechanism level: the holder is KILLED — no
    /// dispose, no <c>finally</c>, no cleanup of any kind runs in it — and the claim is nonetheless
    /// available to the next taker. Nothing detects or reclaims anything; the operating system
    /// dropped the handle (Windows) or the <c>flock</c> (Unix) when the process died, which is the
    /// property the whole design rests on.
    /// </summary>
    [Fact]
    public async Task KilledHolderProcess_ReleasesTheClaimAtTheOperatingSystemLevel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var (holder, _) = await StartHolderAsync(cts.Token);
        Assert.IsType<RunLockResult.HeldByAnotherRun>(NewProductionLock().TryAcquire());

        holder.Kill(entireProcessTree: true);
        await holder.WaitForExitAsync(cts.Token);

        var acquired = Assert.IsType<RunLockResult.Acquired>(NewProductionLock().TryAcquire());
        acquired.Release.Dispose();
    }

    /// <summary>
    /// The same scenario's "and the new run proceeds rather than being rejected indefinitely" leg,
    /// through the whole server rather than at the lock: after the holder is killed, a
    /// <c>run_suite</c> call against the same workspace completes normally.
    /// </summary>
    /// <remarks>
    /// Driven through <see cref="McpTestHarness"/> rather than a spawned server because this case
    /// must reach the RUNNER, and only the in-memory harness can supply a
    /// <see cref="FakeSuiteRunner"/> — a spawned server would insist on the real engine CLI and
    /// Docker. The cross-process half of the claim is still genuine: the killed holder was a real,
    /// separate OS process, and the harness's server takes the real
    /// <see cref="WorkspaceRunLock"/> through the production
    /// <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/> path because a workspace is
    /// configured.
    /// </remarks>
    [Fact]
    public async Task AfterAHolderIsKilled_ANewRunProceedsThroughTheServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var (holder, _) = await StartHolderAsync(cts.Token);
        holder.Kill(entireProcessTree: true);
        await holder.WaitForExitAsync(cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: _workspace);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = _suitePath },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.Equal("Pass", GetStructuredContent(result).GetProperty("verdict").GetString());
    }

    // ── Scenario 3: read-only tools are unaffected by the lock ──────────────────────────────────

    /// <summary>
    /// The story's third Gherkin scenario, and spec §4.6's "read-only tools are safe to call
    /// concurrently" rule: with the lock genuinely held by another process, the read side of a real
    /// server keeps answering. <c>explain_run</c> and <c>diagnose_run</c> stand in for the whole
    /// read-only family here — they are the registry-reading tools that exist at twelve tools, and
    /// US-S3-03's <c>get_run_status</c>/<c>list_runs</c> reach the registry through the same
    /// lock-free path.
    /// </summary>
    /// <remarks>
    /// A finished run is seeded BEFORE the holder starts, so <c>explain_run</c>'s
    /// default-to-most-recent-finished-run behaviour has something to find that is not the holder's
    /// own in-flight entry. That seeding is also what makes this test non-vacuous: the tools return
    /// real diagnoses rather than "nothing to explain", so a lock that DID block reads would show up
    /// as a hang or a failure rather than as an unchanged empty answer.
    /// </remarks>
    [Fact]
    public async Task ReadOnlyTools_KeepAnsweringWhileAnotherProcessHoldsTheLock()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        SeedFinishedRun();

        var (holder, _) = await StartHolderAsync(cts.Token);
        Assert.False(holder.HasExited);

        await using var serverB = await ConnectAsync(["--workspace", _root], cts.Token);

        var explain = await serverB.CallToolAsync("explain_run", new Dictionary<string, object?>(), cancellationToken: cts.Token);
        Assert.False(explain.IsError ?? false);
        Assert.Equal("Pass", GetStructuredContent(explain).GetProperty("verdict").GetString());

        var diagnose = await serverB.CallToolAsync("diagnose_run", new Dictionary<string, object?>(), cancellationToken: cts.Token);
        Assert.False(diagnose.IsError ?? false);

        var validate = await serverB.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["path"] = _suitePath },
            cancellationToken: cts.Token);
        Assert.False(validate.IsError ?? false);

        // The lock is still held — the reads did not take it, wait for it, or release it. Without
        // this line the three successes above would also be consistent with the holder having died.
        Assert.False(holder.HasExited);
        Assert.IsType<RunLockResult.HeldByAnotherRun>(NewProductionLock().TryAcquire());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="WorkspaceRunLock"/> on the same output directory the servers under test use —
    /// the PRODUCTION type, so "is the claim available?" is answered by the same code a server would
    /// run rather than by a test-local imitation of it.
    /// </summary>
    private WorkspaceRunLock NewProductionLock() => new(_workspace.OutputDir, _workspace);

    /// <summary>
    /// Records a completed run with a readable events file, so the read-only tools have something
    /// real to answer with. Written through the production <see cref="FileRunRegistry"/> for the
    /// same reason: the entry must be one the server will actually accept on read (its
    /// <c>eventsFilePath</c> has to equal the path the registry itself would mint).
    /// </summary>
    private void SeedFinishedRun()
    {
        var registry = new FileRunRegistry(_workspace.OutputDir, _workspace);
        var entry = registry.StartRun([_suitePath]);

        File.WriteAllText(entry.EventsFilePath, PassingEventsFileContent);
        registry.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
    }

    /// <summary>
    /// Spawns the holder fixture and waits for its <c>HELD &lt;runId&gt;</c> line — a deterministic
    /// handshake rather than a sleep, so the claim is provably established before anything is
    /// asserted against it.
    /// </summary>
    private async Task<(Process Holder, string ActiveRunId)> StartHolderAsync(CancellationToken cancellationToken)
    {
        var fixtureDll = RepoLayout.ResolveRunLockHolderFixtureDllPath();
        Assert.True(
            File.Exists(fixtureDll),
            $"Expected the built run-lock holder fixture at '{fixtureDll}'. This test assumes the "
            + "solution was already built at the same configuration as this test run — true both in "
            + "CI and in the documented local workflow.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(fixtureDll);
        startInfo.ArgumentList.Add(_workspace.OutputDir);
        startInfo.ArgumentList.Add(_suitePath);

        var holder = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the run-lock holder fixture process.");
        _spawnedHolders.Add(holder);

        // Drain stderr, unconditionally and from the moment the process exists. A REDIRECTED pipe
        // nobody reads fills at the OS buffer size (~4 KB) and then BLOCKS the child's next write
        // forever — the classic child-hang, and here it would hang a holder this test then waits on.
        // Nothing asserts on the fixture's stderr (it writes there only on a usage/acquisition
        // failure, which surfaces as a missing HELD line below), so the handler discards; its job is
        // to keep the pipe empty, not to collect anything.
        holder.ErrorDataReceived += static (_, _) => { };
        holder.BeginErrorReadLine();

        var line = await holder.StandardOutput.ReadLineAsync(cancellationToken);
        Assert.NotNull(line);

        var parts = line.Split(' ', 2);
        Assert.Equal(RunLockHolderFixture.Program.HeldMarker, parts[0]);
        Assert.Equal(2, parts.Length);

        return (holder, parts[1]);
    }

    private static async Task<McpClient> ConnectAsync(string[] serverArguments, CancellationToken cancellationToken)
    {
        var serverDll = RepoLayout.ResolveServerDllPath();
        Assert.True(File.Exists(serverDll), $"Expected the built server at '{serverDll}'.");

        var arguments = new List<string> { serverDll };
        arguments.AddRange(serverArguments);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "vouchfx-mcp-under-test",
            Command = "dotnet",
            Arguments = arguments,
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static JsonElement GetStructuredContent(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent.Value;
    }

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
