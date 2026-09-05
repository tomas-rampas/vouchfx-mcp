using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp;

// Vouchfx.Mcp — Workspace (Sprint 3 / US-S3-08; spec §4.2, plan §2.1).
//
// The one resolved answer to "which directory tree is this server operating on", established ONCE
// at server start from `--workspace <path>` and handed to everything that needs it. Before this
// story there was no workspace concept at all: every tool took an absolute-or-relative path and
// PathSafetyGuard checked only that it was not a UNC/network location. That is still exactly what
// happens when no `--workspace` flag is supplied — see PathSafetyGuard's remarks for why the
// containment rule this type enables is gated on workspace-configured-ness rather than switched on
// for everyone.

/// <summary>
/// The workspace this server operates on (spec §4.2): its root and the two directories derived
/// from it, resolved once at startup from <c>--workspace &lt;path&gt;</c>.
/// </summary>
/// <param name="Root">
/// The absolute, canonicalised root. Every path parameter is asserted to resolve inside this
/// directory once a workspace is configured — see
/// <see cref="Vouchfx.Mcp.Validation.PathSafetyGuard"/>.
/// </param>
/// <param name="SpecsDir">
/// Where suites live: <c>&lt;root&gt;/e2e</c>. Nothing in Sprint 3 resolves against it yet — it is
/// established here so Sprint 5's <c>vouchfx://workspace/specs</c> resource has one authoritative
/// answer rather than guessing its own.
/// </param>
/// <param name="OutputDir">
/// Where run artefacts belong: <c>&lt;root&gt;/.vouchfx/runs</c>. US-S3-01's persisted run registry
/// and US-S3-04's cross-process <c>.lock</c> file are both rooted here, and consume this value
/// rather than each separately deriving a base directory.
/// </param>
/// <param name="ConfigPath">
/// <c>&lt;root&gt;/vouchfx.config.json</c> when that file exists at resolution time,
/// <see langword="null"/> otherwise. Spec §4.2 declares it optional (<c>configPath?</c>), so
/// "absent" is a real state rather than a path to a file that may not be there. Nothing reads the
/// file yet; this only records where it is.
/// </param>
/// <remarks>
/// <para>
/// <b>Resolution is pure path computation plus one existence probe — it NEVER creates anything.</b>
/// <see cref="SpecsDir"/> and <see cref="OutputDir"/> are computed with
/// <see cref="Path.Combine(string, string)"/> and are not required to exist; the single filesystem
/// call made here is the <see cref="File.Exists(string)"/> probe behind <see cref="ConfigPath"/>,
/// which is a read. Creating <see cref="OutputDir"/> is deliberately NOT this type's job — the
/// read-only invariant (CLAUDE.md; <c>ReadOnlySourceGuardTests</c> holds it structurally against
/// <c>src/</c>) admits filesystem mutation only in the small set of types named there. Since
/// US-S3-01 that set includes <see cref="Vouchfx.Mcp.Run.FileRunRegistry"/>, which creates
/// <see cref="OutputDir"/> on the first run it records — so this type still creates nothing, and the
/// directory comes into existence when a run actually needs it rather than because a workspace was
/// merely resolved.
/// </para>
/// <para>
/// <b>A value, not a service.</b> It is immutable, cheap to pass, and carries no behaviour, so it
/// travels as a plain constructor/parameter argument through
/// <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/> rather than as a DI-resolved
/// service — the same treatment <see cref="EnginePin"/> gets, and for the same reason: both are
/// startup facts, fixed for the process's lifetime.
/// </para>
/// </remarks>
public sealed record Workspace(string Root, string SpecsDir, string OutputDir, string? ConfigPath)
{
    /// <summary>The command-line flag that configures a workspace. Absent ⇒ no workspace at all.</summary>
    public const string CommandLineFlag = "--workspace";

    /// <summary>The directory name <see cref="SpecsDir"/> appends to <see cref="Root"/> (spec §4.2).</summary>
    public const string SpecsDirectoryName = "e2e";

    /// <summary>The file name <see cref="ConfigPath"/> probes for under <see cref="Root"/> (spec §4.2).</summary>
    public const string ConfigFileName = "vouchfx.config.json";

