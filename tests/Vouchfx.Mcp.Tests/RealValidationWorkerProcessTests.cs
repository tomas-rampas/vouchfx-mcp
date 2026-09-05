using System.Diagnostics;
using System.Text.Json;
using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spawns the real, built vouchfx-mcp binary directly in <c>--validate-worker &lt;path&gt;</c>
/// mode — the hidden, one-shot mode <see cref="ValidationWorkerClient"/> (the <c>validate_suite</c>
/// orchestrator) spawns as a child process. See <see cref="RealServerProcessTests"/> for the
/// equivalent coverage of the ordinary MCP server mode.
/// </summary>
/// <remarks>
/// Confirms worker mode's own contract directly against the real binary, independent of
/// <see cref="ValidationWorkerClient"/>: given <c>--validate-worker &lt;source&gt;</c>, the process
/// runs <see cref="SuiteValidator"/>'s pipeline against that one suite, writes exactly the
/// serialised result to stdout, and exits — without ever starting the MCP host (no handshake is
/// attempted, and the process does not sit waiting on an MCP session the way the ordinary server
/// mode does).
/// <para>
/// <b>Two sources, one mode (US-S2-02):</b> <c>&lt;source&gt;</c> is either a suite file path or
/// <see cref="ValidationWorkerProtocol.InlineYamlArgument"/>, in which case the suite text arrives
/// on the worker's stdin instead. Only the inline form reads stdin at all — the path form still
/// exits without ever touching it, which is what the missing-argument test below turns on.
/// </para>
/// </remarks>
public class RealValidationWorkerProcessTests
{
    [Fact]
    public async Task ValidateWorker_GoodFixture_WritesOnlyTheJsonResultAndExitsZero()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();
        Assert.True(
            File.Exists(serverDllPath),
            $"Expected the built server at '{serverDllPath}'. This test assumes the solution " +
            "was already built at the same configuration as this test run.");

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "good-suite.e2e.yaml");

        var (exitCode, stdout, stderr) = await RunWorkerAsync(serverDllPath, [fixturePath]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<SuiteAnalysis>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result!.Valid);
        Assert.Empty(result.Errors);
        Assert.Equal(2, Assert.IsType<SuiteSummary>(result.Summary).Steps);

        // The default level is `full`, so US-S2-03's rules run here. The fixture declares no
        // `environment` block, so its two targets name nothing declared (VFX-D-1202) and its
        // db-assert step has no postgres dependency (VFX-D-1205) — all WARNINGS, which is why
        // `Valid` above is still true. What this worker test is actually about is the round trip:
        // the semantic channel crosses the process boundary intact, and every finding in it is a
        // VFX-D code.
        Assert.NotEmpty(result.SemanticDiagnostics);
        Assert.All(
            result.SemanticDiagnostics,
            finding => Assert.StartsWith("VFX-D-", finding.Code, StringComparison.Ordinal));
        Assert.DoesNotContain(result.SemanticDiagnostics, finding => finding.Severity == "error");
    }

    [Fact]
    public async Task ValidateWorker_BadFixture_WritesInvalidResultAndExitsZero()
    {
        // A non-zero exit is reserved for a genuine worker crash (see ValidationWorkerClient's
        // validation-worker-failed handling) — an ordinary "the suite is invalid" outcome is a
        // successful run of the worker itself, so this must still exit 0.
        var serverDllPath = RepoLayout.ResolveServerDllPath();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bad-suite.e2e.yaml");

        var (exitCode, stdout, stderr) = await RunWorkerAsync(serverDllPath, [fixturePath]);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<SuiteAnalysis>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result!.Valid);
        Assert.Equal(2, result.Errors.Count);
    }

    // ── US-S2-02: the inline-YAML transport, against the real binary ──────────────────────────
    //
    // Extends this class rather than starting a parallel one: the contract being confirmed is worker
    // mode's, and inline YAML is a second SOURCE for that same mode, not a second mode. Its stdout
    // contract (exactly one serialised SuiteAnalysis, nothing else) has to hold identically.

    [Fact]
    public async Task ValidateWorker_InlineYamlOnStdin_WritesOnlyTheJsonResultAndExitsZero()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();

        var (exitCode, stdout, stderr) = await RunWorkerAsync(
            serverDllPath,
            [ValidationWorkerProtocol.InlineYamlArgument],
            stdin: """
                steps:
                  - id: check-health
                    type: http.rest
                    target: orders-api
                    method: GET
                    path: /health
                """);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<SuiteAnalysis>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result!.Valid);
        Assert.Empty(result.Errors);
        Assert.Equal(1, Assert.IsType<SuiteSummary>(result.Summary).Steps);
    }

    [Fact]
    public async Task ValidateWorker_InlineYamlWithLevelSemantic_SkipsSchemaEvaluation()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();

        var (exitCode, stdout, stderr) = await RunWorkerAsync(
            serverDllPath,
            [
                ValidationWorkerProtocol.InlineYamlArgument,
                ValidationWorkerProtocol.LevelArgumentFor(ValidationLevel.Semantic),
            ],
            stdin: """
                steps:
                  - id: incomplete-http
                    type: http.rest
                    target: orders-api
                """);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<SuiteAnalysis>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result!.Valid);

        // THE claim of this test: the suite above is schema-INVALID (http.rest requires `method`
        // and `path`), and at level "semantic" the schema pass does not run, so `errors` is empty
        // because nothing looked. The semantic channel, by contrast, does have things to say — and
        // that asymmetry is exactly what makes `valid: true` here mean "no semantic error", not
        // "the engine would accept this". (That hazard is now stated in validate_suite's own
        // description.)
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.SemanticDiagnostics);
        Assert.DoesNotContain(result.SemanticDiagnostics, finding => finding.Severity == "error");
    }

    [Fact]
    public async Task ValidateWorker_TwoThousandCharacterKeySuite_ReturnsLineTooLongFast_NotAWorkerTimeout()
    {
        // Issue #71 regression, end to end through the real spawned worker. Before the pre-parse
        // per-line guard, a single ~2 KB plain-scalar mapping key drove the worker's YamlDotNet
        // Scanner past its 10 s wall clock on EVERY validation of that suite, surfacing as
        // VFX-E-1150 (a killed worker) after >90 s. The line-length guard (VFX-D-1107) now rejects
        // it before the Scanner runs, so the worker returns an ordinary invalid result and exits 0
        // — and comfortably inside this harness's 15 s ceiling, which the old timeout path could
        // never do (the 10 s kill alone already overran a 15 s poll under load).
        var serverDllPath = RepoLayout.ResolveServerDllPath();

        var (exitCode, stdout, stderr) = await RunWorkerAsync(
            serverDllPath,
            [ValidationWorkerProtocol.InlineYamlArgument],
            stdin: new string('a', 2000) + ": v");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<SuiteAnalysis>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result!.Valid);
        Assert.Contains(result.Errors, error => error.Code == "VFX-D-1107");
    }

    [Fact]
    public async Task NormalizeWorker_TwoThousandCharacterKeySuite_ReturnsLineTooLongFast_NotAWorkerTimeout()
    {
        // Issue #71's sibling for the normalize entry point (m2). The YamlSafetyGuard line-length
        // check is a SHARED pre-parse guard, but normalize_suite reaches the worker through a
        // different argument shape (--normalize adds a SuiteNormalization envelope and its own parse
        // path inside SuiteValidator.NormaliseYaml). This proves the same 2 KB-plain-key suite is
        // fast-rejected with VFX-D-1107 there too — well under the 15 s harness ceiling — rather than
        // driving the worker's Scanner past its wall clock through a future normalize-specific parse
        // that forgot to run the guard first.
        var serverDllPath = RepoLayout.ResolveServerDllPath();

        var (exitCode, stdout, stderr) = await RunWorkerAsync(
            serverDllPath,
            [ValidationWorkerProtocol.InlineYamlArgument, ValidationWorkerProtocol.NormaliseArgument],
            stdin: new string('a', 2000) + ": v");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        // --normalize switches the worker's stdout shape to SuiteNormalization (never a bare
        // SuiteAnalysis) — see ValidationWorkerProtocol.NormaliseArgument.
        var result = JsonSerializer.Deserialize<SuiteNormalization>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);

        // The guard rejects before any document is built, so there is nothing to canonicalise: the
        // verdict carries VFX-D-1107 and there is no canonical text (and therefore no comment loss).
        Assert.False(result!.Validation.Valid);
        Assert.Contains(result.Validation.Errors, error => error.Code == "VFX-D-1107");
        Assert.Null(result.NormalizedYaml);
        Assert.False(result.CommentsDropped);
    }

    [Fact]
    public async Task ValidateWorker_UnrecognisedLevelArgument_ExitsNonZeroWithoutWritingToStdout()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "good-suite.e2e.yaml");

        var (exitCode, stdout, stderr) = await RunWorkerAsync(serverDllPath, [fixturePath, "--level=deep"]);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public async Task ValidateWorker_MissingPathArgument_ExitsNonZeroWithoutAttemptingAnMcpHandshake()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverDllPath);
        startInfo.ArgumentList.Add(ValidationWorkerProtocol.WorkerModeArgument);
        // Deliberately no path argument.

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx-mcp validation worker process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Worker mode without --yaml-stdin never reads stdin — closing it here proves that: if this process were
        // instead waiting on an MCP handshake, closing stdin would end its session (as
        // RealServerProcessTests proves for the ordinary server), which is a materially
        // different code path from the immediate, synchronous exit asserted below.
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cts.Token);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Empty(await stdoutTask);
        Assert.NotEmpty(await stderrTask);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWorkerAsync(
        string serverDllPath, string[] workerArguments, string? stdin = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverDllPath);
        startInfo.ArgumentList.Add(ValidationWorkerProtocol.WorkerModeArgument);
        foreach (var argument in workerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx-mcp validation worker process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        // Closed either way — the path source never reads stdin, and the inline source reads to EOF,
        // which only arrives once this handle is gone.
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
