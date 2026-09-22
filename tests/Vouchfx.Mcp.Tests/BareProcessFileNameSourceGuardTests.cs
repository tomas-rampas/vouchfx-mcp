using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for CWE-427 (uncontrolled search path element) at every
/// process-spawn site in this repository — <c>src/</c> AND <c>tests/</c>, unlike
/// <see cref="SecretHygieneSourceGuardTests"/>'s environment-mutation guard, which is <c>src/</c>-only
/// by design (see that class's own remarks). Mirrors <see cref="SecretHygieneSourceGuardTests"/>'s
/// structural-guard pattern otherwise: real files read from the checked-out repo, not the built
/// assembly, because this is a static property of the SOURCE.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b>
/// <see cref="SecretHygieneSourceGuardTests.ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet"/>
/// enumerates <c>src/**/*.cs</c> only — deliberately, since its own concern (an explicit environment
/// dictionary) is a <c>src/</c>-only risk. But a BARE <see cref="System.Diagnostics.ProcessStartInfo.FileName"/>
/// is just as real a risk in a test helper that spawns a real OS process, and one shipped: commit
/// c6734d8 (#68) replaced <c>FileName = "vouchfx"</c> in <c>RealValidateAgainstPinnedCliTests.cs</c>
/// with <c>VouchfxCliPathResolver.ResolveAbsolutePath()</c> after review — a <c>tests/</c> file, so
/// the <c>src/</c>-only guard above was never in a position to have caught either the original defect
/// or its reappearance. This guard covers both trees so that gap cannot recur.
/// </para>
/// <para>
/// <b>What counts as "bare".</b> A <see cref="System.Diagnostics.ProcessStartInfo.FileName"/> (or the
/// equivalent constructor argument) assigned a string literal containing NEITHER <c>/</c> NOR
/// <c>\</c> — i.e. one <see cref="System.Diagnostics.Process"/> can only resolve it by searching
/// <c>PATH</c>, in whatever order the host process happens to have it, rather than by an explicit
/// relative or absolute location. See <see cref="Vouchfx.Mcp.Cli.VouchfxCliPathResolver"/>'s own
/// remarks for the full CWE-427 threat model this guards against. This repo's real spawn sites in
/// <c>src/</c> never do this: <c>VouchfxCliProcessRunner</c> and <c>VouchfxCliSuiteRunner</c> assign
/// an already-resolved absolute path from <c>VouchfxCliPathResolver</c>, and
/// <c>ValidationWorkerClient</c>/<c>SpecIndexWorkerClient</c> re-invoke THIS process via
/// <see cref="Environment.ProcessPath"/> or <see cref="System.Reflection.Assembly.Location"/> — never
/// a literal. Only <c>tests/</c> files assign a bare literal today, and only for two trusted names
/// (<c>dotnet</c> and <c>git</c>) — see <see cref="AllowedBareFileNames"/>.
/// </para>
/// <para>
/// <b>Two shapes, matching the review finding exactly:</b> an initializer/assignment
/// <c>FileName = "literal"</c>, and a constructor argument <c>new ProcessStartInfo("literal", ...)</c>.
/// Both patterns tolerate a leading <c>@</c> (a verbatim string) and arbitrary whitespace around the
/// <c>=</c>/<c>(</c>, but — like <see cref="SecretHygieneSourceGuardTests"/>'s own patterns — require
/// the literal member/type name adjacent, which prose in a doc comment does not produce. Confirmed
/// against this repository: no <c>Process.Start(string, ...)</c> convenience-overload call and no
/// raw-string (<c>"""…"""</c>) <c>FileName</c> literal appears anywhere in <c>src/</c> or
/// <c>tests/</c>, so these two shapes are exhaustive over what exists today; widen this guard first if
/// either ever appears.
/// </para>
/// <para>
/// <b>Not traced: an intermediate variable.</b> <c>var f = "vouchfx"; startInfo.FileName = f;</c>
/// would not be caught — this guard reads the literal at the assignment/constructor site itself, not
/// through a local binding. Every real site in this repo assigns its literal (or its resolved path)
/// inline, so this is not a gap against the actual codebase today; it is named here rather than
/// silently assumed away.
/// </para>
/// <para>
/// <b>Why the allowlist is keyed on <c>(file, literal)</c>, not on the literal alone.</b>
/// <c>dotnet</c> and <c>git</c> are standard toolchain binaries this repo already trusts
/// unconditionally by bare name (CI installs and invokes both that way; <c>global.json</c> pins the
/// SDK's BEHAVIOUR, not its location). <c>cmd.exe</c> is deliberately NOT in that set: a bare
/// <c>"cmd.exe"</c> is resolved by <c>CreateProcess</c>, which searches the application's own
/// directory and the current directory BEFORE <c>%SystemRoot%\System32</c>, so the one Windows
/// shell spawn in this repository names <c>Path.Combine(Environment.SystemDirectory, "cmd.exe")</c>
/// instead (an earlier version of this list trusted the bare name on the opposite belief).
/// <c>vouchfx</c> — the one
/// binary this project wraps, whose IDENTITY (not merely its behaviour) this server's own invariants
/// depend on (<c>ENGINE_PIN</c>'s SHA gate, the <c>*AgainstPinnedCliTests</c> parity oracles) — is
/// deliberately absent from that trusted set, so a future bare <c>FileName = "vouchfx"</c> anywhere
/// fails this guard exactly as the one commit c6734d8 fixed should have. Keying on
/// <c>(file, literal)</c> rather than the literal alone means even a legitimate <c>"dotnet"</c> spawn
/// in a brand-new file needs a one-line entry here first — fail-closed the way
/// <see cref="SecretHygieneSourceGuardTests"/>'s own completeness check is, never a blanket exemption
/// for the two trusted names wherever they might appear.
/// </para>
/// <para>
/// <b>Scanning primitive: <see cref="SourceGuardScan.SourceWithCommentsStrippedOnly"/></b>, not
/// <see cref="SourceGuardScan.ExecutableSourceOf"/> — the latter blanks string literals along with
/// comments, which would blank the very text this guard reads. The former blanks only comments, so a
/// doc comment merely DESCRIBING this shape (as this file's own remarks do, in prose) cannot trip it,
/// while every real literal stays visible.
/// </para>
/// </remarks>
public class BareProcessFileNameSourceGuardTests
{
    /// <summary>
    /// Matches <c>FileName = "literal"</c> (object-initializer or plain assignment; an optional
    /// leading <c>@</c> for a verbatim string), capturing the literal. The character class excludes
    /// <c>"</c>, <c>\</c> and <c>/</c>, so this matches ONLY when the literal is bare — carries no
    /// directory separator at all.
    /// </summary>
    private static readonly Regex BareFileNameAssignmentPattern = new(
        @"\bFileName\s*=\s*@?""(?<literal>[^""\\/]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>new ProcessStartInfo("literal", ...)</c> (optionally <c>global::</c>- and/or
    /// <c>System.Diagnostics.</c>-qualified, with arbitrary whitespace between every token), capturing
    /// the literal under the identical bareness rule as <see cref="BareFileNameAssignmentPattern"/>.
    /// </summary>
    private static readonly Regex BareProcessStartInfoConstructorPattern = new(
        @"new\s+(global::)?(System\s*\.\s*Diagnostics\s*\.\s*)?ProcessStartInfo\s*\(\s*@?""(?<literal>[^""\\/]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// The fail-closed allowlist: every <c>(file, literal)</c> pair this guard permits, each with the
    /// one-line reason review asked for.
    /// <see cref="EveryBareFileNameLiteral_ExactlyMatchesTheAllowlist"/> asserts this is not just a
    /// superset but an EXACT match of what is really in <c>src/</c> and <c>tests/</c> today — an entry
    /// here for a literal that no longer appears fails the test just as loudly as a new literal
    /// missing from it, so the list cannot quietly rot either way.
    /// </summary>
    private static readonly (string RelativePath, string Literal, string Reason)[] AllowedBareFileNames =
    [
        ("tests/Vouchfx.Mcp.Tests/RealServerProcessTests.cs", "dotnet",
            "spawns the real built Vouchfx.Mcp.dll via the SDK muxer (RepoLayout.ResolveServerDllPath)"),
        ("tests/Vouchfx.Mcp.Tests/RealValidationWorkerProcessTests.cs", "dotnet",
            "spawns the real built Vouchfx.Mcp.dll (in --validate-worker / --spec-index-worker mode) via the SDK muxer"),
        ("tests/Vouchfx.Mcp.Tests/RealWorkspaceProcessTests.cs", "dotnet",
            "spawns the real built Vouchfx.Mcp.dll via the SDK muxer"),
        ("tests/Vouchfx.Mcp.Tests/RealHttpTransportProcessTests.cs", "dotnet",
            "spawns the real built Vouchfx.Mcp.dll over the HTTP transport via the SDK muxer"),
        ("tests/Vouchfx.Mcp.Tests/RealCrossProcessRunLockTests.cs", "dotnet",
            "spawns the Vouchfx.Mcp.Tests.RunLockHolderFixture.dll fixture via the SDK muxer"),
        ("tests/Vouchfx.Mcp.Tests/Run/RunSuiteOrchestratorTests.cs", "dotnet",
            "spawns the Vouchfx.Mcp.Tests.StdinEofChildFixture.dll fixture via the SDK muxer"),
        ("tests/Vouchfx.Mcp.Tests/Run/VouchfxCliSuiteRunnerTests.cs", "dotnet",
            "spawns the Vouchfx.Mcp.Tests.StdinEofChildFixture.dll fixture via the SDK muxer"),
        ("tests/Vouchfx.Mcp.Tests/LockFileCoverageSourceGuardTests.cs", "git",
            "runs `git ls-files` against this checkout to enumerate tracked .csproj/packages.lock.json files"),
        ("tests/Vouchfx.Mcp.Tests/RealValidateAgainstPinnedCliTests.cs", "git",
            "runs `git show`/`git ls-files` against a sibling vouchfx engine checkout to extract its rejected corpus"),
    ];

    /// <summary>Every <c>(file, literal)</c> pair either pattern matches, scanned across BOTH <c>src/</c> and <c>tests/</c>.</summary>
    private static IEnumerable<(string RelativePath, string Literal)> BareFileNameSitesIn(IEnumerable<string> files)
    {
        foreach (var path in files)
        {
            var source = SourceGuardScan.SourceWithCommentsStrippedOnly(path);
            var relative = SourceGuardScan.ToRepoRelativeForwardSlashPath(path);

            foreach (Match match in BareFileNameAssignmentPattern.Matches(source))
            {
                yield return (relative, match.Groups["literal"].Value);
            }

            foreach (Match match in BareProcessStartInfoConstructorPattern.Matches(source))
            {
                yield return (relative, match.Groups["literal"].Value);
            }
        }
    }

    [Fact]
    public void EveryBareFileNameLiteral_ExactlyMatchesTheAllowlist()
    {
        var files = SourceGuardScan.SourceFilesInSrc().Concat(SourceGuardScan.SourceFilesInTests());

        var actual = BareFileNameSitesIn(files)
            .Distinct()
            .OrderBy(site => site.RelativePath, StringComparer.Ordinal)
            .ThenBy(site => site.Literal, StringComparer.Ordinal)
            .ToArray();

        var expected = AllowedBareFileNames
            .Select(entry => (entry.RelativePath, entry.Literal))
            .OrderBy(site => site.RelativePath, StringComparer.Ordinal)
            .ThenBy(site => site.Literal, StringComparer.Ordinal)
            .ToArray();

        // Not a subset/superset check: EXACT equality. A new bare FileName anywhere in src/ or
        // tests/ — including a reintroduced bare "vouchfx" — fails here until it is either fixed
        // (resolve the path explicitly) or added to AllowedBareFileNames with a reason; a stale
        // allowlist entry for a literal that no longer appears fails here too.
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoSourceFile_AssignsABareVouchfxFileName()
    {
        // Belt-and-braces on top of the exact-equality check above: names the one literal this guard
        // exists for specifically, so a failure here reads as "the vouchfx binary is being spawned by
        // bare name again" rather than a generic allowlist-diff, and it is fail-closed even if
        // AllowedBareFileNames were ever (wrongly) extended to include it.
        var files = SourceGuardScan.SourceFilesInSrc().Concat(SourceGuardScan.SourceFilesInTests());

        var vouchfxSites = BareFileNameSitesIn(files)
            .Where(site => string.Equals(site.Literal, "vouchfx", StringComparison.Ordinal))
            .Select(site => site.RelativePath)
            .ToArray();

        Assert.True(
            vouchfxSites.Length == 0,
            "Bare 'vouchfx' FileName assignment(s) found (CWE-427) in: " +
            string.Join(", ", vouchfxSites) +
            " — resolve explicitly via VouchfxCliPathResolver.ResolveAbsolutePath() instead, as " +
            "commit c6734d8 (#68) did for this exact shape.");
    }
}
