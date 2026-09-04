using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// Resolves <c>validate_suite</c>'s three input arguments — <c>path</c>, <c>yaml</c>, <c>level</c> —
/// into the pair the pipeline actually needs, or the <see cref="VfxError"/> explaining why it
/// cannot (Sprint 2 / US-S2-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the tool handler because it is a decision, not plumbing.</b> "Exactly one of
/// <c>path</c>/<c>yaml</c>" is the one rule in this tool that no schema keyword enforces and no
/// downstream stage can recover from, and it has four outcomes (path, yaml, both, neither) plus the
/// worker-marker collision below and a level to parse. Proving those through an MCP round trip would
/// additionally spawn a worker process per case, to establish nothing the arguments do not already
/// determine.
/// </para>
/// <para>
/// <b>Nothing here touches the filesystem or spawns anything.</b> A resolved
/// <see cref="SuiteSource"/> is a statement about the ARGUMENTS only — whether the path exists, and
/// whether the YAML parses, are the pipeline's questions, answered behind the process-isolation
/// boundary where a hostile answer cannot hurt.
/// </para>
/// </remarks>
internal static class ValidateSuiteInput
{
    /// <summary>What a successful resolution produced.</summary>
    /// <param name="Source">The suite to analyse — a file path or inline YAML text.</param>
    /// <param name="Level">Which passes to run.</param>
    public readonly record struct Resolved(SuiteSource Source, ValidationLevel Level);

    /// <summary>
    /// Resolves the arguments, or explains the refusal.
    /// </summary>
    /// <param name="path">The <c>path</c> argument as the caller sent it, or <see langword="null"/>.</param>
    /// <param name="yaml">The <c>yaml</c> argument as the caller sent it, or <see langword="null"/>.</param>
    /// <param name="level">The <c>level</c> argument as the caller sent it, or <see langword="null"/> for the default.</param>
    /// <param name="resolved">The resolved source and level; meaningful only when this returns <see langword="true"/>.</param>
    /// <param name="error">The refusal, or <see langword="null"/> on success.</param>
    /// <remarks>
    /// <b>Supplied means "not null", nothing cleverer.</b> A caller that sends <c>path: ""</c> has
    /// supplied a path — a useless one, which the pipeline reports as VFX-E-1002 exactly as it
    /// always has. Treating blank as absent would silently reinterpret an argument the caller wrote,
    /// and would make <c>path: ""</c> with no <c>yaml</c> a different error than it is today for no
    /// reason a caller could predict.
    /// </remarks>
    public static bool TryResolve(
        string? path,
        string? yaml,
        string? level,
        out Resolved resolved,
        out VfxError? error)
    {
        resolved = default;

        // The source is settled BEFORE the level, because a call that names no suite (or two) has
        // nothing for a level to apply to — reporting the level first would send a caller to fix the
        // less fundamental of two problems.
        if (path is not null && yaml is not null)
        {
            error = VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.AmbiguousSuiteInput,
                "validate_suite was given both 'path' and 'yaml'. Supply exactly one: 'path' to "
                + "validate a suite file on disk, or 'yaml' to validate suite text directly.");
            return false;
        }

        if (path is null && yaml is null)
        {
            error = VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.AmbiguousSuiteInput,
                "validate_suite was given neither 'path' nor 'yaml'. Supply exactly one: 'path' to "
                + "validate a suite file on disk, or 'yaml' to validate suite text directly.");
            return false;
        }

        // Distinct messages for the two shapes above, one code: they are the same condition (the
        // call does not identify exactly one suite) with the same remedy, so a host keying off the
        // code acts identically — but a human reading the message should not have to work out which
        // half of the rule they broke.

        // The worker's <source> argument is an IN-BAND discriminator: the literal
        // "--yaml-stdin" in the path position means "the suite text is arriving on stdin"
        // (ValidationWorkerProtocol.InlineYamlArgument). A caller who names a real file with exactly
        // that name would therefore have it silently reinterpreted — the worker would never open the
        // file, would read an empty stdin, and would answer VFX-D-1102 ("the document is empty")
        // about a file it never looked at. Refused HERE, at the tool boundary, because this is the
        // only layer that still knows the caller meant a PATH; by the time the argument is built the
        // two are indistinguishable by construction. Same code as the other two shapes: the call
        // does not identify exactly one suite in a way this server can act on, and the remedy is
        // again an argument change.
        if (string.Equals(path, ValidationWorkerProtocol.InlineYamlArgument, StringComparison.Ordinal))
        {
            error = VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.AmbiguousSuiteInput,
                $"validate_suite cannot take '{ValidationWorkerProtocol.InlineYamlArgument}' as a "
                + "'path': that exact name collides with the internal marker this server uses to say "
                + "\"the suite text is on stdin\", so the file would never be read. Rename the file, "
                + "pass it by a different path (for example './"
                + ValidationWorkerProtocol.InlineYamlArgument + "'), or send its text as 'yaml'.");
            return false;
        }

        var effectiveLevel = ValidationLevels.Default;
        if (level is not null && !ValidationLevels.TryParse(level, out effectiveLevel))
        {
            error = VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.InvalidToolArgument,
                $"validate_suite's 'level' must be one of: {string.Join(", ", ValidationLevels.All)} "
                + $"(case-sensitive). Got: '{VfxCode.SanitiseForEcho(level)}'.");
            return false;
        }

        resolved = new Resolved(
            path is not null ? SuiteSource.FromPath(path) : SuiteSource.FromInlineYaml(yaml!),
            effectiveLevel);
        error = null;
        return true;
    }
}
