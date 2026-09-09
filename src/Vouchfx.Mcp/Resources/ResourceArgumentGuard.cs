using ModelContextProtocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Resources;

// Vouchfx.Mcp.Resources — the one gate every URI-template argument passes through
// (Sprint 5 / US-S5-01 AC-008).
//
// "EVERY" IS LITERAL, AND IT COVERS BOTH SCHEMES. A security review found the errors catalogue
// bypassing this type while this header claimed universality, so the bypass was closed rather than
// the claim narrowed: DiagnosticResourceRegistry routes its {code} through Require() too, which means
// the Sprint 1 vouchfx-docs:///errors/{code} URI and its Sprint 5 vouchfx:// alias apply identical
// admission rules — as they must, since they serve identical bytes. A future template that skips this
// gate makes the sentence above false again; add the call, do not edit the sentence.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY A URI TEMPLATE ARGUMENT NEEDS THE SAME TREATMENT A TOOL ARGUMENT DOES
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// A `{runId}`/`{container}`/`{name}`/`{version}` expansion is CALLER-SUPPLIED TEXT arriving over
// the wire, exactly like a tool's `path` argument — the only difference is the envelope it rides
// in. Sprint 3's path guards therefore apply unchanged, and the AC says so in as many words: a
// UNC/network-shaped value is "rejected the same way PathSafetyGuard already rejects one elsewhere
// in this codebase (reused, not reinvented)".
//
// The threat is concrete rather than theoretical. `vouchfx://runs/%5C%5Cattacker%5Cshare/events`
// expands `{runId}` to `\\attacker\share`; every downstream orchestrator then hands that string to
// the run registry, and a FILE-BACKED registry composes a directory path from a run id. Reading —
// or merely probing — a UNC path on Windows triggers an outbound SMB connection including NTLM
// authentication to the named host, which is the forced-authentication credential leak
// PathSafetyGuard's own remarks describe. Refusing the SHAPE before it reaches any lookup is the
// same fail-closed ordering Workspace.Resolve applies to --workspace, for the same reason.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY REJECTION IS AN McpException RATHER THAN A VfxError
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// The VFX-* envelope (Contracts/VfxError) is a TOOL-RESULT contract — spec §4.4 shapes it as the
// body of an `isError: true` CallToolResult, and StructuredToolResult.Error is the single pathway
// that produces one. `resources/read` has no such envelope: its failure channel is a JSON-RPC
// protocol error, which the SDK produces from a thrown McpException. That is the pattern
// DiagnosticResourceRegistry already established in Sprint 1 ("the SDK's own documented pattern for
// a template resource whose parameter does not resolve"), and this type follows it rather than
// inventing a second failure shape for the same server.
//
// What IS shared with the tool path is the MESSAGE TEXT wherever a condition is already catalogued
// — an unknown runId reuses RunIdArgument.DescribeMissingRun verbatim, so a host reading the
// tool's refusal and the resource's refusal reads one fact. See RunResourceRegistry.

/// <summary>
/// Validates one <c>vouchfx://</c> URI-template argument before it reaches anything that resolves,
/// enumerates, or looks it up — throwing <see cref="McpException"/> on refusal.
/// </summary>
public static class ResourceArgumentGuard
{
    /// <summary>
    /// The longest a template argument may be. Well below every downstream limit
    /// (<c>RunLifecycleLimits.MaxRunIdChars</c>, <c>GetRunArtifactsOrchestrator.MaxContainerChars</c>),
    /// because this guard's job is to stop an absurd value from reaching those checks at all rather
    /// than to restate them.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT derived from the downstream constants: those bound what a valid value may be
    /// and would each need a different number here, whereas this bounds what is worth PARSING. A
    /// 200,000-character run id is not a run id under any of them, and capping it once — before the
    /// sanitisation that would otherwise walk every character of it — is what keeps a hostile URI
    /// cheap to refuse. The same reasoning as <c>PathSafetyGuard.MaxDisplayedPathChars</c>.
    /// </remarks>
    public const int MaxArgumentChars = 512;

    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it is an acceptable template argument, and
    /// throws otherwise.
    /// </summary>
    /// <param name="value">The raw expansion, as the SDK bound it from the request URI.</param>
    /// <param name="parameterName">
    /// The template placeholder's own name (<c>runId</c>, <c>container</c>, …) — spliced into the
    /// refusal so a host is told WHICH segment it got wrong.
    /// </param>
    /// <exception cref="McpException">
    /// <paramref name="value"/> is null/blank, longer than <see cref="MaxArgumentChars"/>, names a
    /// network/UNC location, or contains a path separator or a <c>..</c> traversal segment.
    /// </exception>
    public static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException(
                $"The '{{{parameterName}}}' segment of this resource URI is required and must not be blank.");
        }

        if (value.Length > MaxArgumentChars)
        {
            throw new McpException(
                $"The '{{{parameterName}}}' segment of this resource URI must be at most "
                + $"{MaxArgumentChars} characters.");
        }

        // The UNCONDITIONAL half of PathSafetyGuard's contract — the same string-only test, shared
        // rather than reimplemented, so the two can never disagree about what a network path is. It
        // touches nothing and runs before any lookup, which is the ordering that makes it a guard
        // rather than a report.
        if (PathSafetyGuard.IsNetworkPath(value))
        {
            throw new McpException(
                $"The '{{{parameterName}}}' segment of this resource URI must not name a network/UNC "
                + $"location: '{VfxCode.SanitiseForEcho(value)}'.");
        }

        // A separator or a traversal segment in a single URI SEGMENT is never legitimate for any of
        // this server's template arguments — a run id, a container name, an example name and a
        // schema version are all flat identifiers. Refused here rather than left to whichever
        // downstream component happens to compose a path from it, because "which component composes
        // a path" is exactly the kind of fact that changes without this file being reread.
        //
        // Both separators are tested on every platform: on Unix a backslash is an ordinary filename
        // character and would sail past a platform-conditional check, then be interpreted as a
        // separator by a Windows host reading the same registry directory.
        if (value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || ContainsTraversalSegment(value))
        {
            throw new McpException(
                $"The '{{{parameterName}}}' segment of this resource URI must be a single flat "
                + "identifier — no path separators and no '..' traversal: "
                + $"'{VfxCode.SanitiseForEcho(value)}'.");
        }

        return value;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is, or contains, a <c>..</c> traversal segment.
    /// </summary>
    /// <remarks>
    /// Checked as a WHOLE-STRING equality rather than a substring search, because the separator test
    /// above has already established there are no separators — so the only traversal shape that can
    /// still be present is the value being <c>..</c> (or <c>.</c>) in its entirety. A substring test
    /// would additionally reject the perfectly ordinary <c>run..2026</c>, which is not a traversal
    /// and which nothing here has a reason to refuse.
    /// </remarks>
    private static bool ContainsTraversalSegment(string value) =>
        string.Equals(value, "..", StringComparison.Ordinal)
        || string.Equals(value, ".", StringComparison.Ordinal);
}
