using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Scaffold;

/// <summary>
/// REQ-007's scaffold pipeline: pin handshake → serialise structured intent to a temp file →
/// invoke the pinned <c>vouchfx scaffold --intent &lt;tmp&gt;</c> → return YAML (or a structured
/// failure). Free text is never accepted; the host LLM chooses types/ids before calling this tool.
/// </summary>
/// <remarks>
/// Deterministic for a given input and engine registration (REQ-010). Does not host or call an LLM.
/// Temp intent files are always deleted in a finally block.
/// </remarks>
public sealed class ScaffoldSuiteOrchestrator
{
    private static readonly JsonSerializerOptions IntentJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Maximum characters of CLI stderr (post-sanitise) spliced into a tool error message.
    /// </summary>
    private const int MaxDiagnosticLength = 1500;

    private readonly CliPinVerifier _pinVerifier;
    private readonly IVouchfxCli _cli;
    private readonly EnginePin _enginePin;

    public ScaffoldSuiteOrchestrator(CliPinVerifier pinVerifier, IVouchfxCli cli, EnginePin enginePin)
    {
        ArgumentNullException.ThrowIfNull(pinVerifier);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(enginePin);

        _pinVerifier = pinVerifier;
        _cli = cli;
        _enginePin = enginePin;
    }

    /// <summary>
    /// Scaffolds a suite from structured step/environment intent via the pinned engine CLI.
    /// </summary>
    public async Task<ScaffoldSuiteOutcome> ScaffoldAsync(
        IReadOnlyList<ScaffoldStepRequest> steps,
        IReadOnlyList<ScaffoldServiceRequest>? services,
        IReadOnlyList<ScaffoldDependencyRequest>? dependencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
        {
            return new ScaffoldSuiteOutcome.InvalidArgument(
                "scaffold_suite requires at least one step (ordered list of id + type). " +
                "Free text is not accepted — choose step types via list_step_types first.");
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                return new ScaffoldSuiteOutcome.InvalidArgument(
                    $"steps[{i}].id is required and must be non-empty.");
            }

            if (string.IsNullOrWhiteSpace(step.Type))
            {
                return new ScaffoldSuiteOutcome.InvalidArgument(
                    $"steps[{i}].type is required and must be a dotted family.provider key " +
                    $"(e.g. http.rest). Free text is not accepted.");
            }

            if (step.Id.StartsWith('-') || step.Type.StartsWith('-'))
            {
                return new ScaffoldSuiteOutcome.InvalidArgument(
                    "Step id and type must not begin with '-' (would be misread as a CLI option).");
            }
        }

