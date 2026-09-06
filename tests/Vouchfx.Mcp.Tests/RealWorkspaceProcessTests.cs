using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-08's startup wiring, against the REAL, built <c>vouchfx-mcp</c> process launched with a
/// real <c>--workspace</c> flag — the first Gherkin scenario ("a workspace is resolved at server
/// start") and the AC that <c>ToolMeta.workspaceRoot</c> now sources from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a spawned process rather than <see cref="McpTestHarness"/>.</b> The provenance stamp is
/// composed once per PROCESS and pre-serialised once by <c>Tools/StructuredToolResult</c> (both
/// properties are load-bearing and predate this story — see <c>Tools/ToolMetaProvider</c>). The
/// in-memory harness runs many servers inside this one test process, in parallel, so there is no
/// honest way for it to give one of them a different stamp. A real child process has exactly one
/// startup, exactly one stamp, and leaves no static state behind in the test host — so it can assert
/// the configured value without any test isolation caveat at all.
/// </para>
/// <para>
/// Same <c>Real*</c> convention as <c>RealServerProcessTests</c>: real spawned <c>vouchfx-mcp</c>,
/// never the real <c>vouchfx</c> ENGINE CLI. Nothing here needs the engine — every tool used is
/// CLI-free.
/// </para>
/// </remarks>
public class RealWorkspaceProcessTests : IDisposable
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

    private readonly string _sandbox;
    private readonly string _root;

    public RealWorkspaceProcessTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-ws-process-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "repo");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "inside.e2e.yaml"), ValidSuiteYaml);
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

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    [Fact]
    public async Task RealProcess_StartedWithWorkspace_StampsTheWorkspaceRootOnEveryResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var client = await ConnectAsync(["--workspace", _root], cts.Token);

        var result = await client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_root, "inside.e2e.yaml") },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);

        var meta = result.StructuredContent!.Value.GetProperty("meta");

        // The AC in one assertion: workspaceRoot is the CONFIGURED root, not this process's (or the
        // server process's) base directory.
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)), meta.GetProperty("workspaceRoot").GetString());
    }

    [Fact]
    public async Task RealProcess_StartedWithWorkspace_ContainsPathsAndStillServesInsideOnes()
    {
        var outside = Path.Combine(_sandbox, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.e2e.yaml"), ValidSuiteYaml);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var client = await ConnectAsync(["--workspace", _root], cts.Token);

        var escaping = await client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_root, "..", "elsewhere", "secret.e2e.yaml") },
            cancellationToken: cts.Token);

        Assert.True(escaping.IsError ?? false);
        Assert.Contains("VFX-E-1001", TextOf(escaping), StringComparison.Ordinal);

        // Anti-vacuity, in the same session: containment is a boundary, not a blanket refusal.
        var inside = await client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_root, "inside.e2e.yaml") },
            cancellationToken: cts.Token);

        Assert.False(inside.IsError ?? false);
    }

    /// <param name="joinedArguments">
    /// The server arguments, '|'-separated — xUnit's <c>[InlineData]</c> takes
    /// <c>params object[]</c> and cannot carry a <c>string[]</c> argument without wrapping ceremony.
    /// </param>
    /// <remarks>
    /// The UNC cases are a security review's MAJOR finding, closed end to end: <c>--workspace
    /// \\attacker\share</c> used to resolve, and <see cref="Workspace.Resolve"/>'s config-file probe
    /// then fired an outbound SMB/NTLM authentication at the named host during startup. <b>This test
    /// performs no network I/O</b>, and that is structural rather than hopeful: the rejection is
    /// pure string inspection (<c>PathSafetyGuard.IsNetworkPath</c>) placed before the probe, and
    /// <c>WorkspaceTests.Resolve_NetworkRoot_ThrowsBeforeAnyFilesystemProbe</c> pins that ordering at
    /// the unit seam. What the spawn adds here is the STARTUP contract — non-zero exit, clean stdout
    /// — which only a real process can show.
    /// </remarks>
    [Theory]
    // The flag is last — no value follows it at all.
    [InlineData("--workspace")]
    // Present but empty in the '=' spelling.
    [InlineData("--workspace=")]
    // A network/UNC root, in both spellings and both slash directions.
    [InlineData(@"--workspace|\\attacker-host\share")]
    [InlineData("--workspace|//attacker-host/share")]
    [InlineData(@"--workspace=\\attacker-host\share")]
    public async Task RealProcess_UnusableWorkspaceFlag_ExitsNonZeroWithACleanStdout(string joinedArguments)
    {
        var (exitCode, stdout, stderr) = await RunToStdinEofAsync(joinedArguments.Split('|'));

        // Fail closed: a --workspace that cannot be honoured must never degrade into a running
        // server with containment silently off.
        Assert.NotEqual(0, exitCode);

        // The diagnosis goes to stderr and NOTHING goes to stdout — stdout is the JSON-RPC channel
        // and a startup message on it would corrupt every frame a connected agent reads.
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--workspace", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A peer review's MAJOR finding: a MISSPELLED workspace flag was silently swallowed by the
    /// parse loop, so the operator got a server with path containment off and no indication of it.
    /// The unit-level cases live in <c>WorkspaceTests</c>; what this adds is that the near miss is
    /// genuinely startup-FATAL in the real process rather than merely reported.
    /// </summary>
    [Theory]
    [InlineData("--workspce|X")]
    [InlineData("--workspacey|X")]
    [InlineData("--WORKSPACE|X")]
    public async Task RealProcess_NearMissWorkspaceFlag_ExitsNonZeroAndSuggestsTheRealFlag(string joinedArguments)
    {
        var (exitCode, stdout, stderr) = await RunToStdinEofAsync(joinedArguments.Split('|'));

        Assert.NotEqual(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("did you mean --workspace", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A peer review's MAJOR finding: stderr was byte-identical with and without <c>--workspace</c>,
    /// so nothing the server itself printed told an operator which path policy was in force. Both
    /// modes are asserted in one test precisely because the defect was the ABSENCE of a difference —
    /// checking either mode alone could pass on a banner that says the same thing twice.
    /// </summary>
    [Fact]
    public async Task RealProcess_AnnouncesItsWorkspaceModeOnStderrInBothModes()
    {
        var configured = await RunToStdinEofAsync(["--workspace", _root]);
        var unconfigured = await RunToStdinEofAsync([]);

        // Both are ordinary, successful startups that ended at stdin EOF — this is the normal path,
        // not an error path, and stdout stays the JSON-RPC channel in both.
        Assert.Equal(0, configured.ExitCode);
        Assert.Equal(0, unconfigured.ExitCode);
        Assert.Equal(string.Empty, configured.Stdout);
        Assert.Equal(string.Empty, unconfigured.Stdout);

        Assert.Contains(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)),
            configured.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("path containment ON", configured.Stderr, StringComparison.Ordinal);

        Assert.Contains("no workspace configured", unconfigured.Stderr, StringComparison.Ordinal);
        Assert.Contains("path containment OFF", unconfigured.Stderr, StringComparison.Ordinal);

        // The two banners are mutually exclusive: an operator reading one line must not have to
        // check whether the other also appeared.
        Assert.DoesNotContain("containment OFF", configured.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("containment ON", unconfigured.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spawns the real server with <paramref name="serverArguments"/>, immediately closes its stdin,
    /// and waits for it to exit — which is either a startup refusal (non-zero) or a normal
    /// run-until-EOF (zero, since <c>WithStdioServerTransport</c> stops the host when the session
    /// ends). Speaks no MCP deliberately: what these cases assert is the process's own stdout/stderr
    /// contract, and <see cref="StdioClientTransport"/> owns the child's stderr where it is used.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToStdinEofAsync(
        string[] serverArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(ResolveServerDllPath());
        foreach (var argument in serverArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx-mcp server process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<McpClient> ConnectAsync(string[] serverArguments, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { ResolveServerDllPath() };
        arguments.AddRange(serverArguments);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "vouchfx-mcp-under-test",
            Command = "dotnet",
            Arguments = arguments,
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static string ResolveServerDllPath()
    {
        var path = RepoLayout.ResolveServerDllPath();
        Assert.True(
            File.Exists(path),
            $"Expected the built server at '{path}'. This test assumes the solution was already built "
            + "at the same configuration as this test run — true both in CI (build always precedes "
            + "test at the same -c) and in the documented local workflow.");

        return path;
    }

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