    /// <summary>
    /// The first segment of <see cref="OutputDir"/>'s path relative to <see cref="Root"/> — spec
    /// §4.2's <c>&lt;root&gt;/.vouchfx/runs</c>, split so the separator is the platform's own rather
    /// than a hardcoded <c>/</c>.
    /// </summary>
    private const string OutputDirectoryFirstSegment = ".vouchfx";

    /// <summary>The second segment of <see cref="OutputDir"/> — see <see cref="OutputDirectoryFirstSegment"/>.</summary>
    private const string OutputDirectorySecondSegment = "runs";

    /// <summary>
    /// Why a <c>\\</c>-prefixed <c>--workspace</c> root is refused — see <see cref="Resolve"/>'s
    /// remarks. One literal, shared by the exception <see cref="Resolve"/> throws and the
    /// startup-fatal line <see cref="TryParseCommandLine"/> prints, so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// <b>The wording covers device-prefixed forms as well as genuine UNC ones, because the test
    /// does</b> (a peer review's NIT). <see cref="PathSafetyGuard.IsNetworkPath"/> refuses anything
    /// beginning <c>\\</c>, which includes the legitimate LOCAL extended-length spelling
    /// <c>\\?\C:\repo</c> and the device form <c>\\.\...</c> — neither of which touches the network.
    /// The rejection itself is deliberately left as-is: <c>\\?\UNC\host\share</c> is a genuine UNC
    /// path wearing the same prefix, so picking the forms apart would trade a startup-time
    /// inconvenience (spell the root <c>C:\repo</c>) for a credential-leak bypass. Only the MESSAGE
    /// is fixed, so it no longer tells an operator their local path is a "network location".
    /// </remarks>
    internal const string NetworkRootRejection =
        "The workspace root must be a plain local directory path. Any root beginning '\\\\' is " +
        "refused — a network/UNC location (\\\\host\\share), and equally the '\\\\?\\' and '\\\\.\\' " +
        "device-prefixed forms, since '\\\\?\\UNC\\host\\share' is a UNC path in that same spelling: " +
        "reading one triggers an outbound SMB/NTLM authentication to the host it names. Spell a " +
        "local root as an ordinary path (e.g. C:\\repo).";

    /// <summary>
    /// The prefix that makes an argument a NEAR MISS for <see cref="CommandLineFlag"/> — a typo
    /// worth refusing rather than ignoring. See <see cref="TryParseCommandLine"/>'s remarks.
    /// </summary>
    private const string NearMissFlagPrefix = "--worksp";

