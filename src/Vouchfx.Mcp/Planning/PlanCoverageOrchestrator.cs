using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Planning;

/// <summary>
/// REQ-012's <c>plan_coverage</c> pipeline: pin handshake → invoke the pinned
/// <c>vouchfx plan &lt;path&gt; --json [--events &lt;path&gt;] [threshold overrides]</c> → parse the
/// schema-versioned report document (or a structured failure). Structured arguments only — no free
/// text is ever accepted or spliced into the CLI invocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never <c>--fail-on-gap</c> (a deliberate design decision, not an oversight).</b> The engine CLI's
/// <c>--fail-on-gap</c> flag exists so a CI pipeline can treat "at least one gap finding" as a build
/// failure (exit 5). That framing is wrong for this tool: a gap finding is exactly the PRODUCT
/// <c>plan_coverage</c> exists to return, not an error condition — a run that finds ten gaps is a
/// perfectly successful call. This orchestrator therefore never adds that flag, and by construction
/// only ever expects exit <c>0</c> (success — regardless of how many gaps were found) or one of the
/// two documented failure exits (<c>2</c> usage error, <c>3</c> incomplete catalogue metadata) from
/// <c>vouchfx plan</c>. See <c>PlanCommand.cs</c> in the engine repo for the full exit-code contract
/// this mirrors.
/// </para>
/// <para>
/// <b>Own, independent DTOs, not the engine's typed records.</b> This server never references any
/// engine assembly (see <see cref="Vouchfx.Mcp.Run.VouchfxCliSuiteRunner"/>'s and
/// <see cref="Vouchfx.Mcp.Run.SuiteEventParser"/>'s remarks for the same rule elsewhere in this
/// codebase) — the <c>PlanCoverageModels.cs</c> records mirror the engine's frozen v1 plan-report
/// wire shape (planner-coverage-and-gap-report REQ-011) without importing it, so the report is
/// relayed to the host verbatim: every REQ-004/REQ-005 gap finding's <c>suggestedTypes</c>/
/// <c>suggestedStepId</c> reaches the host UNCHANGED from what the engine emitted, ready to feed
/// straight into <c>scaffold_suite</c>'s own <c>steps[].type</c>/<c>steps[].id</c> (REQ-012's
/// "hand-off hints feed scaffold_suite unchanged" acceptance criterion).
/// </para>
/// <para>
/// <b>Deterministic and read-only (REQ-013).</b> This orchestrator never writes, modifies, or deletes
/// a <c>.e2e.yaml</c> file, never invokes git, and never calls a model API — it only invokes the
/// pinned CLI's own read-only <c>plan</c> analysis and relays its JSON output.
/// </para>
/// </remarks>
public sealed class PlanCoverageOrchestrator
{
    /// <summary>The <c>vouchfx plan</c> exit code for a successful analysis (REQ-010).</summary>
    private const int SuccessExitCode = 0;

    /// <summary>
    /// The <c>vouchfx plan</c> exit code for a usage error: a bad/missing suite path, an empty suite
    /// folder (EDGE-009), or an out-of-range threshold (REQ-010).
    /// </summary>
    private const int UsageErrorExitCode = 2;

    /// <summary>Maximum length, in characters, of CLI stderr/stdout (post-sanitise) spliced into a tool error message.</summary>
    private const int MaxDiagnosticLength = 1500;

    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CliPinVerifier _pinVerifier;
    private readonly IVouchfxCli _cli;
    private readonly EnginePin _enginePin;

    public PlanCoverageOrchestrator(CliPinVerifier pinVerifier, IVouchfxCli cli, EnginePin enginePin)
    {
        ArgumentNullException.ThrowIfNull(pinVerifier);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(enginePin);

        _pinVerifier = pinVerifier;
        _cli = cli;
        _enginePin = enginePin;
    }

    /// <summary>
    /// Runs the Planner's coverage-and-gap analysis via the pinned engine CLI.
    /// </summary>
    /// <param name="path">Directory to search recursively for <c>*.e2e.yaml</c> suites, or a single suite file.</param>
    /// <param name="eventsPath">Optional path to a JSON Lines event history file or directory. <see langword="null"/> is a valid, successful analysis (REQ-009).</param>
    /// <param name="staleDays">Optional <c>--stale-days</c> override (REQ-006). <see langword="null"/> uses the engine default.</param>
    /// <param name="flakyMinRuns">Optional <c>--flaky-min-runs</c> override. <see langword="null"/> uses the engine default.</param>
    /// <param name="fragileMinEnvErrors">Optional <c>--fragile-min-env-errors</c> override. <see langword="null"/> uses the engine default.</param>
    /// <param name="inconclusiveMin">Optional <c>--inconclusive-min</c> override. <see langword="null"/> uses the engine default.</param>
    /// <param name="cancellationToken">Cancels the pin handshake and the CLI invocation.</param>
    public async Task<PlanCoverageOutcome> PlanAsync(
        string path,
        string? eventsPath,
        int? staleDays,
        int? flakyMinRuns,
        int? fragileMinEnvErrors,
        int? inconclusiveMin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new PlanCoverageOutcome.InvalidArgument(
                "path is required and must be a non-empty suite file or directory path.");
        }

