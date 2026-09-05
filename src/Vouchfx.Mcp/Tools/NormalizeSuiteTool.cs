using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>normalize_suite</c> tool (Sprint 2 / US-S2-04): returns a suite's canonical text and its
/// full validation result to the HOST. <b>This server never writes the file.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>This is plan D3's read-only replacement for the spec's dropped <c>write_spec</c>.</b> The
/// spec asked for a tool that writes a suite to disk; this repository's governing read-only
/// invariant (CLAUDE.md; plan §2.7 invariant 5) says no tool ever writes, modifies, or deletes a
/// suite file. Rather than carve an exception into the invariant, the capability was split: this
/// server produces the bytes and says whether they are valid, and the host — which already has file
/// access, already shows the user a diff, and is already the thing the user authorised to edit their
/// repository — decides whether and where to write them. Nothing is lost except the server's ability
/// to surprise someone.
/// </para>
/// <para>
/// <b>Why the tool is worth having even though normalization is opt-in.</b> Without
/// <c>normalize: true</c> this returns exactly what <c>validate_suite</c> at level <c>full</c>
/// returns, wrapped. That is the honest default given the measured comment loss (see
/// <see cref="SuiteNormalizer"/>): a caller that has not said "I accept losing my comments" gets a
/// verdict, not a rewrite of their file.
/// </para>
/// <para>
/// <b>Belongs to the CLI-FREE class</b> alongside <see cref="ValidateSuiteTool"/>,
/// <see cref="SearchDocsTool"/>, and <see cref="ExplainDiagnosticTool"/> — no engine install, no
/// network, no Docker — and shares that class's process-isolation boundary in full: the suite is
/// parsed inside the spawned <c>--validate-worker</c> child, under the same wall clock and the same
/// whole-tree kill, whether it arrived as a path or as inline text.
/// </para>
/// <para>
/// <b>Always validates at <see cref="ValidationLevel.Full"/>, and there is no <c>level</c>
/// argument.</b> A caller could otherwise ask for <c>schema</c> and receive canonical text for a
/// suite whose embedded AWS key the semantic pass never looked for — silently turning off the
/// VFX-D-1207 check on the one result a host is invited to write over the author's file. That gate
/// is structural here, not a rule to remember: the diagnostic appears because the full pass ran, and
/// nothing in this tool can arrange for it not to.
/// </para>
/// </remarks>
internal static class NormalizeSuiteTool
{
    public const string Name = "normalize_suite";

    private const string Description =
        "Returns the CANONICAL formatting of a vouchfx .e2e.yaml suite — stable key order taken " +
        "from the engine's own JSON Schema, one consistent quoting and block-layout style — " +
        "together with the same full validation result validate_suite returns for it. " +
        "Supply EXACTLY ONE of 'path' (a suite file on disk) or 'yaml' (suite text inline); both, " +
        "or neither, is a tool error. " +
        "THIS SERVER NEVER WRITES ANYTHING. The canonical text comes back to you as a string and " +
        "nothing on disk is touched — read, modified, or deleted. If the suite should be " +
        "reformatted, YOU write the returned text, after showing the user what would change. " +
        "IMPORTANT — NORMALIZATION DISCARDS ALL COMMENTS. The YAML library this server is pinned " +
        "to cannot carry comments through a re-serialisation, so every '#' comment in the suite is " +
        "lost from the returned text. For that reason normalization is OPT-IN: set 'normalize' to " +
        "true to receive it. Left at its default (false), 'normalizedYaml' is null and only the " +
        "validation result comes back. Do not set it without the user's agreement on a commented " +
        "suite, and diff before writing. The result says so too: 'commentsDropped' is true on " +
        "exactly the responses that carry canonical text, so the loss is on the payload and not " +
        "only in this description. " +
        "Formatting only: the text is otherwise the author's own document — step order never " +
        "changes, no value is edited, and anchors and aliases are preserved rather than expanded " +
        "(an '&anchor' definition travels with its node, so reordering can move which key carries " +
        "it; the '*alias' still points at the same node). Key ORDER within a mapping changes only " +
        "where the engine's schema actually describes that mapping: a mapping of your own data — " +
        "request headers, a JSON body, variables, the services map, captures — is left in the order " +
        "you wrote it, even when one of its keys happens to share a name with a schema field. " +
        "Quoting is normalised, never retyped: single quotes become double quotes, and a value the " +
        "emitter cannot write in the style it was authored in (measured: text outside the Basic " +
        "Multilingual Plane, such as emoji, written unquoted) comes back double-quoted with escapes " +
        "— the VALUE is always identical, only its spelling changes. Any value the suite hard-codes " +
        "stays exactly as written, INCLUDING a secret literal: this server does not redact, it " +
        "reports (see VFX-D-1207 in 'validation.semanticDiagnostics'). " +
        "The canonical text is PROVED before it is returned — it must parse back to the same " +
        "document. On the rare shape this server's emitter cannot render faithfully (an alias used " +
        "as a mapping KEY is the known one), you get 'normalizedYaml': null and a " +
        "'normalizationRefused' reason instead of text that would corrupt the file; the validation " +
        "result is unaffected and still complete. Never write a suite from a response whose " +
        "'normalizationRefused' is non-null — there is nothing to write. " +
        "Validation always runs at level 'full' — both the JSON Schema pass and the semantic pass — " +
        "so 'validation' is the complete validate_suite payload (valid, errors, " +
        "semanticDiagnostics, semanticDiagnosticsTruncated, summary, level) and carries the same " +
        "meanings documented there. " +
        "A suite that is merely INVALID is a successful call: it still has a canonical form, and " +
        "you get it alongside the errors. A call that could not be performed at all (the input " +
        "does not name exactly one suite, the file is missing or unreadable, the path is a network " +
        "location, or the isolated validation worker timed out or failed) returns a tool error " +
        "carrying a single VFX-E-#### error object instead. It never throws for either case. " +
        "PRACTICAL CEILING: a suite near the 5 MB input cap can exceed the validation worker's " +
        "10-second budget, and VFX-E-1150 is the refusal you get. Measured on one developer host, " +
        "a 2.4 MB suite takes about 7 seconds to validate and 8 with normalization, while a 5.1 MB " +
        "one takes about 13 and 14 — so at that size the budget is exceeded with or without " +
        "'normalize', which adds roughly 10-15%. Split a suite that large rather than expecting " +
        "either call to complete.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,