    /// <summary>
    /// Resolves a workspace from a root directory path, applying spec §4.2's defaults.
    /// </summary>
    /// <param name="root">
    /// The root as supplied on the command line. Made absolute against the process's current
    /// directory when relative, so <see cref="Root"/> is always absolute regardless of how the host
    /// spelled it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> is empty, begins with <c>\\</c> (a network/UNC location, or one of
    /// the device-prefixed forms that share that spelling — see <see cref="NetworkRootRejection"/>),
    /// or is not a path this platform can canonicalise. <see cref="TryParseCommandLine"/> converts
    /// this into a startup-fatal message rather than letting it escape.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>A UNC root is refused BEFORE the <see cref="File.Exists(string)"/> probe below, and that
    /// ordering is the whole point</b> (a security review's MAJOR finding). Reading — or merely
    /// probing for — a UNC path on Windows triggers an outbound SMB connection including NTLM
    /// authentication to whatever host the path names, so <c>--workspace \\attacker\share</c> turned
    /// this type's single, innocuous config probe into a credential-leaking forced authentication at
    /// server startup: the exact primitive
    /// <see cref="Vouchfx.Mcp.Validation.PathSafetyGuard"/> exists to prevent, reached through the
    /// flag whose purpose is to TIGHTEN path safety. The test is that guard's own
    /// <c>IsNetworkPath</c>, shared rather than reimplemented so the two can never disagree, and it
    /// is applied to BOTH the root as written and its canonicalised form (a relative root
    /// canonicalised against a UNC current directory would otherwise slip through the first check).
    /// It is pure string inspection and touches nothing.
    /// </para>
    /// <para>
    /// Canonicalised through <see cref="Path.GetFullPath(string)"/> then
    /// <see cref="Path.TrimEndingDirectorySeparator(string)"/>, exactly as
    /// <c>Tools/ToolMetaProvider</c> already canonicalises the base directory it reports today: one
    /// consistent shape, with no trailing separator, so the prefix comparison
    /// <see cref="Vouchfx.Mcp.Validation.PathSafetyGuard"/> makes against
    /// <see cref="Root"/> cannot be thrown off by <c>"/repo"</c> vs <c>"/repo/"</c>. A genuine
    /// filesystem root (<c>C:\</c>, <c>/</c>) is deliberately left alone —
    /// <see cref="Path.TrimEndingDirectorySeparator(string)"/> does not trim one.
    /// </para>
    /// </remarks>
    public static Workspace Resolve(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (PathSafetyGuard.IsNetworkPath(root))
        {
            throw new ArgumentException(NetworkRootRejection, nameof(root));
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (PathSafetyGuard.IsNetworkPath(canonicalRoot))
        {
            throw new ArgumentException(NetworkRootRejection, nameof(root));
        }

        var configPath = Path.Combine(canonicalRoot, ConfigFileName);

        return new Workspace(
            canonicalRoot,
            Path.Combine(canonicalRoot, SpecsDirectoryName),
            Path.Combine(canonicalRoot, OutputDirectoryFirstSegment, OutputDirectorySecondSegment),

            // A read, never a write — and the ONLY filesystem call this whole type makes. `null`
            // when absent rather than a path to a file that is not there, because spec §4.2 types
            // the field as optional and a host must be able to tell "no config" from "config here".
            File.Exists(configPath) ? configPath : null);
    }

    /// <summary>
    /// Extracts the workspace, if any, from this process's command line.
    /// </summary>
    /// <param name="args">The raw process arguments (server mode only — see <c>Program.cs</c>).</param>
    /// <param name="workspace">
    /// The resolved workspace, or <see langword="null"/> when <see cref="CommandLineFlag"/> did not
    /// appear at all. <b>Null is a first-class, fully supported mode</b>, not a degraded one: it
    /// means every path behaves exactly as it did before this story.
    /// </param>
    /// <param name="error">
    /// A ready-to-print, sanitised one-line reason the flag could not be honoured; non-null only
    /// when this method returns <see langword="false"/>.
    /// </param>
    /// <returns><see langword="false"/> only when the flag WAS supplied but could not be honoured.</returns>
    /// <remarks>
    /// <para>
    /// <b>Fail closed, never fail quiet.</b> A <c>--workspace</c> whose value is missing, blank, or
    /// unusable is startup-fatal rather than ignored: silently dropping it would leave the server
    /// running with containment OFF while the host believes it is ON, which is the one failure mode
    /// a security-relevant flag must not have. That is also why both spellings
    /// (<c>--workspace &lt;path&gt;</c> and <c>--workspace=&lt;path&gt;</c>) are accepted and why a
    /// REPEATED flag is rejected rather than resolved last-wins — an ambiguous root is not a root.
    /// </para>
    /// <para>
    /// A next argument beginning with <c>-</c> is treated as a MISSING value, not as a path: it is
    /// far more likely to be the next flag (<c>--workspace --verbose</c>) than a directory whose
    /// name starts with a dash, and reading it as a path would silently root the workspace somewhere
    /// nobody asked for. Same reasoning as <c>RunSuiteOrchestrator</c>'s leading-dash rejection of a
    /// suite path.
    /// </para>
    /// <para>
    /// <b>A NEAR MISS is startup-fatal too</b> (a peer review's MAJOR finding). Before this, a
    /// misspelled <c>--workspce /repo</c> fell through the loop untouched and the server came up with
    /// containment OFF while the operator believed it was ON — the same silent-degradation failure
    /// the fail-closed rule above exists to prevent, arrived at by a typo rather than a bad value.
    /// So any argument beginning <c>--worksp</c> (case-insensitively, which also catches
    /// <c>--WORKSPACE</c> and <c>--Workspace=/repo</c>) that is not EXACTLY
    /// <c>--workspace</c>/<c>--workspace=…</c> is refused with a "did you mean" line. Deliberately
    /// NARROW: the rule is <b>any argument beginning <c>--worksp</c></b> — not "misspellings of this
    /// flag, recognised as such" — so <c>--workspaces</c> and <c>--workspce</c> are both refused
    /// while <c>--verbose</c> is not looked at. General unknown arguments belong to
    /// <c>Host.CreateApplicationBuilder</c>, which is handed the same <paramref name="args"/>, and
    /// stealing that job here would make this method the arbiter of every flag the host stack
    /// understands. The prefix is the whole test; nothing here measures edit distance or knows what a
    /// typo is.
    /// </para>
    /// <para>
    /// <b>Failure-arm ordering is deliberate</b> (a peer review's NIT): the missing-value complaint
    /// is checked BEFORE the supplied-more-than-once one, so <c>--workspace /repo --workspace</c>
    /// reports the trailing flag's missing value — the actionable fact — rather than a
    /// "supplied more than once" that is true but does not name what to fix.
    /// </para>
    /// </remarks>
    public static bool TryParseCommandLine(IReadOnlyList<string> args, out Workspace? workspace, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        workspace = null;
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            string? value;

            if (IsNearMissFlag(argument))
            {
                workspace = null;
                error =
                    $"'{VfxCode.SanitiseForEcho(argument)}' is not a recognised flag — did you mean "
                    + $"{CommandLineFlag}? Refused rather than ignored, so a typo cannot leave this "
                    + "server running with path containment silently off.";
                return false;
            }

            if (string.Equals(argument, CommandLineFlag, StringComparison.Ordinal))
            {
                var next = i + 1 < args.Count ? args[i + 1] : null;
                value = next is not null && next.StartsWith('-') ? null : next;
                i++;
            }
            else if (argument.StartsWith(CommandLineFlag + "=", StringComparison.Ordinal))
            {
                value = argument[(CommandLineFlag.Length + 1)..];
            }
            else
            {
                continue;
            }

            // Every failure arm below clears `workspace` before returning: a caller that ignores the
            // false return and reads the out parameter anyway must not receive a half-resolved
            // workspace built from the FIRST of two contradictory flags.
            //
            // The missing-value arm comes FIRST — see this method's remarks. A trailing bare
            // `--workspace` after a good one is both "no value" and "twice"; naming the missing value
            // is the message that tells the operator what to type.
            if (string.IsNullOrWhiteSpace(value))
            {
                workspace = null;
                error = $"{CommandLineFlag} requires a directory path, e.g. {CommandLineFlag} /path/to/repo.";
                return false;
            }

            if (workspace is not null)
            {
                workspace = null;
                error = $"{CommandLineFlag} was supplied more than once. Supply it at most once.";
                return false;
            }

            // Checked HERE as well as inside Resolve — not redundantly, but so the operator is told
            // WHY. Resolve's throw would otherwise reach the generic catch below and be reported as
            // "not a usable directory path (ArgumentException)", which is true and useless. Pure
            // string inspection either way; no filesystem call has happened yet on this path.
            if (PathSafetyGuard.IsNetworkPath(value))
            {
                error =
                    $"{CommandLineFlag} value is not usable: '{VfxCode.SanitiseForEcho(value)}'. "
                    + NetworkRootRejection;
                return false;
            }

            try
            {
                workspace = Resolve(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // SanitiseForEcho, not SanitiseForDisplay: this echoes a raw command-line token back
                // to a terminal, which is exactly what that helper's 64-character cap plus control
                // -character escaping exists for — the same choice Program.cs's validate-worker
                // argument rejection makes. The exception's own Message is deliberately NOT
                // forwarded: BCL path exceptions routinely embed a full filesystem path that a
                // control-character-only sanitiser would pass straight through (see
                // PinFailureReporting's message-forwarding policy).
                error =
                    $"{CommandLineFlag} value is not a usable directory path: "
                    + $"'{VfxCode.SanitiseForEcho(value)}' ({ex.GetType().Name}).";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="argument"/> is a misspelling of <see cref="CommandLineFlag"/> close
    /// enough to be a typo rather than somebody else's flag — see
    /// <see cref="TryParseCommandLine"/>'s remarks for why that is startup-fatal.
    /// </summary>
    /// <remarks>
    /// The two EXACT spellings are excluded first and ORDINALLY, so this can never fire on the real
    /// flag: a case-insensitive <c>--Workspace=/repo</c> is a near miss precisely BECAUSE the parse
    /// above accepts only the ordinal spelling, and silently ignoring it would be the degradation
    /// this check exists to stop.
    /// </remarks>
    private static bool IsNearMissFlag(string argument) =>
        !string.Equals(argument, CommandLineFlag, StringComparison.Ordinal)
        && !argument.StartsWith(CommandLineFlag + "=", StringComparison.Ordinal)
        && argument.StartsWith(NearMissFlagPrefix, StringComparison.OrdinalIgnoreCase);
}