        if (services is not null)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(services[i].Name))
                {
                    return new ScaffoldSuiteOutcome.InvalidArgument(
                        $"services[{i}].name is required and must be non-empty.");
                }
            }
        }

        if (dependencies is not null)
        {
            for (var i = 0; i < dependencies.Count; i++)
            {
                var dep = dependencies[i];
                if (string.IsNullOrWhiteSpace(dep.Name) || string.IsNullOrWhiteSpace(dep.Type))
                {
                    return new ScaffoldSuiteOutcome.InvalidArgument(
                        $"dependencies[{i}] requires both name and type (e.g. type: postgres).");
                }
            }
        }

        var pin = await _pinVerifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        switch (pin)
        {
            case CliPinResult.NotFound notFound:
                return new ScaffoldSuiteOutcome.CliUnavailable(notFound.Message);
            case CliPinResult.VersionMismatch mismatch:
                return new ScaffoldSuiteOutcome.CliUnavailable(mismatch.Message);
            case CliPinResult.Unparseable unparseable:
                return new ScaffoldSuiteOutcome.CliUnavailable(unparseable.Message);
            case CliPinResult.Ok:
                break;
            default:
                return new ScaffoldSuiteOutcome.CliUnavailable(
                    $"The vouchfx CLI (version {_enginePin.Version}) could not be verified for scaffold_suite.");
        }

        var intentPath = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-scaffold-{Guid.NewGuid():N}.json");
        try
        {
            var intentDocument = new ScaffoldIntentDocument(
                Steps: steps.Select(s => new ScaffoldIntentStep(s.Id.Trim(), s.Type.Trim(), NullIfWhiteSpace(s.Label))).ToArray(),
                Services: services is { Count: > 0 }
                    ? services.Select(s => new ScaffoldIntentService(s.Name.Trim(), NullIfWhiteSpace(s.Image))).ToArray()
                    : null,
                Dependencies: dependencies is { Count: > 0 }
                    ? dependencies.Select(d => new ScaffoldIntentDependency(d.Name.Trim(), d.Type.Trim())).ToArray()
                    : null);

            var intentJson = JsonSerializer.Serialize(intentDocument, IntentJsonOptions);
            await File.WriteAllTextAsync(intentPath, intentJson, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            var invocation = await _cli.RunAsync(
                    ["scaffold", "--intent", intentPath],
                    VouchfxCliProcessRunner.MaxScaffoldOutputBytes,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!invocation.Launched)
            {
                // Mirrors PlanCoverageOrchestrator's identical three-way split (see
                // CliLaunchFailureReason's own remarks): "install/update the CLI" is the wrong
                // instruction when the CLI is present and simply ran past its budget or produced too
                // much output.
                return invocation.FailureReason switch
                {
                    CliLaunchFailureReason.TimedOut => new ScaffoldSuiteOutcome.ScaffoldFailed(
                        $"vouchfx scaffold did not complete within its {(int)VouchfxCliProcessRunner.DefaultTimeout.TotalSeconds}-" +
                        "second budget and was terminated. Retry with a smaller intent document (fewer steps)."),
                    CliLaunchFailureReason.OutputCapExceeded => new ScaffoldSuiteOutcome.ScaffoldFailed(
                        "vouchfx scaffold's output exceeded the " +
                        $"{VouchfxCliProcessRunner.MaxScaffoldOutputBytes / (1024 * 1024)} MB output cap and was " +
                        "terminated before it could be captured. Retry with a smaller intent document (fewer steps)."),
                    _ => new ScaffoldSuiteOutcome.CliUnavailable(
                        $"The vouchfx CLI (version {_enginePin.Version}) could not run 'scaffold'. " +
                        "Ensure the pinned CLI is on PATH and implements Spec B (`vouchfx scaffold --intent`). " +
                        $"Install/update with: dotnet tool install --global vouchfx --version " +
                        $"{CliVersionNormaliser.Normalise(_enginePin.Version)}"),
                };
            }

            if (invocation.ExitCode != 0)
            {
                var diagnostic = BuildCliDiagnostic(invocation.Stderr, invocation.Stdout, invocation.ExitCode);
                return new ScaffoldSuiteOutcome.ScaffoldFailed(diagnostic);
            }

            var yaml = invocation.Stdout;
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return new ScaffoldSuiteOutcome.ScaffoldFailed(
                    "vouchfx scaffold exited 0 but produced empty stdout. " +
                    "Confirm the pinned CLI implements Spec B scaffold and does not require --output.");
            }

            return new ScaffoldSuiteOutcome.Completed(new ScaffoldSuiteResult(yaml));
        }
        finally
        {
            try
            {
                if (File.Exists(intentPath))
                {
                    File.Delete(intentPath);
                }
            }
#pragma warning disable CA1031 // Best-effort temp cleanup.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildCliDiagnostic(string? stderr, string? stdout, int exitCode)
    {
        var raw = !string.IsNullOrWhiteSpace(stderr)
            ? stderr.Trim()
            : !string.IsNullOrWhiteSpace(stdout)
                ? stdout.Trim()
                : $"vouchfx scaffold failed with exit code {exitCode} and no diagnostic output.";

        var sanitised = TextSanitiser.SanitiseForDisplay(raw);
        if (sanitised.Length > MaxDiagnosticLength)
        {
            sanitised = sanitised[..MaxDiagnosticLength];
        }

        // Prefix so agents know this is an engine/scaffold failure, not an MCP crash.
        if (sanitised.Contains("scaffold", StringComparison.OrdinalIgnoreCase)
            || sanitised.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || sanitised.Contains("step", StringComparison.OrdinalIgnoreCase))
        {
            return sanitised;
        }

        return $"vouchfx scaffold failed (exit {exitCode}): {sanitised}";
    }

    // Intent JSON shape matching engine ScaffoldIntentDto (camelCase).
    private sealed record ScaffoldIntentDocument(
        IReadOnlyList<ScaffoldIntentStep> Steps,
        IReadOnlyList<ScaffoldIntentService>? Services,
        IReadOnlyList<ScaffoldIntentDependency>? Dependencies);

    private sealed record ScaffoldIntentStep(string Id, string Type, string? Label);

    private sealed record ScaffoldIntentService(string Name, string? Image);

    private sealed record ScaffoldIntentDependency(string Name, string Type);
}
