using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S6-06's HTTP transport, against the REAL, built <c>vouchfx-mcp</c> process: the bearer-token
/// gate, the refuse-to-start-without-a-token contract, and stdio remaining the untouched default.
/// </summary>
/// <remarks>
/// <para>
/// <b>A spawned process rather than a harness, and necessarily so.</b> What is under test is a
/// STARTUP decision (which transport, and whether a listener is opened at all) plus an ASP.NET Core
/// middleware ordering. Neither exists inside <see cref="McpTestHarness"/>, which attaches an
/// in-memory stream transport to an already-built host — there is no command line, no environment,
/// and no HTTP pipeline for it to exercise.
/// </para>
/// <para>
/// Same <c>Real*</c> convention as <c>RealWorkspaceProcessTests</c>: the real spawned
/// <c>vouchfx-mcp</c>, never the real <c>vouchfx</c> ENGINE CLI. Nothing here needs the engine —
/// these tests never reach a tool handler at all, which is rather the point.
/// </para>
/// </remarks>
public class RealHttpTransportProcessTests
{
    private const string Token = "test-token-8f2a1c4e9b0d7a3f6e5c2b1a0d9f8e7c";

    /// <summary>
    /// The Gherkin's refuse-to-start scenario, end to end: no token ⇒ non-zero exit, a catalogued
    /// diagnosis, and — asserted rather than assumed — nothing listening on the port it would have
    /// used.
    /// </summary>
    [Fact]
    public async Task WithHttpTransportAndNoToken_TheProcessRefusesToStart_AndOpensNoListener()
    {
        var port = ReserveFreePort();

        var (exitCode, stdout, stderr) = await RunToCompletionAsync(
            ["--transport", "http", "--urls", $"http://127.0.0.1:{port}"],
            token: null,
            TimeSpan.FromSeconds(60));

        Assert.NotEqual(0, exitCode);

        // stdout stays the JSON-RPC channel even on a failed HTTP start — nothing may be written there.
        Assert.Equal(string.Empty, stdout);

        var messages = StructuredMessagesOf(stderr);

        Assert.Contains(messages, m => m.Contains("VFX-E-1007", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("VOUCHFX_MCP_HTTP_TOKEN", StringComparison.Ordinal));

        // "And no HTTP listener is opened", evidenced by what the process DID NOT SAY. A bindable port
        // proves nothing — a listener that opened and then closed on exit leaves the port bindable
        // too, so the earlier version of this assertion could not fail. Kestrel logs a "Now listening
        // on" record the instant it binds; its absence, and the absence of the port number anywhere in
        // stderr, is the observable that distinguishes "never bound" from "bound and released".
        Assert.DoesNotContain(messages, m => m.Contains("Now listening", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(messages, m => m.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithHttpTransportAndAToken_AnUnauthenticatedCallIsRejectedWithABareChallenge()
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var response = await client.PostAsync(
            new Uri($"http://127.0.0.1:{port}/mcp"), JsonRpcInitialize());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // A bare challenge: the scheme, and NO body. A body distinguishing "no token configured"
        // from "wrong token" would tell an unauthenticated caller how this server is configured.
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Equal(string.Empty, body);

        // "And no tool result, error or otherwise, is returned" — nothing JSON-RPC-shaped came back.
        Assert.DoesNotContain("jsonrpc", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bearer wrong-token-entirely")]
    [InlineData("Bearer test-token-8f2a1c4e9b0d7a3f6e5c2b1a0d9f8e7")]      // one char short
    [InlineData("Bearer test-token-8f2a1c4e9b0d7a3f6e5c2b1a0d9f8e7cX")]    // one char long
    [InlineData("Basic dGVzdDp0ZXN0")]                                      // wrong scheme
    public async Task WithHttpTransportAndAToken_AnInvalidCredentialIsRejectedIdentically(string authorization)
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = JsonRpcInitialize(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        var response = await client.SendAsync(request);

        // Every failure mode looks the same from outside — no oracle distinguishing "wrong length"
        // from "wrong value" from "wrong scheme".
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    /// <summary>
    /// Anti-vacuity for every rejection above: with the RIGHT token the same request reaches MCP and
    /// gets a protocol answer.
    /// </summary>
    /// <remarks>
    /// Without this, all the 401 assertions would pass just as happily against a server that was
    /// broken, mis-routed, or refusing everything — which is the failure mode an auth test is most
    /// likely to hide.
    /// </remarks>
    [Fact]
    public async Task WithHttpTransportAndTheCorrectToken_TheRequestReachesMcp()
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = JsonRpcInitialize(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // It reached the MCP layer: the answer is JSON-RPC, whatever the SDK made of the handshake.
        Assert.Contains("jsonrpc", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheBearerToken_NeverAppearsInAnythingTheProcessWritesToStderr()
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = JsonRpcInitialize(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        await client.SendAsync(request);

        var stderr = await server.StopAndReadStderrAsync();

        // The token is configured, used to authorise a real request, and must still appear nowhere —
        // not in a startup banner, not in a log record, not in an error.
        Assert.DoesNotContain(Token, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoTransportFlag_TheProcessStillServesStdioAndNeedsNoToken()
    {
        // AC 3: an existing host integration sees no change. No flag, no token in the environment,
        // and the server starts and exits cleanly at stdin EOF exactly as before this story.
        var (exitCode, stdout, stderr) = await RunToCompletionAsync([], token: null, TimeSpan.FromSeconds(60));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);

        var messages = StructuredMessagesOf(stderr);
        Assert.DoesNotContain(messages, m => m.Contains("VFX-E-1007", StringComparison.Ordinal));
    }

    private static StringContent JsonRpcInitialize() =>
        new(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
            """,
            Encoding.UTF8,
            "application/json");

    /// <summary>A port no one is listening on, released before it is handed back.</summary>
    /// <remarks>
    /// The usual bind-to-0-then-release trick. There is an unavoidable race between releasing and the
    /// server binding, accepted because the alternative — parsing Kestrel's chosen port out of a log
    /// stream — couples these tests to log formatting this very sprint just changed.
    /// </remarks>
    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<RunningServer> StartHttpServerAsync(
        int port,
        string token,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = BuildStartInfo(["--transport", "http", "--urls", $"http://127.0.0.1:{port}"], token);

        // The CWD is the lever the hostile-appsettings test pulls: ASP.NET Core would root its
        // configuration here by default.
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        // The environment is the OTHER lever on the same configuration: ASPNETCORE_-prefixed
        // variables are a first-class source for the default host builder.
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start vouchfx-mcp.");

        var stderrTask = process.StandardError.ReadToEndAsync();

        // stdout is DRAINED as well as redirected. Leaving it unread would eventually block the child
        // on a full pipe, and — more to the point — it is the only way to assert that a RUNNING HTTP
        // server writes nothing there, which no other test covered.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var server = new RunningServer(process, stderrTask, stdoutTask);

        // Wait for the port to accept a connection rather than sleeping a fixed interval.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        while (!cts.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                var failure = await stderrTask;
                Assert.Fail($"The HTTP server exited during startup (code {process.ExitCode}). stderr: {failure}");
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return server;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cts.Token);
            }
        }

        // Killed in the failure path too, not just on the happy one — a server left running would hold
        // its port and fail every later test in this class for an unrelated-looking reason.
        server.Dispose();
        Assert.Fail($"The HTTP server did not begin listening on port {port} within the timeout.");
        return server;
    }

    private static ProcessStartInfo BuildStartInfo(string[] serverArguments, string? token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(RepoLayout.ResolveServerDllPath());

        foreach (var argument in serverArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The token reaches the child through the ENVIRONMENT, never argv — which is also what the
        // production contract requires of an operator.
        if (token is not null)
        {
            startInfo.Environment["VOUCHFX_MCP_HTTP_TOKEN"] = token;
        }
        else
        {
            startInfo.Environment.Remove("VOUCHFX_MCP_HTTP_TOKEN");
        }

        return startInfo;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToCompletionAsync(
        string[] serverArguments, string? token, TimeSpan timeout)
    {
        using var process = Process.Start(BuildStartInfo(serverArguments, token))
            ?? throw new InvalidOperationException("Failed to start vouchfx-mcp.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Close stdin so a successfully-started stdio server exits at EOF.
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static List<string> StructuredMessagesOf(string stderr)
    {
        var messages = new List<string>();

        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                messages.Add(document.RootElement.GetProperty("message").GetString() ?? string.Empty);
            }
            catch (JsonException)
            {
                // ASP.NET Core's own startup lines are not this server's records; the structured
                // contract covers what THIS server writes (see StructuredLog's scope remarks), so a
                // foreign line is skipped rather than failing the test.
                messages.Add(line);
            }
        }

        return messages;
    }

    private sealed class RunningServer(Process process, Task<string> stderrTask, Task<string> stdoutTask)
        : IDisposable
    {
        public async Task<string> StopAndReadStderrAsync()
        {
            Stop();
            return await stderrTask;
        }

        /// <summary>Everything the running server wrote to stdout — which must be nothing.</summary>
        public async Task<string> StopAndReadStdoutAsync()
        {
            Stop();
            return await stdoutTask;
        }

        public void Dispose() => Stop();

        private void Stop()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Item 1's regression test: a hostile <c>appsettings.json</c> planted in the working directory
    /// must not move the bind.
    /// </summary>
    /// <remarks>
    /// <c>WebApplication.CreateBuilder</c> roots configuration in the CURRENT DIRECTORY by default —
    /// which, for this server, is whatever untrusted workspace a host launched it from. A planted
    /// file there could rebind the listener (Kestrel endpoint configuration outranks the URLs a host
    /// is asked for) or flip the environment to Development and change the middleware posture. The
    /// host pins the content root to the install directory and configures Kestrel explicitly from the
    /// already-parsed endpoint; this proves both, by running the server with its CWD set to a
    /// directory containing exactly such a file.
    /// </remarks>
    [Fact]
    public async Task AHostileAppSettingsInTheWorkingDirectory_CannotRebindTheListener()
    {
        var port = ReserveFreePort();
        var hijackPort = ReserveFreePort();

        var sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-http-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sandbox, "appsettings.json"),
                $$"""
                {
                  "Kestrel": { "Endpoints": { "Hijack": { "Url": "http://127.0.0.1:{{hijackPort}}" } } },
                  "urls": "http://127.0.0.1:{{hijackPort}}"
                }
                """,
                CancellationToken.None);

            using var server = await StartHttpServerAsync(port, Token, workingDirectory: sandbox);

            // The operator's port serves; the planted one was never bound.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var response = await client.PostAsync(new Uri($"http://127.0.0.1:{port}/mcp"), JsonRpcInitialize());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                await client.PostAsync(new Uri($"http://127.0.0.1:{hijackPort}/mcp"), JsonRpcInitialize()));
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Temp hygiene only.
            }
        }
    }

    /// <summary>
    /// The environment-variable half of the same guarantee: <c>ASPNETCORE_URLS</c> cannot move the
    /// bind either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hostile-<c>appsettings.json</c> test above closes the FILE route; this closes the
    /// ENVIRONMENT route, which is a separate configuration source with separate precedence and is
    /// the one an operator is most likely to have set globally for unrelated reasons. A server that
    /// honoured it would bind somewhere other than the address it was asked for — here, every
    /// interface rather than loopback — while the operator's own <c>--urls</c> silently did nothing.
    /// </para>
    /// <para>
    /// The planted value is <c>0.0.0.0</c> deliberately: that is the widening an accident actually
    /// takes, and asserting on a merely-different loopback port would not distinguish "the explicit
    /// Listen won" from "the wildcard bind also covered the operator's port". Nothing is exposed by
    /// running it — the assertion is that the planted endpoint never binds, and a regression fails
    /// the test within seconds of the listener opening.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAspNetCoreUrlsVariableInTheEnvironment_CannotRebindTheListener()
    {
        var port = ReserveFreePort();
        var hijackPort = ReserveFreePort();

        using var server = await StartHttpServerAsync(
            port,
            Token,
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ASPNETCORE_URLS"] = $"http://0.0.0.0:{hijackPort}",
            });

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // The operator's endpoint serves — the 401 proves the MCP pipeline is on it, not merely that
        // something accepted a socket.
        var response = await client.PostAsync(new Uri($"http://127.0.0.1:{port}/mcp"), JsonRpcInitialize());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The planted one was never bound.
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await client.PostAsync(new Uri($"http://127.0.0.1:{hijackPort}/mcp"), JsonRpcInitialize()));
    }

    /// <summary>
    /// Turns "one DI configuration, two transports" from an inference into a measurement: the HTTP
    /// transport advertises exactly the eighteen tools stdio does.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedToolsListOverHttp_ReturnsExactlyTheEighteenAdvertisedTools()
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var sessionId = await InitializeSessionAsync(client, port);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        var toolNames = ToolNamesIn(body);

        Assert.Equal(18, toolNames.Count);
        Assert.Contains("validate_suite", toolNames);
        Assert.Contains("run_suite", toolNames);
        Assert.Contains("get_run_artifacts", toolNames);
    }

    /// <summary>
    /// The one surface v0.1.0 ships that nothing else drives end to end: a real <c>tools/call</c>
    /// over the real HTTP transport, from the wire to a tool handler and back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not implied by the tools/list test above.</b> That one proves the tool
    /// REGISTRY is reachable over HTTP; it never enters a handler, never builds a
    /// <c>CallToolResult</c>, and never exercises <c>StructuredToolResult</c>'s serialisation or
    /// US-S1-02's <c>meta</c> stamp on this transport. Everything between "the SDK routed the
    /// request" and "the host got a structured answer" was untested on HTTP — which is the half of
    /// the path a transport change is most likely to break.
    /// </para>
    /// <para>
    /// <b><c>explain_diagnostic</c>, deliberately.</b> It is CLI-free (no engine, no Docker),
    /// spawns nothing, touches no filesystem the test would have to prepare, and its answer is
    /// fully determined by an embedded catalogue page — so a failure here is a transport or
    /// serialisation failure and can be nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAuthenticatedToolsCallOverHttp_ReturnsStructuredContentCarryingTheMetaStamp()
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var sessionId = await InitializeSessionAsync(client, port);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"explain_diagnostic","arguments":{"code":"VFX-E-1002"}}}
                """,
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        var result = JsonRpcResultIn(body);

        // A successful tool call, not a tool-level error dressed up as one.
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());

        var structured = result.GetProperty("structuredContent");

        // The payload shape explain_diagnostic promises, arriving intact over HTTP.
        Assert.Equal("VFX-E-1002", structured.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(structured.GetProperty("explanation").GetString()));
        Assert.NotEmpty(structured.GetProperty("commonCauses").EnumerateArray());
        Assert.NotEmpty(structured.GetProperty("fixes").EnumerateArray());
        Assert.Contains("VFX-E-1002", structured.GetProperty("docsUrl").GetString(), StringComparison.Ordinal);

        // The meta stamp — StructuredToolResult's choke point — reaches the wire on this transport
        // too. It is the one field a host reads to know WHICH build answered it, so a transport that
        // dropped it would be silently lossy rather than broken.
        var meta = structured.GetProperty("meta");
        Assert.Equal(ServerIdentity.Version, meta.GetProperty("serverVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("schemaVersion").GetString()));
        Assert.True(meta.TryGetProperty("workspaceRoot", out _));

        // And the text Content block carries the same JSON, which is the half of the result a client
        // that ignores structuredContent reads.
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("VFX-E-1002", text, StringComparison.Ordinal);
    }

    /// <summary>Every tool name in a tools/list answer, whether framed as JSON or as an SSE event.</summary>
    private static List<string> ToolNamesIn(string body) =>
        JsonRpcResultIn(body)
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

    /// <summary>
    /// The <c>result</c> object of the first JSON-RPC response in <paramref name="body"/>, whether
    /// the SDK framed it as a bare JSON document or as a <c>text/event-stream</c> event.
    /// </summary>
    /// <remarks>
    /// ONE framing parser for every assertion in this class, deliberately: which of the two framings
    /// the SDK picks is its choice and can change between versions, and a second copy of this
    /// scanner is a second place to forget that. It fails loudly rather than returning nothing,
    /// because "no result frame" is indistinguishable from "an empty result" to every caller.
    /// </remarks>
    private static JsonElement JsonRpcResultIn(string body)
    {
        foreach (var candidate in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var json = candidate.StartsWith("data:", StringComparison.Ordinal)
                ? candidate["data:".Length..].Trim()
                : candidate;

            if (!json.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty("result", out var result))
                {
                    // Cloned: the JsonDocument backing it is disposed on the way out of this loop.
                    return result.Clone();
                }
            }
            catch (JsonException)
            {
                // Not a JSON-RPC frame — SSE framing lines and blanks fall here.
            }
        }

        Assert.Fail($"No JSON-RPC result frame was found in the response body: {body}");
        return default;
    }

    /// <summary>Runs initialize and returns the session id the server assigned, if any.</summary>
    private static async Task<string?> InitializeSessionAsync(HttpClient client, int port)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = JsonRpcInitialize(),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);

        return response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;
    }

    /// <summary>
    /// Invariant 11 on the HTTP path, for a RUNNING server rather than only a failed start.
    /// </summary>
    /// <remarks>
    /// The refuse-to-start test asserts an empty stdout, but that process never got as far as serving.
    /// This one drives real traffic — authenticated and unauthenticated — through a live server and
    /// then asserts stdout is still byte-empty. stdout stops being the protocol channel under HTTP,
    /// which is exactly when someone might assume it is now free to write to.
    /// </remarks>
    [Fact]
    public async Task ARunningHttpServer_WritesNothingToStdout()
    {
        var port = ReserveFreePort();
        using var server = await StartHttpServerAsync(port, Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        await client.PostAsync(new Uri($"http://127.0.0.1:{port}/mcp"), JsonRpcInitialize());

        using var authorised = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/mcp"))
        {
            Content = JsonRpcInitialize(),
        };
        authorised.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        authorised.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        await client.SendAsync(authorised);

        var stdout = await server.StopAndReadStdoutAsync();

        Assert.Equal(string.Empty, stdout);
    }
}
