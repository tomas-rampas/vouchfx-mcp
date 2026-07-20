using System.Globalization;
using System.Text;

namespace Vouchfx.Mcp;

/// <summary>
/// Renders untrusted text safely for display — in an exception message, a tool result, or
/// anywhere else it might eventually reach a terminal — by replacing every character outside the
/// printable ASCII range with a literal <c>\uXXXX</c> escape.
/// </summary>
/// <remarks>
/// This is a security boundary, not cosmetics. Text that started life as a value a caller
/// supplied — a file path argument, a YAML scalar, a step type name, an exception message that
/// echoes back part of the input — can carry arbitrary bytes, including an ANSI/terminal escape
/// sequence (e.g. the ESC byte followed by a title-set or cursor-control sequence). Every place in
/// this codebase that splices such a value into a message meant for display renders it through
/// this helper first — never interpolates it directly. Originally introduced as
/// <c>EnginePin.SanitiseForDisplay</c> (which now delegates here) and promoted to a shared
/// helper so <c>Vouchfx.Mcp.Validation</c>'s suite validator and tool error messages can use it
/// without reaching into <see cref="EnginePin"/>.
/// </remarks>
public static class TextSanitiser
{
    /// <summary>
    /// Renders <paramref name="value"/> safely for display: every character outside the printable
    /// ASCII range (<c>0x20</c>-<c>0x7E</c>) is replaced with a literal <c>\uXXXX</c> escape rather
    /// than passed through raw.
    /// </summary>
    public static string SanitiseForDisplay(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isPrintableAscii = c is >= (char)0x20 and <= (char)0x7E;
            if (isPrintableAscii)
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(value.Length + 16).Append(value, 0, i);
            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }

        return builder?.ToString() ?? value;
    }
}
