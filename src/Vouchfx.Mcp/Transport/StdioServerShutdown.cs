using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Transport;

/// <summary>Starts normal host shutdown when the stdio client disconnects.</summary>
/// <remarks>
/// The SDK awaits in-flight handlers before its session completes. Waiting for that session to
/// stop the host leaves an active run waiting for a cancellation token that never fires on EOF.
/// Observe channel completion without consuming any messages, then let the SDK's hosted service
/// cancel and await its handlers through the existing host shutdown path.
/// </remarks>
internal sealed class StdioServerShutdown(ITransport transport, IHostApplicationLifetime lifetime)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await transport.MessageReader.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is already stopping; there is no second teardown path.
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>Registers the SDK stdio transport and its disconnect observer together.</summary>
    internal static IMcpServerBuilder WithDisconnectAwareStdioTransport(IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithStdioServerTransport();
        builder.Services.AddHostedService<StdioServerShutdown>();

        // Host's default 30 seconds is shorter than the runner's existing 35-second graceful
        // teardown plus 5-second kill confirmation and 2-second output drain. Leave room for those
        // bounds and terminal registry persistence rather than abandoning an unfinished handler.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(50));
        return builder;
    }
}
