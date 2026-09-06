using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>validate_suite</c> tool: validates an <c>.e2e.yaml</c> suite — a file on disk or YAML
/// text supplied inline — against the vouchfx JSON Schema without running it (REQ-003), and reports
/// what the suite contains (Sprint 2 / US-S2-02).
/// </summary>
/// <remarks>
/// <para>
/// A thin MCP-facing wrapper: argument resolution is <see cref="ValidateSuiteInput"/>'s, and the
/// actual validation pipeline runs isolated in a child process, via
/// <see cref="ValidationWorkerClient"/> — see that type's remarks for why untrusted YAML content
/// must never be parsed directly inside this long-lived server process. <b>Inline YAML is no
/// exception to that</b>: it crosses the same boundary, under the same wall clock, with the same
/// whole-tree kill. It is untrusted content in exactly the way a file's content is.
/// </para>
/// <para>
/// <b>Two result channels, never merged</b> (see <see cref="SuiteAnalysis"/>): <c>errors</c> is the
/// schema verdict — what the engine itself would say — and <c>semanticDiagnostics</c> is this
/// server's own advice about a document the schema already accepts.
/// </para>
/// </remarks>
internal static class ValidateSuiteTool
{
    public const string Name = "validate_suite";

    // `static readonly`, not `const`, so the entry cap is interpolated from
    // SuiteSummaryBuilder.MaxEntriesPerList rather than restated as a literal a future change to that
    // constant would silently leave stale in the host-facing prose. McpServerToolCreateOptions takes
    // a plain string, so nothing here needs a compile-time constant.
    private static readonly string Description =
        "Validates a vouchfx .e2e.yaml integration test suite against the engine's JSON Schema " +
        "and reports every structural error found (with a JSON pointer and, where derivable, a " +
        "YAML line number), without running the suite. Supply EXACTLY ONE of 'path' (a suite file " +
        "on disk) or 'yaml' (suite text inline, for a draft that has not been written to a file " +
        "yet) — both, or neither, is a tool error. " +
        "The result also carries a 'summary' of what the suite contains (step count, step types, " +
        "service and dependency names, capture variable names, and the {placeholder} tokens used), " +
        "and a separate 'semanticDiagnostics' array kept apart from the schema 'errors' array. " +
        "'summary' is null whenever no document was built to describe — malformed YAML, or input " +
        "a safety guard rejected — so check it for null before reading it. Each of its lists stops " +
        $"at {SuiteSummaryBuilder.MaxEntriesPerList} entries; when any of them did, " +
        "'summary.truncated' is true and the digest must not be treated as a complete inventory of " +
        "the suite. Separately, and WITHOUT setting 'truncated', every list omits any name " +
        "containing a ${…} reference (secret hygiene: a capture, service, dependency, or step " +
        "type named after one is dropped rather than echoed), so 'summary' must never be used to " +
        "decide that a name is undeclared — a name's absence from a list is not evidence it was " +
        "not declared. " +
        "'level' selects which passes run: 'full' (the default) runs both, 'schema' runs only the " +
        "JSON Schema pass, 'semantic' runs only the semantic-rules pass. " +
        "At level 'semantic' the JSON Schema pass DOES NOT RUN, so 'valid' there reports only that " +
        "no semantic error was found — it is not evidence the engine would accept the suite, and " +
        "'errors' is empty because nothing looked. Read 'level' back off the result before " +
        "interpreting 'valid'. " +
        "Most semantic findings are advice and leave 'valid' true; a semantic finding of severity " +
        "'error' (today only VFX-D-1207, a secret literal written into the suite) makes 'valid' " +
        "false even when 'errors' is empty. Severity is a property of the FINDING, not of the code " +
        "— VFX-D-1207 itself reports at 'error' for a structurally certain shape (a private-key PEM " +
        "header, an AWS key id, an inline password) and at 'warning' for a high-entropy-token guess " +
        "— so read 'severity' off each entry rather than inferring it from the code. " +
        $"'semanticDiagnostics' stops at {Validation.Semantics.SemanticAnalyser.MaxPublishedFindings} " +
        "entries; when it did, 'semanticDiagnosticsTruncated' is true and the array is not a " +
        "complete list of this suite's findings. " +
        "A suite that is merely INVALID is a successful call: valid:true, or valid:false " +
        "with an errors list carrying VFX-D-#### diagnostic codes — malformed YAML and schema " +
        "violations both come back that way, never as a tool error. A call that could not be " +
        "performed at all (the input does not name exactly one suite, the level is unrecognised, " +
        "the file is missing or unreadable, the path is a network location or — when this server " +
        "was started with a workspace — resolves outside that workspace root, or " +
        "the isolated validation worker timed out or failed) returns a tool error carrying a " +
        "single VFX-E-#### error object instead, because the suite's validity was never " +
        "determined. It never throws for either case.";

    /// <param name="workspace">
    /// The workspace resolved at server start (US-S3-08), or <see langword="null"/> when the host
    /// supplied no <c>--workspace</c> flag. Captured in the handler's closure — the same shape
    /// <see cref="ExplainRunTool"/> uses for its orchestrator — and passed to the worker client,
    /// which is where containment is actually enforced. Never part of the tool's INPUT schema: the
    /// workspace is a server configuration, not something a caller may nominate per call.
    /// </param>
    public static McpServerTool Create(Workspace? workspace)
    {
        // The '= null' defaults are load-bearing, not stylistic: they are what makes the SDK's
        // generated JSON schema mark path/yaml/level OPTIONAL, which is the whole basis of the
        // "exactly one of path or yaml" rule enforced below rather than in the schema. Same
        // rationale ExplainRunTool records for its own optional parameter.
        Task<CallToolResult> Handle(
            [Description(
                "Absolute or workspace-relative path to the .e2e.yaml suite file to validate. Supply " +
                "this OR 'yaml', never both.")]
            string? path = null,
            [Description(
                "The suite's YAML text, validated directly without reading or writing any file. Supply " +
                "this OR 'path', never both.")]
            string? yaml = null,
            [Description(
                "Which passes to run: 'full' (default), 'schema' for the JSON Schema pass only, or " +
                "'semantic' for the semantic-rules pass only. Case-sensitive.")]
            string? level = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(workspace, path, yaml, level, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        Workspace? workspace,
        string? path,
        string? yaml,
        string? level,
        CancellationToken cancellationToken)
    {
        // Neither `path` nor `yaml` is marked required in the input schema, because the real rule is
        // "exactly one of the two" — which no `required` list can express, and which an MCP host
        // would enforce as "always send path" if either were listed. The rule therefore lives here,
        // and its refusal is a catalogued code (VFX-E-1152) rather than a schema rejection the host
        // would render as a protocol error.
        if (!ValidateSuiteInput.TryResolve(path, yaml, level, out var resolved, out var inputError))
        {
            return StructuredToolResult.Error(inputError!);
        }

        var analysis = await ValidationWorkerClient
            .AnalyseAsync(resolved.Source, resolved.Level, workspace, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // US-S1-04: a result whose problems are all diagnostics is still a SUCCESS carrying data —
        // the behaviour this tool has always had, and the precedent the whole VFX-code split was
        // built around. Only a code that says validity was never determined becomes isError.
        // See ValidationOutcomeRenderer for why the rule lives there rather than inline here; it is
        // handed the narrowed v1 shape because the split turns on the schema channel alone (a
        // semantic finding is a VFX-D code by construction and so can never be a call failure).
        return ValidationOutcomeRenderer.TryRenderCallFailure(analysis.AsValidationResult(), out var failure)
            ? failure!
            : StructuredToolResult.Success(analysis);
    }
}
