using System.Reflection;

namespace Vouchfx.Mcp;

/// <summary>
/// The identity this MCP server reports to clients as <c>serverInfo</c> during the MCP
/// initialize handshake: its name and version.
/// </summary>
/// <remarks>
/// Kept as a single source of truth so that production startup (<see cref="Program"/>) and
/// tests configure an identical <see cref="ModelContextProtocol.Protocol.Implementation"/> —
/// there is exactly one place that can drift, and it is covered by
/// <c>McpServerSkeletonTests</c>.
/// </remarks>
public static class ServerIdentity
{
    /// <summary>The name reported as <c>serverInfo.name</c>.</summary>
    public const string Name = "vouchfx-mcp";

    /// <summary>
    /// The version reported as <c>serverInfo.version</c>: the executing assembly's
    /// informational version. The .NET compiler emits <see cref="AssemblyInformationalVersionAttribute"/>
    /// from the <c>&lt;Version&gt;</c> MSBuild property (see Vouchfx.Mcp.csproj) — this is the
    /// same value that will become the packaged tool's NuGet version once it is published.
    /// <see cref="VouchfxMcpServerRegistration"/> is what copies this value into
    /// <see cref="ModelContextProtocol.Protocol.Implementation.Version"/>; the MCP SDK itself
    /// has no part in deriving it.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(ServerIdentity).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