        // The strongest sense of read-only this server has, and the one tool whose whole contract is
        // this flag: no file is opened for writing, created, moved, or deleted anywhere on this path.
        // Held to that by ReadOnlySourceGuardTests, which scans the source rather than trusting the
        // annotation.
        ReadOnly = true,
    });

    private static async Task<CallToolResult> Handle(
        [Description(
            "Absolute or workspace-relative path to the .e2e.yaml suite file to normalize. The file " +
            "is READ ONLY — the canonical text is returned to you, never written back. Supply this " +
            "OR 'yaml', never both.")]
        string? path = null,
        [Description(
            "The suite's YAML text, normalized directly without reading or writing any file. Supply " +
            "this OR 'path', never both.")]
        string? yaml = null,
        [Description(
            "Set true to receive the canonical YAML in 'normalizedYaml'. DEFAULT false, because " +
            "normalization DISCARDS ALL COMMENTS in the suite. Left false, only the validation " +
            "result is returned.")]
        bool? normalize = null,
        CancellationToken cancellationToken = default)
    {
        // The same resolver validate_suite uses, reporting the same VFX-E-1152 for the same three
        // shapes (both, neither, and a path colliding with the worker's stdin marker) — see
        // ValidateSuiteInput. `level` is deliberately not a parameter of this tool: the NormaliseAsync
        // call below hard-codes ValidationLevel.Full (never the resolver's default), so the
        // secret-literal (VFX-D-1207) gate can never be silenced on output a host may write to disk.
        // RealNormalizeSuiteMcpTests.NormalizeSuite_IgnoresALevelArgument_AndAlwaysValidatesAtFull pins
        // that as a fact rather than a coincidence of the default's current value.
        if (!ValidateSuiteInput.TryResolve(path, yaml, level: null, out var resolved, out var inputError, Name))
        {
            return StructuredToolResult.Error(inputError!);
        }

        var normalisation = await ValidationWorkerClient
            .NormaliseAsync(resolved.Source, ValidationLevel.Full, normalize == true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Identical outcome split to validate_suite's, through the identical shared renderer: a
        // suite whose problems are all diagnostics is a SUCCESS carrying data; only a code saying
        // validity was never determined becomes isError. Two tools returning different verdicts about
        // the same missing file is exactly what ValidationOutcomeRenderer exists to prevent.
        return ValidationOutcomeRenderer.TryRenderCallFailure(
            normalisation.Validation.AsValidationResult(), out var failure)
            ? failure!
            : StructuredToolResult.Success(normalisation);
    }
}