        if (path.StartsWith('-'))
        {
            return new PlanCoverageOutcome.InvalidArgument(
                $"path must not begin with '-': '{TextSanitiser.SanitiseForDisplay(path)}'. A leading " +
                "'-' would be interpreted as a command-line option, not a file path.");
        }

        if (eventsPath is not null && eventsPath.StartsWith('-'))
        {
            return new PlanCoverageOutcome.InvalidArgument(
                $"eventsPath must not begin with '-': '{TextSanitiser.SanitiseForDisplay(eventsPath)}'. " +
                "A leading '-' would be interpreted as a command-line option, not a file path.");
        }

        var thresholdError = ValidateThresholds(staleDays, flakyMinRuns, fragileMinEnvErrors, inconclusiveMin);
        if (thresholdError is not null)
        {
            return new PlanCoverageOutcome.InvalidArgument(thresholdError);
        }

        var pin = await _pinVerifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        switch (pin)
        {
            case CliPinResult.NotFound notFound:
                return new PlanCoverageOutcome.CliUnavailable(notFound.Message);
            case CliPinResult.VersionMismatch mismatch:
                return new PlanCoverageOutcome.CliUnavailable(mismatch.Message);
            case CliPinResult.Unparseable unparseable:
                return new PlanCoverageOutcome.CliUnavailable(unparseable.Message);
            case CliPinResult.Ok:
                break;
            default:
                return new PlanCoverageOutcome.CliUnavailable(
                    $"The vouchfx CLI (version {_enginePin.Version}) could not be verified for plan_coverage.");
        }

        var arguments = BuildArguments(path, eventsPath, staleDays, flakyMinRuns, fragileMinEnvErrors, inconclusiveMin);

        // plan does NOT share the shared version-probe/list/schema/scaffold DefaultTimeout: it walks
        // the full analysed suite tree plus an optional event-history directory, work that scales
        // with the caller's own path/eventsPath — see VouchfxCliProcessRunner.PlanTimeout's remarks.
        var invocation = await _cli.RunAsync(
                arguments,
                VouchfxCliProcessRunner.MaxPlanOutputBytes,
                VouchfxCliProcessRunner.PlanTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (!invocation.Launched)
        {
            // Three DIFFERENT reasons collapse to "not launched", and each needs a DIFFERENT,
            // actionable message: "install/update the CLI" is flatly wrong advice for a CLI that is
            // present, pinned, and simply took too long or produced too much output for a large
            // suite/history — see CliLaunchFailureReason's own remarks.
            return invocation.FailureReason switch
            {
                CliLaunchFailureReason.TimedOut => new PlanCoverageOutcome.PlanFailed(
                    $"vouchfx plan did not complete within its {(int)VouchfxCliProcessRunner.PlanTimeout.TotalSeconds}-" +
                    "second budget and was terminated. Narrow `path` or `eventsPath` (fewer suites, or a " +
                    "smaller/more recent event history) and retry."),
                CliLaunchFailureReason.OutputCapExceeded => new PlanCoverageOutcome.PlanFailed(
                    "vouchfx plan's report exceeded the " +
                    $"{VouchfxCliProcessRunner.MaxPlanOutputBytes / (1024 * 1024)} MB output cap and was " +
                    "terminated before it could be captured. Narrow `path` or `eventsPath` and retry."),
                _ => new PlanCoverageOutcome.CliUnavailable(
                    $"The vouchfx CLI (version {_enginePin.Version}) could not run 'plan'. " +
                    "Ensure the pinned CLI is on PATH and implements the M3 Planner (`vouchfx plan`). " +
                    $"Install/update with: dotnet tool install --global vouchfx --version " +
                    $"{CliVersionNormaliser.Normalise(_enginePin.Version)}"),
            };
        }

        if (invocation.ExitCode == UsageErrorExitCode)
        {
            return new PlanCoverageOutcome.InvalidArgument(
                BuildCliDiagnostic(invocation.Stderr, invocation.Stdout, invocation.ExitCode));
        }

        if (invocation.ExitCode != SuccessExitCode)
        {
            // Exit 3 (incomplete catalogue metadata) or any other unrecognised non-zero exit code
            // (--fail-on-gap's exit 5 is unreachable here since this orchestrator never passes that
            // flag — see this type's remarks — but is still handled defensively, never mistaken for
            // success) — an engine-side failure, not the caller's actionable mistake.
            return new PlanCoverageOutcome.PlanFailed(
                BuildCliDiagnostic(invocation.Stderr, invocation.Stdout, invocation.ExitCode));
        }

        var stdout = invocation.Stdout;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new PlanCoverageOutcome.PlanFailed(
                "vouchfx plan exited 0 but produced empty stdout. Confirm the pinned CLI implements " +
                "the M3 Planner and --json output.");
        }

