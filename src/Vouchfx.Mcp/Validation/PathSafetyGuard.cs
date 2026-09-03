using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Rejects a suite path that would make <see cref="File.ReadAllText(string)"/> (or any other
/// filesystem call) reach out over the network, before any such call is made.
/// </summary>
/// <remarks>
/// <para>
/// <b>The threat (M2):</b> on Windows, reading a UNC path (<c>\\host\share\file</c>) triggers an
/// outbound SMB connection — including NTLM authentication — to whatever host the path names.
/// An attacker who controls the <c>path</c> argument passed to <c>validate_suite</c> can name an
/// attacker-controlled host and force this server to authenticate to it, leaking the machine's
/// NTLM credentials (a well-known "forced authentication" / credential-leak primitive) even
/// though the "file" is never actually read successfully.
/// </para>
/// <para>
/// <b>What stays allowed, deliberately:</b> local path traversal (<c>../../etc/whatever</c>) is
/// NOT blocked here — the tool's whole job is reading an arbitrary local file the caller names,
/// and restricting it to some notional "workspace root" is a separate, unrelated policy decision
/// this fix does not make. Only paths that resolve to a network location are rejected.
/// </para>
/// </remarks>
public static class PathSafetyGuard
{
    /// <summary>
    /// Checks whether <paramref name="path"/> would touch the network to be read, and if so,
    /// returns a <see cref="VfxCodeCatalogue.PathOutsideWorkspace"/> error describing why. Returns
    /// <see langword="null"/> for any local path (absolute or relative), including local paths
    /// using <c>../</c> traversal.
    /// </summary>
    /// <remarks>
    /// US-S1-04 changed the returned error's CODE (from the ad-hoc <c>invalid-path</c> kind) and
    /// nothing else — the detection logic below, and therefore exactly which paths this guard
    /// rejects, is untouched. Sprint 3's workspace-containment model is what widens the behaviour;
    /// this story only gave the existing behaviour a stable name.
    /// </remarks>
    public static SuiteValidationError? CheckLocalPath(string path)
    {
        return IsNetworkPath(path)
            ? new SuiteValidationError(
                VfxCodeCatalogue.PathOutsideWorkspace,
                null,
                $"Path must be a local file path, not a network/UNC location: '{TextSanitiser.SanitiseForDisplay(path)}'.",
                null,
                null)
            : null;
    }

    private static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // The literal UNC prefix, in either slash direction — both '\\host\share\...' and
        // '//host/share/...' are accepted by Windows' path APIs, and both would trigger the
        // outbound SMB/NTLM connection described above. This also catches the '\\?\UNC\host\...'
        // extended-length form, since that too begins with '\\'.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        // Secondary check via Path/Uri for a rooted path that names a UNC share without the
        // literal prefix above having matched (defensive backstop for a form this simple prefix
        // check didn't anticipate). A path that fails to parse here (e.g. a relative path, or one
        // containing characters Uri rejects) is not a UNC path we need to block — it either gets
        // through as an ordinary local path, or fails later, on its own terms, when
        // SuiteValidator actually tries to read it.
        if (!Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}
