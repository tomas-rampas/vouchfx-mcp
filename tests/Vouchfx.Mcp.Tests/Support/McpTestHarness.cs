using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
/// </remarks>
internal sealed class McpTestHarness : IAsyncDisposable
{
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

    public static async Task<McpTestHarness> StartAsync(CancellationToken cancellationToken)
    {
        var clientToServerPipe = new Pipe();
        var serverToClientPipe = new Pipe();

        // Fully qualified: the instance property McpTestHarness.Host below would otherwise
        // shadow the Microsoft.Extensions.Hosting.Host static factory class within this type's
        // scope.
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services
            .AddVouchfxMcpServer()
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
