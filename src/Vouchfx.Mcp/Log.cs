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
}
