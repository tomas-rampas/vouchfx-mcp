using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>run_suite</c> tool: will run an <c>.e2e.yaml</c> suite through the packaged vouchfx
/// CLI and report its verdict.
/// </summary>
/// <remarks>
/// REQ-008: before anything else, <c>Handle</c> verifies the vouchfx CLI is on PATH and matches
/// <c>ENGINE_PIN</c> via <see cref="CliPinVerifier"/> — the actual run (todo 7) does not exist yet,
/// but the gate does, since it is what an agent needs to see FIRST when the CLI is missing or
/// mismatched, regardless of whether the run itself is implemented. See
/// <see cref="CliPinVerifier"/>'s remarks for why <c>validate_suite</c>/<c>list_step_types</c>/
/// <c>describe_step_type</c>/<c>search_docs</c> never call it.
/// </remarks>
internal static class RunSuiteTool
{
    public const string Name = "run_suite";

    private const string Description =
        "Runs a vouchfx .e2e.yaml suite through the packaged vouchfx CLI and reports its verdict " +
        "(pass / fail / environment error / inconclusive) once the run completes. Give it the " +
        "suite path; optionally restrict the run to steps or scenarios matching one or more " +
        "tags, and/or cap the whole run with a timeout in seconds. Requires the vouchfx CLI on " +
        "PATH at the version this server is pinned to — a missing or mismatched CLI returns a " +
        "structured error naming the exact install/update command, rather than attempting to " +
        "run anyway.";

    public static McpServerTool Create(CliPinVerifier cliPinVerifier)
    {
        ArgumentNullException.ThrowIfNull(cliPinVerifier);

        // The '= null' default values below are load-bearing, not stylistic: they are what makes
        // the SDK's generated JSON schema mark tags/timeoutSeconds as OPTIONAL (see
        // RunSuite_Schema_HasRequiredPathAndOptionalTagsAndTimeoutSeconds) and let a caller omit
        // them entirely without the SDK failing parameter binding before Handle even runs —
        // exactly matching this parameter list's ORIGINAL (pre-REQ-008) shape.
        Task<CallToolResult> Handle(
            [Description("Absolute or workspace-relative path to the .e2e.yaml suite file to run.")]
            string path,
            [Description("Only run steps/scenarios matching one or more of these tags. Omit to run the whole suite.")]
            string[]? tags = null,
            [Description("Abort the run if it has not completed within this many seconds. Omit for the engine's default timeout.")]
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(cliPinVerifier, path, tags, timeoutSeconds, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = false,
            Destructive = false,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        CliPinVerifier cliPinVerifier,
        string path,
        string[]? tags,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        // path/tags/timeoutSeconds are unused until todo 7 (real execution) lands; the gate below
        // is REQ-008's whole deliverable for this tool in this todo.
        _ = path;
        _ = tags;
        _ = timeoutSeconds;

        var pinResult = await cliPinVerifier.VerifyAsync(cancellationToken);

        if (pinResult is not CliPinResult.Ok)
        {
            return StructuredToolResult.Error(DescribeGateFailure(pinResult));
        }

        return StubToolResult.NotImplemented(Name, "Running vouchfx suites through the packaged CLI");
    }

    private static string DescribeGateFailure(CliPinResult result) => result switch
    {
        CliPinResult.NotFound notFound => notFound.Message,
        CliPinResult.VersionMismatch mismatch => mismatch.Message,
        CliPinResult.Unparseable unparseable => unparseable.Message,
        _ => "The vouchfx CLI could not be verified.",
    };
}
