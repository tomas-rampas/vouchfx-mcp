using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Rejects a suite path that would make <see cref="File.ReadAllText(string)"/> (or any other
/// filesystem call) reach out over the network, before any such call is made — and, once a
/// <see cref="Workspace"/> is configured, a path that resolves outside its root.
/// </summary>
/// <remarks>
/// <para>
/// <b>The threat (M2):</b> on Windows, reading a UNC path (<c>\\host\share\file</c>) triggers an
/// outbound SMB connection — including NTLM authentication — to whatever host the path names.
/// An attacker who controls the <c>path</c> argument passed to <c>validate_suite</c> can name an
/// attacker-controlled host and force this server to authenticate to it, leaking the machine's
/// NTLM credentials (a well-known "forced authentication" / credential-leak primitive) even
/// though the "file" is never actually read successfully. <b>That rejection is UNCONDITIONAL</b> —
/// it does not depend on whether a workspace is configured, and US-S3-08 did not weaken it.
/// </para>
/// <para>
/// <b>Containment is workspace-gated, and that gating is the whole point</b> (US-S3-08; plan §2.1).
/// Local path traversal (<c>../../etc/whatever</c>) was deliberately allowed here before Sprint 3 —
/// not overlooked — because the tool's whole job was reading an arbitrary local file the caller
/// named. Restricting it to a root is therefore NEW POLICY, not a bug fix, and it applies only when
/// the host opted in by launching this server with <c>--workspace &lt;path&gt;</c>. With no
/// workspace configured this guard behaves byte for byte as it always has: the UNC check and
/// nothing else, so no caller that never asked for a workspace sees a new rejection. With one
/// configured, every path parameter is canonicalised, symlink-resolved, and asserted to be inside
/// <see cref="Workspace.Root"/>; a violation is
/// <see cref="VfxCodeCatalogue.PathOutsideWorkspace"/> (VFX-E-1001) — the code Sprint 1's US-S1-04
/// reserved for exactly this and populated in the meantime with the UNC case. One idea, one code:
/// no second code was minted for the containment half.
/// </para>
/// <para>
/// <b>Why symlink resolution rather than string arithmetic alone.</b>
/// <see cref="Path.GetFullPath(string)"/> already collapses <c>..</c> segments, which catches the
/// ordinary escape; it does not catch a symlink or NTFS junction INSIDE the root whose target lives
/// outside it. <see cref="ResolveRealPath"/> therefore walks every segment of the candidate and
/// resolves each one's link target, ITERATING TO A FIXED POINT, before the containment comparison —
/// a path that does not exist yet is still contained by its resolved ancestors, which is what lets
/// this guard run before the file-existence check rather than after it.
/// </para>
/// <para>
/// <b>Relative paths resolve against the root when a workspace is configured</b> (US-S3-08 review
/// fix). Every path-taking tool's description promises "absolute or workspace-relative", and a
/// workspace root is very often NOT this process's current directory (a host launches
/// <c>vouchfx-mcp</c> from wherever it happens to live), so resolving a relative path against the
/// CWD would have made every relative path fail containment — the promise and the behaviour would
/// have disagreed. <see cref="ResolveCallerPath"/> is the single seam that closes that: a caller
/// path that is not already fully qualified is rebased onto <see cref="Workspace.Root"/> BEFORE both
/// the containment test and the read, so the guard and the filesystem always see the same absolute
/// path. With no workspace configured this method returns its argument untouched and relative paths
/// keep resolving against the CWD, byte for byte as before.
/// </para>
/// <para>
/// <b>Fail closed on a path this process cannot canonicalise.</b> If
/// <see cref="Path.GetFullPath(string)"/> or the link walk throws, containment cannot be
/// DEMONSTRATED, so the path is rejected. That direction is deliberate: the alternative — treating
/// "could not tell" as "inside" — turns any input that upsets the path APIs into a containment
/// bypass. It costs nothing in practice, because such a path would fail its own read moments later
/// anyway.
/// </para>
/// <para>
/// <b>Two limits this guard does NOT close, stated plainly rather than implied away.</b> First,
/// TOCTOU: containment is decided against the filesystem as it was AT CHECK TIME, and the read
/// happens afterwards — anything with write access to a directory on the path can swap a segment for
/// a link in between, and no path-based check can close that window (only opening a handle and
/// validating it would). Second, HARD LINKS: a hard link is a second directory entry for the same
/// inode, not a reference to another path, so no path API resolves one — a hard link inside the root
/// to a file outside it is, at the path level, genuinely inside the root and is accepted. Both are
/// accepted risks for a local, single-user developer tool whose caller could read those files
/// directly anyway; neither is a reason to weaken the checks that DO hold.
/// </para>
/// </remarks>
public static class PathSafetyGuard
{
    /// <summary>
    /// The comparison containment uses. Case-insensitive on Windows, where <c>D:\Repo</c> and
    /// <c>d:\repo</c> are the same directory and an ordinal comparison would reject a caller's
    /// perfectly contained path; ordinal elsewhere, which is the stricter (and therefore
    /// fail-closed) choice on platforms whose case behaviour is filesystem-dependent rather than
    /// OS-wide.
    /// </summary>
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The TOTAL segment-resolution budget <see cref="ResolveRealPath"/> spends before refusing —
    /// counted across every pass of its fixed-point walk, not per pass. The walk makes up to two
    /// filesystem probes per segment, and a caller-supplied path is not length-bounded before it
    /// reaches here (<c>ExplainRunOrchestrator</c> has a regression test built on a
    /// 200,000-character path), so an unbounded walk would be a cheap way to make this guard slow on
    /// the server's request thread. Exceeding the bound is treated as NOT contained rather than as
    /// "resolved without link-following": a path nested 256 deep is not a suite path, and "could not
    /// tell" must never resolve to "inside" — see this type's fail-closed remarks.
    /// </summary>
    /// <remarks>
    /// Making the budget CUMULATIVE rather than per-pass is what makes a symlink CYCLE
    /// (<c>a → b → a</c> through two directories) terminate: each pass that substitutes a target
    /// spends at least one segment of the budget, so a cycle exhausts it and fails closed instead of
    /// looping forever. See <see cref="ResolveRealPath"/>.
    /// </remarks>
    private const int MaxResolvedPathSegments = 256;

    /// <summary>
    /// Maximum characters of a caller-supplied path ever spliced into a message this guard (or a
    /// caller reusing <see cref="CapAndSanitisePathForDisplay"/>) produces.
    /// </summary>
    /// <remarks>
    /// A review found the error branches in <c>ExplainRunOrchestrator</c> (missing/unreadable/
    /// invalid-path) echoed the FULL caller-supplied path with no length cap at all, and a later
    /// review found THIS type's own <see cref="Reject"/> had the same hole on the branch where no
    /// pre-capped <c>displayPath</c> was supplied: an implausibly long path would yield an oversized
    /// tool ERROR response — undermining the 64&#160;KB envelope cap the success paths carefully
    /// enforce — while also doing unbounded sanitisation work over a value that, by definition, is
    /// never going to resolve to a real file anyway once it is this long. The bound lives HERE, with
    /// the guard that owns path display, so the two call sites share one number rather than drifting.
    /// </remarks>
    internal const int MaxDisplayedPathChars = 1_000;

    /// <summary>
    /// Checks whether <paramref name="path"/> would touch the network to be read, and — when
    /// <paramref name="workspace"/> is non-null — whether it resolves outside that workspace's root.
    /// Returns a <see cref="VfxCodeCatalogue.PathOutsideWorkspace"/> error describing which of the
    /// two it failed, or <see langword="null"/> when it passed both.
    /// </summary>
    /// <param name="path">The caller-supplied path, raw and uncapped.</param>
    /// <param name="workspace">
    /// The workspace resolved at server start, or <see langword="null"/> when the host supplied no
    /// <c>--workspace</c> flag. <see langword="null"/> selects the pre-US-S3-08 behaviour exactly:
    /// UNC rejection only, local traversal allowed.
    /// </param>
    /// <param name="displayPath">
    /// An ALREADY-CAPPED-AND-SANITISED rendering of <paramref name="path"/> to splice into the
    /// message instead of <paramref name="path"/> itself. Exists for <c>ExplainRunOrchestrator</c>,
    /// which builds that rendering ONCE and reuses it across its own non-guard error branches too;
    /// passing it here is what stops that caller having to rebuild this guard's message text itself
    /// and drift from it. <see langword="null"/> (the usual case) means "cap and sanitise
    /// <paramref name="path"/> yourself" — via the same
    /// <see cref="CapAndSanitisePathForDisplay"/> that caller uses, so neither branch can emit an
    /// unbounded path echo.
    /// </param>
    /// <remarks>
    /// US-S1-04 changed the returned error's CODE (from the ad-hoc <c>invalid-path</c> kind) and
    /// nothing else. US-S3-08 is what widened the behaviour — but only for a caller that configured
    /// a workspace; see this type's remarks.
    /// </remarks>
    public static SuiteValidationError? CheckLocalPath(string path, Workspace? workspace = null, string? displayPath = null)
    {
        if (IsNetworkPath(path))
        {
            return Reject(
                "Path must be a local file path, not a network/UNC location",
                path,
                displayPath);
        }

        // An empty path is not a containment question: there is nothing to canonicalise, and
        // Path.GetFullPath would throw on it. Left to fail on its own terms at the read, exactly as
        // it did before this story — CheckLocalPath("") has always returned null.
        if (workspace is null || string.IsNullOrEmpty(path))
        {
            return null;
        }

        return IsInsideWorkspace(path, workspace.Root)
            ? null
            : Reject("Path resolves outside the configured workspace root", path, displayPath);
    }

    /// <summary>
    /// Rebases <paramref name="path"/> onto <paramref name="workspace"/>'s root when it is not
    /// already fully qualified, so "workspace-relative" — which every path-taking tool's description
    /// promises — is what actually happens. Returns <paramref name="path"/> UNCHANGED whenever there
    /// is no workspace, nothing to rebase, or rebasing would be wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This must be called at the ONE seam that also performs the read</b> (or hands the path to
    /// the subprocess that will), and its result used for both the containment check and that read.
    /// A guard that contains one string while the filesystem opens another is not a guard. Today
    /// those seams are <see cref="ValidationWorkerClient"/> (validate_suite / normalize_suite, and
    /// run_suite's EDGE-003 pre-flight), <c>RunSuiteOrchestrator</c> (which additionally splices the
    /// path into the engine CLI's argument list), and <c>ExplainRunOrchestrator</c>.
    /// </para>
    /// <para>
    /// <b>Why <see cref="Path.IsPathFullyQualified(string)"/> rather than
    /// <see cref="Path.IsPathRooted(string)"/>.</b> On Windows <c>IsPathRooted</c> is true for the
    /// drive-RELATIVE forms (<c>\suites\x.yaml</c>, <c>C:x.yaml</c>) whose meaning still depends on
    /// ambient process state — the current drive, or the per-drive current directory. Those are
    /// exactly the forms a workspace should pin down, so they are handed to
    /// <see cref="Path.GetFullPath(string, string)"/> too — which rebases them onto the root itself
    /// only while their drive agrees with it (<c>\suites\x</c> joins the root's VOLUME, and a
    /// cross-drive <c>C:x.yaml</c> against a <c>D:</c> root falls back to that drive's ambient
    /// directory). Either stray lands outside the root and containment refuses it, so the
    /// fail-closed direction holds even where the rebase is not onto the root directory itself.
    /// </para>
    /// <para>
    /// A network path is returned untouched: <see cref="CheckLocalPath"/> rejects it unconditionally
    /// a moment later, and rebasing it first would only risk turning a recognisable UNC string into
    /// something the string check no longer recognises. Likewise an empty path, which
    /// <see cref="CheckLocalPath"/> deliberately leaves to fail on its own terms at the read.
    /// </para>
    /// </remarks>
    public static string ResolveCallerPath(string path, Workspace? workspace)
    {
        if (workspace is null || string.IsNullOrEmpty(path) || Path.IsPathFullyQualified(path) || IsNetworkPath(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path, workspace.Root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path this platform cannot canonicalise at all. Returned unchanged rather than
            // swallowed into some substitute: CheckLocalPath's own GetFullPath will throw on it
            // moments later and fail CLOSED, which is the answer this case deserves — see this
            // type's remarks. Deciding it here would mean inventing a second, quieter verdict.
            return path;
        }
    }

    /// <summary>
    /// Caps <paramref name="path"/> to <see cref="MaxDisplayedPathChars"/> BEFORE sanitising, then
    /// AGAIN afterwards — the one rendering every caller-supplied path goes through before it is
    /// spliced into any message.
    /// </summary>
    /// <remarks>
    /// Two-stage cap, mirroring <c>CliPinVerifier</c>'s identical rationale: capping the RAW text
    /// first keeps sanitisation itself cheap regardless of how long an agent-supplied path is;
    /// sanitising can EXPAND length (each non-printable character becomes a 6-character
    /// <c>\uXXXX</c> escape), so a SECOND cap applied to the ALREADY-sanitised result is what
    /// actually bounds what ends up in the response — the first cap alone would not.
    /// </remarks>
    internal static string CapAndSanitisePathForDisplay(string path)
    {
        var rawCapped = path.Length > MaxDisplayedPathChars ? path[..MaxDisplayedPathChars] : path;
        var sanitised = TextSanitiser.SanitiseForDisplay(rawCapped);
        return sanitised.Length > MaxDisplayedPathChars ? sanitised[..MaxDisplayedPathChars] : sanitised;
    }

    private static SuiteValidationError Reject(string reason, string path, string? displayPath) =>
        new(
            VfxCodeCatalogue.PathOutsideWorkspace,
            null,
            $"{reason}: '{displayPath ?? CapAndSanitisePathForDisplay(path)}'.",
            null,
            null);

    private static bool IsInsideWorkspace(string path, string root)
    {
        string? resolvedPath;
        string? resolvedRoot;
        try
        {
            resolvedPath = ResolveRealPath(Path.GetFullPath(path));

            // The root is resolved through the same walk, not trusted as written: if the root itself
            // sits under a symlink, comparing a link-resolved candidate against an unresolved root
            // would reject every path in the workspace.
            resolvedRoot = ResolveRealPath(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                   or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Fail closed — see this type's remarks.
            return false;
        }

        if (resolvedPath is null || resolvedRoot is null)
        {
            return false;
        }

        resolvedRoot = Path.TrimEndingDirectorySeparator(resolvedRoot);

        // The separator is what makes the test below a containment test rather than a string-prefix
        // test: without it, "…/workspace-a-evil/x" would count as inside "…/workspace-a". It is
        // APPENDED CONDITIONALLY because a genuine filesystem root already carries one and
        // Path.TrimEndingDirectorySeparator deliberately does not trim it (Workspace.Resolve's
        // remarks say so explicitly). Unconditional concatenation produced "C:\\" — or "//" — for a
        // drive-root/filesystem-root workspace, which nothing matches, so `--workspace C:\` rejected
        // every path on the drive it was pointing at. Caught pre-merge by both reviewers.
        var prefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        // Only DirectorySeparatorChar is considered, never AltDirectorySeparatorChar: both operands
        // have been through Path.GetFullPath, which normalises '/' to '\' on Windows, and on Unix
        // '\' is an ordinary filename character rather than a separator at all.
        return string.Equals(resolvedPath, resolvedRoot, PathComparison)
            || resolvedPath.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// Resolves <paramref name="fullPath"/> (already absolute, already <c>..</c>-collapsed) through
    /// every symlink/junction on its way, at ANY level, ITERATING TO A FIXED POINT — returning
    /// <see langword="null"/> when the work needed exceeds <see cref="MaxResolvedPathSegments"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Top-down, segment by segment, and that is not an implementation detail.</b> The obvious
    /// cheaper shape — find the deepest ancestor that exists, resolve THAT one's link target, and
    /// re-append the rest — silently misses the case this guard exists for, because
    /// <see cref="FileSystemInfo.FullName"/> does not resolve a link in an ANCESTOR:
    /// <c>&lt;root&gt;/link/secret.yaml</c>, where <c>link</c> points outside the root, has a
    /// perfectly ordinary non-link file at its deepest existing entry, so that shape returns the
    /// path unchanged and reports the escape as contained. (Measured — it is what the first version
    /// of this method did, and
    /// <c>CheckLocalPath_SymlinkInsideTheRootPointingOutside_IsRejected</c> caught it.)
    /// </para>
    /// <para>
    /// <b>Why ONE top-down pass is still not enough, and the fixed point that fixes it</b> (a
    /// security review's MAJOR finding on the first US-S3-08 implementation). Substituting a
    /// segment's link target splices in a path whose OWN ancestors this walk has never looked at, so
    /// a single pass leaves them unresolved. The concrete bypass: root <c>/ws</c>, a link
    /// <c>/ws/inner → /etc</c>, and a link <c>/ws/link → /ws/inner/passwd</c>. One pass resolves
    /// <c>link</c> to <c>/ws/inner/passwd</c> — textually inside the root — the prefix test passes,
    /// and the read that follows lands on <c>/etc/passwd</c>. So <see cref="ResolveOnePass"/> stops
    /// at the FIRST segment it substitutes, re-attaches the untouched tail, and this method re-walks
    /// the whole result from the top; it returns only once a complete pass changes nothing. That is
    /// the fixed point, and it is the property the containment comparison actually needs: the string
    /// compared contains no unresolved link at any level.
    /// </para>
    /// <para>
    /// <b>Termination is bought with the budget, not with a cycle detector.</b> Every pass that
    /// substitutes anything spends at least one segment of the shared
    /// <see cref="MaxResolvedPathSegments"/> budget, so a symlink cycle exhausts it and this method
    /// returns <see langword="null"/> — NOT CONTAINED, per this type's fail-closed rule — rather
    /// than looping. (Windows also throws <see cref="IOException"/> on a cycle when asked for the
    /// final target; that is handled in <see cref="TryResolveLinkTarget"/>, but the budget is what
    /// makes termination structural rather than dependent on any one platform's error reporting.)
    /// </para>
    /// <para>
    /// A segment that does not exist yet is simply left as written and the walk continues, so a
    /// not-yet-created file inside the root still resolves — containment runs BEFORE the existence
    /// check, and must not turn every missing file into a containment error.
    /// </para>
    /// </remarks>
    private static string? ResolveRealPath(string fullPath)
    {
        var current = fullPath;
        var remainingBudget = MaxResolvedPathSegments;

        while (true)
        {
            var next = ResolveOnePass(current, ref remainingBudget, out var substituted);
            if (next is null)
            {
                return null;
            }

            if (!substituted)
            {
                return Path.GetFullPath(next);
            }

            current = next;
        }
    }

    /// <summary>
    /// Walks <paramref name="fullPath"/> from its root, spending <paramref name="remainingBudget"/>
    /// one segment at a time, and returns as soon as a segment resolves to something different —
    /// with the substitution applied and the untouched tail re-attached, and
    /// <paramref name="substituted"/> set. Returns the path unchanged with
    /// <paramref name="substituted"/> <see langword="false"/> when no segment on it is a link, or
    /// <see langword="null"/> when the budget ran out.
    /// </summary>
    private static string? ResolveOnePass(string fullPath, ref int remainingBudget, out bool substituted)
    {
        substituted = false;

        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            // Not rooted after GetFullPath — nothing this method can meaningfully resolve.
            return fullPath;
        }

        var segments = fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = pathRoot;
        for (var i = 0; i < segments.Length; i++)
        {
            if (remainingBudget-- <= 0)
            {
                return null;
            }

            current = Path.Combine(current, segments[i]);

            var target = TryResolveLinkTarget(current);
            if (target is null)
            {
                continue;
            }

            substituted = true;

            // A link's stored target may be relative to the link's own directory; resolve it against
            // that directory rather than assuming an absolute target.
            var resolved = Path.IsPathRooted(target.FullName)
                ? target.FullName
                : Path.Combine(Path.GetDirectoryName(current) ?? pathRoot, target.FullName);

            // Re-attach everything the ORIGINAL path had below this segment, then hand the whole
            // thing back for a fresh walk — the substituted target's own ancestors have not been
            // examined yet. See ResolveRealPath's remarks for the double-hop bypass this closes.
            for (var j = i + 1; j < segments.Length; j++)
            {
                resolved = Path.Combine(resolved, segments[j]);
            }

            return Path.GetFullPath(resolved);
        }

        return current;
    }

    /// <summary>
    /// The link target of the entry at <paramref name="path"/>, or <see langword="null"/> when there
    /// is no entry there at all or the entry is not a link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Existence is NOT the gate, and using it as one was a real hole</b> (a code review's
    /// finding, folded in with the fixed-point fix). The earlier shape asked
    /// <c>Directory.Exists || File.Exists</c> first and only then resolved the link — but on Unix
    /// both of those FOLLOW the link, so a DANGLING link (target missing) answers false to both and
    /// was silently treated as an ordinary directory name, leaving its target unresolved while this
    /// type's remarks claimed resolution "at ANY level". (Windows happens to answer
    /// <c>File.Exists == true</c> for a dangling link, so the hole was Unix-only — measured on
    /// .NET 8; this tool ships cross-platform.) The link APIs themselves work on a dangling link, so
    /// they are asked directly and a not-found is what distinguishes "no entry" from "not a link".
    /// </para>
    /// <para>
    /// <c>returnFinalTarget: true</c> follows a chain to its end rather than one hop, and on Windows
    /// additionally canonicalises the target's ancestors and any 8.3 short names — strictly more
    /// than this method needs, and worth keeping. It throws <see cref="IOException"/> when the chain
    /// cannot be completed (measured: a symlink cycle reports "The name of the file cannot be
    /// resolved by the system"), in which case one hop still tells the truth about THIS segment and
    /// <see cref="ResolveRealPath"/>'s bounded re-walk handles the rest.
    /// </para>
    /// </remarks>
    private static FileSystemInfo? TryResolveLinkTarget(string path)
    {
        try
        {
            // Directory.Exists is a fine POSITIVE signal (it is only true for a real directory or a
            // link that resolves to one) — it just cannot be trusted as a negative. The File
            // overload handles everything else, including directory links whose target is missing:
            // measured, it resolves a Windows directory reparse point too.
            return Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.ResolveLinkTarget(path, returnFinalTarget: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No entry here at all — the segment simply does not exist yet. Left as written; see
            // ResolveRealPath's remarks on why containment must not turn a missing file into a
            // containment error.
            return null;
        }
        catch (IOException)
        {
            // The chain could not be walked to its end. One hop is still a truthful answer about
            // this segment, and any exception THIS call throws propagates to IsInsideWorkspace's
            // catch, which fails closed.
            return File.ResolveLinkTarget(path, returnFinalTarget: false);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a network/UNC location — decided PURELY by inspecting
    /// the string, never by touching the network.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> rather than private so <see cref="Workspace.Resolve"/> can apply
    /// the IDENTICAL test to the <c>--workspace</c> root itself before its one
    /// <see cref="File.Exists(string)"/> probe (a security review's MAJOR finding:
    /// <c>--workspace \\attacker\share</c> made that probe an outbound SMB/NTLM authentication at
    /// startup — the exact forced-authentication primitive this type's remarks describe, reached
    /// through the flag meant to TIGHTEN safety). Shared rather than duplicated so the two can never
    /// disagree about what counts as a network path.
    /// </remarks>
    internal static bool IsNetworkPath(string path)
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
