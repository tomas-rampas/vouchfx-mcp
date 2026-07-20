using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Resources;
using Vouchfx.Mcp.Tools;

namespace Vouchfx.Mcp;

/// <summary>
/// Registers this server's identity, tool collection, and resource collection with the MCP SDK's
/// DI container.
/// </summary>
/// <remarks>
/// This is the single place that configures <see cref="ModelContextProtocol.Server.McpServerOptions.ServerInfo"/>,
/// <see cref="ModelContextProtocol.Server.McpServerOptions.ToolCollection"/>, and
/// <see cref="ModelContextProtocol.Server.McpServerOptions.ResourceCollection"/>. Both production
/// startup (<see cref="Program"/>, over stdio) and the test suite (over an in-memory paired
/// stream) call this same method, so there is no second copy of the configuration that could
/// drift from what actually ships.
/// </remarks>
public static class VouchfxMcpServerRegistration
{
    /// <summary>
    /// Adds the MCP server with the vouchfx-mcp server identity, tool registry, and resource
    /// registry configured. A transport (stdio, stream, …) still needs to be attached to the
    /// returned builder.
    /// </summary>
    public static IMcpServerBuilder AddVouchfxMcpServer(this IServiceCollection services) =>
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = ServerIdentity.Name,
                Version = ServerIdentity.Version,
            };
            options.ToolCollection = [.. ToolRegistry.CreateAll()];
            options.ResourceCollection = [.. DocResourceRegistry.CreateAll()];
        });
}
