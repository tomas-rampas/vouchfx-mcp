using System.Globalization;
using System.Text;

namespace Vouchfx.Mcp;

/// <summary>
/// The vouchfx engine version and commit this MCP server is pinned to, as recorded
/// in the repo-root <c>ENGINE_PIN</c> file. See that file for what each field means
/// and how to advance the pin.
/// </summary>
/// <param name="Version">
/// The published <c>vouchfx</c> tool version (e.g. <c>v1.0.0-alpha.9</c>) the CLI
/// handshake verifies against at run time.
/// </param>
/// <param name="CommitSha">
/// The full 40-character lowercase commit SHA on
/// <see href="https://github.com/tomas-rampas/vouchfx"/> that the vendored
/// artefacts (JSON schema, language reference, recipes) are drift-gated against.
/// </param>
public sealed record EnginePin(string Version, string CommitSha)
{
    private const int CommitShaLength = 40;
    private static readonly string AllZerosCommitSha = new('0', CommitShaLength);

    /// <summary>
    /// Reads and parses the <c>ENGINE_PIN</c> file at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="FormatException">The file's pin line is missing or malformed.</exception>
    public static EnginePin Load(string path)
    {
        if (!File.Exists(path))
        {
            // Message deliberately omits the directory: it is what Program.cs echoes to
            // stderr (via PinFailureReporting), and a startup failure must not leak the host's
            // directory layout. The full path is still recorded in FileNotFoundException.FileName
            // for anyone attaching a debugger or reading it from a caught exception directly.
            throw new FileNotFoundException(
                $"{Path.GetFileName(path)} not found beside the executable.", path);
        }

        return Parse(File.ReadLines(path));
    }

    /// <summary>
    /// Parses the ENGINE_PIN format from <paramref name="lines"/>: the first
    /// non-comment, non-blank line must read
    /// "&lt;version&gt; &lt;full-40-char-lowercase-commit-sha&gt;". Comment lines
    /// (starting with <c>#</c>) and blank lines before that line are skipped;
    /// anything after it (commentary, pin history, …) is ignored.
    /// </summary>
    /// <exception cref="FormatException">
    /// The input has no pin line, or the pin line does not have exactly two
    /// space-separated fields, or the version field contains anything outside
    /// ASCII letters/digits/<c>. - +</c>, or the commit SHA is not exactly 40
    /// lowercase hexadecimal characters, or it is the all-zeros placeholder.
    /// </exception>
    public static EnginePin Parse(IEnumerable<string> lines)
    {
        var pinLine = FindFirstPinLine(lines);
        if (pinLine is null)
        {
            throw new FormatException(
                "ENGINE_PIN is empty or contains only comment/blank lines; expected a pin " +
                "line of the form '<version> <full-40-char-lowercase-commit-sha>'.");
        }

        var fields = pinLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2)
        {
            throw new FormatException(
                $"Malformed ENGINE_PIN line '{SanitiseForDisplay(pinLine)}': expected exactly two " +
                $"space-separated fields '<version> <full-40-char-lowercase-commit-sha>', " +
                $"found {fields.Length}.");
        }

        var version = fields[0];
        var commitSha = fields[1];

        ValidateVersion(version);
        ValidateCommitSha(commitSha);

        return new EnginePin(version, commitSha);
    }

    private static string? FindFirstPinLine(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            return line;
        }

        return null;
    }

    private static void ValidateVersion(string version)
    {
        // Restrict to a conservative printable-ASCII subset (letters, digits,
        // '.', '-', '+' — enough for "v1.0.0-alpha.9"-shaped strings). This is
        // a security boundary, not just a format check: the version flows
        // straight into console output (Program.cs) and, in the MCP server
        // arriving in a later todo, into trust decisions such as the CLI
        // handshake check — so control characters (e.g. ANSI escape
        // sequences) must never survive parsing.
        if (version.Length == 0 || !IsAllowedVersionCharacters(version))
        {
            throw new FormatException(
                $"Malformed ENGINE_PIN version '{SanitiseForDisplay(version)}': expected only ASCII letters, " +
                "digits, '.', '-', or '+' characters (no spaces, control characters, or " +
                "other symbols).");
        }
    }

    private static bool IsAllowedVersionCharacters(string value)
    {
        foreach (var c in value)
        {
            var isAllowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c is '.' or '-' or '+';
            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateCommitSha(string commitSha)
    {
        if (commitSha.Length != CommitShaLength || !IsLowercaseHex(commitSha))
        {
            throw new FormatException(
                $"Malformed ENGINE_PIN commit SHA '{SanitiseForDisplay(commitSha)}': expected exactly " +
                $"{CommitShaLength} lowercase hexadecimal characters, found {commitSha.Length}.");
        }

        if (commitSha == AllZerosCommitSha)
        {
            throw new FormatException(
                "ENGINE_PIN commit SHA is the all-zeros placeholder — replace it with the " +
                "real vouchfx engine commit SHA before use.");
        }
    }

    private static bool IsLowercaseHex(string value)
    {
        foreach (var c in value)
        {
            var isLowercaseHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowercaseHexDigit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Renders <paramref name="value"/> safely for display in an exception message (or,
    /// downstream, on a terminal): every character outside the printable ASCII range
    /// (<c>0x20</c>-<c>0x7E</c>) is replaced with a literal <c>\uXXXX</c> escape rather than
    /// passed through raw.
    /// </summary>
    /// <remarks>
    /// This is a security boundary, not cosmetics. A malformed ENGINE_PIN's version or commit
    /// SHA field can carry arbitrary bytes before validation rejects it — including an ANSI/
    /// terminal escape sequence (e.g. the ESC byte followed by a title-set or cursor-control
    /// sequence). Every call site in this type that reports a failing raw pin value renders it
    /// through this helper first; splicing a raw failing value straight into an exception
    /// message would let that payload reach a terminal verbatim once something downstream (see
    /// <c>Program.cs</c>) prints the message to stderr.
    /// </remarks>
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