        PlanCoverageResult result;
        try
        {
            result = ParsePlanReport(stdout);
        }
        catch (JsonException ex)
        {
            return new PlanCoverageOutcome.PlanFailed(
                "vouchfx plan --json produced output that could not be parsed as the plan report " +
                $"document: {TextSanitiser.SanitiseForDisplay(ex.Message)}");
        }

        return new PlanCoverageOutcome.Completed(result);
    }

    /// <summary>
    /// Rejects an out-of-range threshold before it is ever spliced into the CLI's argument list —
    /// mirrors the engine's own <c>PlanExport.ValidateThresholds</c> bounds (a negative
    /// <c>--stale-days</c>, or any of the three count thresholds below 1, would otherwise either be
    /// rejected late by the CLI itself as exit 2, or — worse, if the CLI's own guard ever regressed —
    /// silently degenerate the corresponding classification into "every observed step matches").
    /// </summary>
    private static string? ValidateThresholds(int? staleDays, int? flakyMinRuns, int? fragileMinEnvErrors, int? inconclusiveMin)
    {
        if (staleDays is < 0)
        {
            return $"staleDays must be zero or greater (received {staleDays}).";
        }

        if (flakyMinRuns is < 1)
        {
            return $"flakyMinRuns must be at least 1 (received {flakyMinRuns}).";
        }

        if (fragileMinEnvErrors is < 1)
        {
            return $"fragileMinEnvErrors must be at least 1 (received {fragileMinEnvErrors}).";
        }

        if (inconclusiveMin is < 1)
        {
            return $"inconclusiveMin must be at least 1 (received {inconclusiveMin}).";
        }

        return null;
    }

    /// <summary>
    /// Builds the <c>vouchfx plan</c> argument list: always <c>plan &lt;path&gt; --json</c>, plus any
    /// supplied optional overrides. Deliberately NEVER appends <c>--fail-on-gap</c> or <c>--output</c>
    /// — see this type's remarks.
    /// </summary>
    private static List<string> BuildArguments(
        string path,
        string? eventsPath,
        int? staleDays,
        int? flakyMinRuns,
        int? fragileMinEnvErrors,
        int? inconclusiveMin)
    {
        var arguments = new List<string> { "plan", path, "--json" };

        if (!string.IsNullOrWhiteSpace(eventsPath))
        {
            arguments.Add("--events");
            arguments.Add(eventsPath);
        }

        if (staleDays is { } sd)
        {
            arguments.Add("--stale-days");
            arguments.Add(sd.ToString(CultureInfo.InvariantCulture));
        }

        if (flakyMinRuns is { } fmr)
        {
            arguments.Add("--flaky-min-runs");
            arguments.Add(fmr.ToString(CultureInfo.InvariantCulture));
        }

        if (fragileMinEnvErrors is { } fmee)
        {
            arguments.Add("--fragile-min-env-errors");
            arguments.Add(fmee.ToString(CultureInfo.InvariantCulture));
        }

        if (inconclusiveMin is { } im)
        {
            arguments.Add("--inconclusive-min");
            arguments.Add(im.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    private static PlanCoverageResult ParsePlanReport(string json) =>
        JsonSerializer.Deserialize<PlanCoverageResult>(json, ReportJsonOptions)
            ?? throw new JsonException("Plan report document deserialised to null.");

    private static string BuildCliDiagnostic(string? stderr, string? stdout, int exitCode)
    {
        var raw = !string.IsNullOrWhiteSpace(stderr)
            ? stderr.Trim()
            : !string.IsNullOrWhiteSpace(stdout)
                ? stdout.Trim()
                : $"vouchfx plan failed with exit code {exitCode} and no diagnostic output.";

        var sanitised = TextSanitiser.SanitiseForDisplay(raw);
        if (sanitised.Length > MaxDiagnosticLength)
        {
            sanitised = sanitised[..MaxDiagnosticLength];
        }

        // Prefix so agents know this is an engine/plan failure, not an MCP crash.
        if (sanitised.Contains("plan", StringComparison.OrdinalIgnoreCase)
            || sanitised.Contains("suite", StringComparison.OrdinalIgnoreCase)
            || sanitised.Contains("catalogue", StringComparison.OrdinalIgnoreCase)
            || sanitised.Contains("threshold", StringComparison.OrdinalIgnoreCase))
        {
            return sanitised;
        }

        return $"vouchfx plan failed (exit {exitCode}): {sanitised}";
    }
}
