using Microsoft.Extensions.Logging;

namespace Vouchfx.Mcp;

/// <summary>
/// Source-generated log messages for this assembly (<c>CA1848</c>: prefer <see cref="LoggerMessageAttribute"/>
/// delegates over the <see cref="ILogger"/> extension methods).
/// </summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "vouchfx-mcp: pinned to vouchfx engine {Version} ({CommitSha})")]
    public static partial void EnginePinLoaded(ILogger logger, string version, string commitSha);

    /// <summary>
    /// The startup banner stating that a <c>--workspace</c> root IS configured, printed beside
    /// <see cref="EnginePinLoaded"/> — see its counterpart <see cref="NoWorkspaceConfigured"/>.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "vouchfx-mcp: workspace {Root} (path containment ON)")]
    public static partial void WorkspaceConfigured(ILogger logger, string root);

    /// <summary>
    /// The startup banner stating that NO workspace is configured (a peer review's MAJOR finding).
    /// </summary>
    /// <remarks>
    /// Before this pair existed, stderr was byte-identical whether or not the server had been
    /// launched with <c>--workspace</c>, so an operator whose flag never took effect — a typo, a
    /// client config that dropped it — had no way to tell from the server's own output which path
    /// policy was in force. The two modes now announce themselves, in the one channel that is safe
    /// to write to (stdout is the JSON-RPC stream). <c>Workspace.TryParseCommandLine</c>'s near-miss
    /// rejection closes the typo half; this closes the observability half.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "vouchfx-mcp: no workspace configured (path containment OFF)")]
    public static partial void NoWorkspaceConfigured(ILogger logger);
}
