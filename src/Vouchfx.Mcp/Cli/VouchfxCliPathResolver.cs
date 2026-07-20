namespace Vouchfx.Mcp.Cli;

/// <summary>
/// Resolves <c>vouchfx</c> to an ABSOLUTE executable path via an explicit, PATH-only search —
/// never letting the OS process loader resolve a bare command name itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>CWE-427 (uncontrolled search path element) — why this exists at all:</b> handing a BARE
/// name (<c>"vouchfx"</c>, no directory) to <c>ProcessStartInfo.FileName</c> with
/// <c>UseShellExecute = false</c> does not skip name resolution — the OS still resolves it, and on
/// Windows <c>CreateProcess</c> searches the CALLING PROCESS'S OWN CURRENT WORKING DIRECTORY
/// *before* PATH. This server's CWD is plausibly the user's opened (untrusted) workspace — the
/// very thing <c>validate_suite</c> and friends already treat as hostile input. An attacker who
/// drops a <c>vouchfx.exe</c>/<c>.cmd</c>/<c>.bat</c> in that workspace, echoing back the PUBLIC,
/// checked-in <c>ENGINE_PIN</c> version, would pass this whole handshake with a FAKE binary — one
/// that becomes arbitrary code execution the moment <c>run_suite</c> (a later todo) actually
/// invokes it, not just probes <c>--version</c>. The handshake's entire trust assumption
/// ("vouchfx resolves via PATH") was false as long as a bare name was handed to the process loader.
/// </para>
/// <para>
/// <b>The fix:</b> perform the PATH search HERE, ourselves, explicitly — reading only the PATH
/// environment variable (never the working directory, never any other implicit search location) —
/// and hand <see cref="VouchfxCliProcessRunner"/> the resulting ABSOLUTE path to spawn. An absolute
/// path is never subject to CWD-relative resolution by the OS at all, by construction: this is not
/// "search CWD last", it is "CWD is never consulted, ever, by anything, because nothing is ever
/// asked to resolve a bare name in the first place".
/// </para>
/// <para>
/// <b>The residual this closes (Copilot + security review):</b> resolving a directory ENTRY from
/// PATH is not, by itself, enough — <c>Path.Combine</c> followed by <c>Path.GetFullPath</c> (used
/// to make the final result absolute) resolves a RELATIVE PATH entry (<c>"."</c>, <c>"tools"</c>,
/// <c>"../bin"</c>, …) against <see cref="Environment.CurrentDirectory"/>, which would silently
/// reintroduce exactly the CWD influence this type exists to exclude, for anyone who happened to
/// have a relative entry on PATH. Every PATH entry is therefore checked with
/// <see cref="Path.IsPathFullyQualified(string)"/> and SKIPPED outright if it is not fully
/// qualified, before it is ever combined with a candidate name — a real tool directory is always
/// absolute, so a relative entry is a misconfiguration with nothing legitimate to find there.
/// </para>
/// <para>
/// <b>Platform search rules:</b> on Windows, <c>PATHEXT</c> lists the extensions
/// (<c>.COM;.EXE;.BAT;.CMD;...</c>) a bare command name is tried against, in order, within EACH
/// PATH directory before moving to the next directory — mirroring exactly what <c>cmd.exe</c>/
/// <c>CreateProcess</c> itself does once CWD is out of the picture. A <c>dotnet tool install
/// --global</c> shim is named after the package's <c>ToolCommandName</c> (<c>vouchfx.exe</c> for
/// this tool) and lives in the global tools directory, which the .NET SDK adds to the user's PATH —
/// so a correct PATH+PATHEXT search finds the REAL installed tool. On Unix, there is no PATHEXT;
/// this checks for a plain <c>vouchfx</c> file with at least one executable permission bit set.
/// </para>
/// </remarks>
public static class VouchfxCliPathResolver
{
    private const string CommandName = "vouchfx";

    private static readonly string[] DefaultWindowsExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    /// <summary>
    /// Resolves <c>vouchfx</c> against the CURRENT process's real PATH/PATHEXT environment
    /// variables and the real OS. Returns <see langword="null"/> if it is not found on PATH.
    /// </summary>
    public static string? ResolveAbsolutePath() =>
        ResolveAbsolutePath(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            OperatingSystem.IsWindows());

    /// <summary>
    /// The testable core: resolves <c>vouchfx</c> against explicitly-supplied PATH/PATHEXT values
    /// rather than the real environment, so tests can prove CWD is never consulted without
    /// mutating real, process-wide environment variables. Deliberately takes NO working-directory
    /// parameter at all — there is no CWD search to configure, because there is no CWD search.
    /// </summary>
    internal static string? ResolveAbsolutePath(string? pathVariable, string? pathExtVariable, bool isWindows)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var candidateNames = BuildCandidateNames(pathExtVariable, isWindows);

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // A RELATIVE PATH entry (".", "tools", "../bin", …) would have Path.Combine +
            // Path.GetFullPath below resolve it against the CURRENT WORKING DIRECTORY —
            // reintroducing exactly the CWD influence this whole resolver exists to exclude (see
            // this type's own CWE-427 remarks). Real tool directories are always absolute; a
            // relative PATH entry is a misconfiguration, so skipping it outright is safe, and is
            // what makes the "CWD is never consulted" guarantee actually hold rather than merely
            // being true for the common case of an already-absolute PATH.
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (var candidateName in candidateNames)
            {
                string candidatePath;
                try
                {
                    candidatePath = Path.Combine(directory, candidateName);
                }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: a malformed
                // PATH entry (invalid path characters, etc.) must not abort the whole search or
                // crash the handshake; it is simply not a match, so move on to the next candidate.
                catch (Exception)
#pragma warning restore CA1031
                {
                    continue;
                }

                if (IsResolvableExecutable(candidatePath, isWindows))
                {
                    return Path.GetFullPath(candidatePath);
                }
            }
        }

        return null;
    }

    private static List<string> BuildCandidateNames(string? pathExtVariable, bool isWindows)
    {
        if (!isWindows)
        {
            return [CommandName];
        }

        var extensions = string.IsNullOrEmpty(pathExtVariable)
            ? DefaultWindowsExtensions
            : pathExtVariable.Split(';', StringSplitOptions.RemoveEmptyEntries);

        var names = new List<string>(extensions.Length);
        foreach (var extension in extensions)
        {
            names.Add(CommandName + extension);
        }

        return names;
    }

    private static bool IsResolvableExecutable(string candidatePath, bool isWindows)
    {
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        if (isWindows)
        {
            // The extension was already drawn from PATHEXT when building candidateNames; on
            // Windows, existence is the whole test.
            return true;
        }

        try
        {
#pragma warning disable CA1416 // This call site is reachable on all platforms — deliberate: this
            // branch is only EVER reached when isWindows is false, and the public, real-environment
            // entry point (ResolveAbsolutePath()) always passes isWindows = OperatingSystem.IsWindows(),
            // so on an actual Windows machine this line is provably unreachable at run time. The
            // analyzer's static reachability check cannot see that guarantee through a bool
            // parameter (as opposed to a literal OperatingSystem.IsWindows() call), hence the
            // suppression — the parameter exists specifically so ResolveAbsolutePath's internal
            // overload stays testable with an explicit platform flag rather than the real OS.
            var mode = File.GetUnixFileMode(candidatePath);
#pragma warning restore CA1416
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: any I/O
        // failure reading the file's mode (permissions, a race with deletion, …) means this
        // candidate cannot be confirmed executable, so it is simply not a match.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
