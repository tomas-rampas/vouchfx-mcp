using System.Text;

namespace Vouchfx.Mcp.Cli;

/// <summary>
/// The single decoder for the pinned engine's redirected UTF-8 stdout and stderr.
/// </summary>
/// <remarks>
/// The matching engine pin writes UTF-8 without a BOM through process-owned text wrappers.
/// This decoder is independent of Windows console pages, including hidden or detached hosts;
/// it neither queries nor changes the host console. Both CLI runners and schema comparison
/// share this value. An older engine must be refused by the pin handshake before any
/// CLI-dependent operation; this change must ship with the matching ENGINE_PIN update.
/// </remarks>
public static class EngineOutputEncoding
{
    private const int Utf8CodePage = 65001;

    /// <summary>The pinned engine's redirected text encoding, resolved once.</summary>
    public static Encoding Current { get; } = new UTF8Encoding(false);

    /// <summary>Whether <paramref name="encoding"/> is UTF-8, by code page rather than identity.</summary>
    public static bool IsUtf8(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.CodePage == Utf8CodePage;
    }
}
