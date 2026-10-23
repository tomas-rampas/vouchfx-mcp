using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// Compile-validates a suite with the pinned CLI's live provider registry, without starting topology.
/// Offline authoring validation deliberately remains a separate, bundled-provider contract.
/// </summary>
internal static class CliSuitePreflight
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal const long MaxOutputBytes = 1024 * 1024;

    internal static async Task<ValidateSuiteResult> ValidateAsync(
        IVouchfxCli cli, string path, string expectedVersion, Workspace? workspace, CancellationToken cancellationToken)
    {
        // Recheck the same path guards immediately before the CLI receives the file. No YAML is
        // parsed in this process, and no bundled schema filters out trusted external providers.
        var guard = SuiteValidator.CheckFastRejects(path, workspace);
        if (guard is not null)
        {
            return new(false, [guard]);
        }

        var result = await cli.RunAsync(["validate", path, "--json"], MaxOutputBytes, Timeout, cancellationToken);
        if (result.FailureReason == CliLaunchFailureReason.TimedOut)
        {
            return Failure(VfxCodeCatalogue.ValidationTimeout, "The CLI validation exceeded its wall-clock budget.");
        }

        if (!result.Launched || result.ExitCode is not (0 or 4) || result.Stdout is null)
        {
            return Unusable();
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || !string.Equals(CliVersionNormaliser.Normalise(root.GetProperty("engineVersion").GetString() ?? ""),
                    expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return Unusable();
            }

            var valid = root.GetProperty("valid").GetBoolean();
            var scenarios = root.GetProperty("scenarios");
            if (scenarios.ValueKind != JsonValueKind.Array || scenarios.GetArrayLength() == 0)
            {
                // validate accepts an empty discovery set; run_suite promised a specific file.
                return Unusable();
            }

            var allValid = true;
            var errors = new List<SuiteValidationError>();
            foreach (var scenario in scenarios.EnumerateArray())
            {
                if (!string.Equals(Path.GetFullPath(scenario.GetProperty("path").GetString()!), Path.GetFullPath(path),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return Unusable();
                }

                var scenarioValid = scenario.GetProperty("valid").GetBoolean();
                allValid &= scenarioValid;
                var diagnostics = scenario.GetProperty("diagnostics");
                if (diagnostics.ValueKind != JsonValueKind.Array || (scenarioValid && diagnostics.GetArrayLength() != 0)
                    || (!scenarioValid && diagnostics.GetArrayLength() == 0))
                {
                    return Unusable();
                }

                foreach (var diagnostic in diagnostics.EnumerateArray())
                {
                    var stage = diagnostic.GetProperty("stage").GetString();
                    if (stage is not ("schema" or "parse" or "pipeline" or "roslyn")
                        || diagnostic.GetProperty("message").ValueKind != JsonValueKind.String)
                    {
                        return Unusable();
                    }

                    // Provider/compiler diagnostics can contain suite literals and absolute host
                    // paths. Never relay those arbitrary messages or stderr into MCP results.
                    errors.Add(new(
                        stage == "parse" ? VfxCodeCatalogue.YamlParseError :
                        stage == "schema" ? VfxCodeCatalogue.SchemaViolation : VfxCodeCatalogue.SuiteInvalid,
                        null, $"The pinned CLI rejected the suite during {stage} validation. Run vouchfx validate locally for details.",
                        null, null));
                }
            }

            return valid == allValid && result.ExitCode == (valid ? 0 : 4)
                ? new(valid, errors)
                : Unusable();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                   or ArgumentException or NotSupportedException or FormatException or OverflowException
                                   or PathTooLongException)
        {
            return Unusable();
        }
    }

    private static ValidateSuiteResult Unusable() => Failure(
        VfxCodeCatalogue.ValidationWorkerFailed,
        "The pinned CLI did not produce a usable validation result. Check vouchfx validate locally and retry.");

    private static ValidateSuiteResult Failure(string code, string message) =>
        new(false, [new(code, null, message, null, null)]);
}
