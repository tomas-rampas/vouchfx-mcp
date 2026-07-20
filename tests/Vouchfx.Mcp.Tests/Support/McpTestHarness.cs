using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Hosts the vouchfx-mcp server over an in-memory paired-stream transport and connects an MCP
/// client to it, using the exact same <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/>
/// configuration production startup uses — only the transport (stream pipes instead of real
/// stdio) and logging (cleared, since tests assert stdout cleanliness separately via
/// <see cref="ConsoleOutCapture"/>) differ from <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// Shared by every test class that drives the real MCP server end to end (as opposed to calling
/// a service class like <see cref="Vouchfx.Mcp.Validation.SuiteValidator"/> directly) — see
/// <c>McpServerSkeletonTests</c> and <c>RealToolsMcpTests</c>.
/// <para>
/// <b>The CLI AND the suite runner are FAKED by default, never real</b> (REQ-006/REQ-008):
/// <see cref="StartAsync"/> defaults to <see cref="DefaultTestPin"/>, a <see cref="FakeVouchfxCli"/>
/// reporting that exact pin's own version (an "Ok" CLI gate), and a <see cref="FakeSuiteRunner"/>
/// that is never expected to be reached — so no test in this repo ever depends on the real
/// <c>vouchfx</c> CLI or Docker being installed on the machine running it. Tests that specifically
/// cover the CLI gate's failure behaviour pass their own <c>vouchfxCli</c> and/or <c>enginePin</c>
/// override; tests that cover a real <c>run_suite</c> execution (REQ-006) pass their own
/// <c>suiteRunner</c>.
/// </para>
/// </remarks>
internal sealed class McpTestHarness : IAsyncDisposable
{
    /// <summary>
    /// The <see cref="EnginePin"/> used when a test does not supply its own — an arbitrary but
    /// well-formed pin, unrelated to whatever the real <c>ENGINE_PIN</c> file on disk says (this
    /// harness never reads that file). Tests that care about a SPECIFIC pin value pass their own.
    /// </summary>
    public static readonly EnginePin DefaultTestPin = new("v1.0.0-alpha.9", "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");

    private readonly Pipe _clientToServerPipe;
    private readonly Pipe _serverToClientPipe;

    private McpTestHarness(IHost host, McpClient client, Pipe clientToServerPipe, Pipe serverToClientPipe)
    {
        Host = host;
        Client = client;
        _clientToServerPipe = clientToServerPipe;
        _serverToClientPipe = serverToClientPipe;
    }

    public IHost Host { get; }

    public McpClient Client { get; }

    /// <param name="cancellationToken">Bounds startup — see call sites' own test timeout.</param>
    /// <param name="vouchfxCli">
    /// The CLI probe <c>run_suite</c>'s gate uses. Defaults to a <see cref="FakeVouchfxCli"/>
    /// reporting <paramref name="enginePin"/>'s own version (an "Ok" gate) — never the real CLI.
    /// </param>
    /// <param name="enginePin">
    /// The pin <c>run_suite</c>'s gate checks against. Defaults to <see cref="DefaultTestPin"/>.
    /// </param>
    /// <param name="suiteRunner">
    /// The process runner <c>run_suite</c>'s <see cref="RunSuiteOrchestrator"/> uses to actually
    /// execute a suite. Defaults to a <see cref="FakeSuiteRunner"/> that is never expected to be
    /// invoked by tests that only exercise the gates in front of it (every gate test in this repo
    /// uses a path that fails validation before a runner is ever reached) — never the real CLI or
    /// Docker. Tests that specifically cover a real run (REQ-006) pass their own.
    /// </param>
    public static async Task<McpTestHarness> StartAsync(
        CancellationToken cancellationToken,
        IVouchfxCli? vouchfxCli = null,
        EnginePin? enginePin = null,
        ISuiteRunner? suiteRunner = null)
    {
        var pin = enginePin ?? DefaultTestPin;

        // The real CLI never reports a leading 'v' (see CliVersionNormaliser's remarks) — the
        // default fake mirrors that real shape rather than just echoing the pin's own "vX.Y.Z"
        // form back at itself, so this default "Ok" path exercises the SAME normalisation the
        // real CLI's output needs, not a coincidental match from both sides carrying 'v'.
        var cli = vouchfxCli ?? FakeVouchfxCli.ReportingVersion(CliVersionNormaliser.Normalise(pin.Version));
        var runner = suiteRunner ?? FakeSuiteRunner.NeverExpectedToRun();

        var clientToServerPipe = new Pipe();
        var serverToClientPipe = new Pipe();

        // Fully qualified: the instance property McpTestHarness.Host below would otherwise
        // shadow the Microsoft.Extensions.Hosting.Host static factory class within this type's
        // scope.
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services
            .AddVouchfxMcpServer(pin, cli, runner)
            .WithStreamServerTransport(
                clientToServerPipe.Reader.AsStream(),
                serverToClientPipe.Writer.AsStream());

        var host = hostBuilder.Build();
        await host.StartAsync(cancellationToken);

        try
        {
            var clientTransport = new StreamClientTransport(
                serverInput: clientToServerPipe.Writer.AsStream(),
                serverOutput: serverToClientPipe.Reader.AsStream());

            var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

            return new McpTestHarness(host, client, clientToServerPipe, serverToClientPipe);
        }
        catch
        {
            // The host already started its background MCP session
            // (SingleSessionMcpServerHostedService) above: if the client-side handshake throws
            // or is cancelled, that host must not leak.
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        finally
        {
            try
            {
                await Host.StopAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Host.Dispose();
                CompletePipesQuietly();
            }
        }
    }

    private void CompletePipesQuietly()
    {
        // Client/Host teardown above already completes these via the Stream wrappers' own
        // Dispose in the ordinary case; this is a defensive backstop so the pipes never outlive
        // the harness even if that teardown path was skipped (e.g. StartAsync failed before a
        // client existed). PipeReader/PipeWriter.Complete() is idempotent, but guard anyway
        // rather than let one failure skip the rest.
        TryComplete(() => _clientToServerPipe.Reader.Complete());
        TryComplete(() => _clientToServerPipe.Writer.Complete());
        TryComplete(() => _serverToClientPipe.Reader.Complete());
        TryComplete(() => _serverToClientPipe.Writer.Complete());

        static void TryComplete(Action complete)
        {
            try
            {
                complete();
            }
            catch (InvalidOperationException)
            {
                // Already completed by the stream wrapper's own Dispose — fine.
            }
        }
    }
}
