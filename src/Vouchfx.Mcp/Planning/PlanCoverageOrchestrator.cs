using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;

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
/// <b>Verbatim per ITEM, bounded per LIST (issue #41).</b> Every item this tool returns is the
/// engine's own, byte-for-byte; what <see cref="PlanCoverageResponseBudget"/> decides is HOW MANY of
/// them fit in one response. That budget is applied once, here, on the parsed report — so a
/// 50-suite x 10-step repository with no run history returns a bounded answer instead of ~200&#160;KB
/// of <c>step-never-exercised</c> findings doubled by the wire envelope — and every item it drops is
/// reported by a counter on the result. This is the RESPONSE being bounded, never the analysis: the
/// engine always analyses everything, and no bound here ever reaches its command line.
/// </para>
/// <para>
/// <b>Deterministic and read-only (REQ-013).</b> This orchestrator never writes, modifies, or deletes
/// a <c>.e2e.yaml</c> file, never invokes git, and never calls a model API — it only invokes the
/// pinned CLI's own read-only <c>plan</c> analysis and relays its JSON output.
/// </para>
/// <para>
/// <b>Both path arguments go through <see cref="PathSafetyGuard"/> (issue #76).</b> US-S3-08 wired
/// containment into every other path-taking tool and explicitly scoped this one out; the gap that
/// left was not merely the containment half. This orchestrator splices <c>path</c> and
/// <c>eventsPath</c> into the engine CLI's ARGUMENT LIST, so an unguarded UNC path did not fail to
/// reach the network — it reached it one process over, with <c>vouchfx plan</c> performing the
/// outbound SMB/NTLM handshake on this server's behalf. The guard now runs here, at the seam that
/// builds that argument list, and the RESOLVED string is what both the guard and the engine see
/// (<see cref="PathSafetyGuard.ResolveCallerPath"/>'s "resolve at the seam that reads" rule — a
/// guard that contains one string while the subprocess opens another is not a guard). Refusal comes
/// BEFORE the pin handshake: nothing is spawned to decide a path is unsafe.
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
    private readonly Workspace? _workspace;

    /// <param name="pinVerifier">The ENGINE_PIN handshake this tool fails closed on.</param>
    /// <param name="cli">The subprocess seam <c>vouchfx plan</c> is invoked through.</param>
    /// <param name="enginePin">The pin whose version is named in every CLI-unavailable message.</param>
    /// <param name="workspace">
    /// The startup workspace, or <see langword="null"/> when the host supplied no <c>--workspace</c>
    /// flag — containment off, UNC rejection still on. <b>Required, with no default</b>, for the
    /// reason <see cref="PathSafetyGuard.CheckLocalPath"/> gives its own workspace parameter none: a
    /// security parameter whose omitted value turns the check off makes containment-off the thing a
    /// forgetful call site gets.
    /// </param>
    public PlanCoverageOrchestrator(
        CliPinVerifier pinVerifier, IVouchfxCli cli, EnginePin enginePin, Workspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(pinVerifier);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(enginePin);

        _pinVerifier = pinVerifier;
        _cli = cli;
        _enginePin = enginePin;
        _workspace = workspace;
    }

    /// <summary>
    /// Runs the Planner's coverage-and-gap analysis via the pinned engine CLI.
    /// </summary>
    /// <param name="path">
    /// Directory to search recursively for <c>*.e2e.yaml</c> suites, or a single suite file. Absolute,
    /// or relative to the workspace root when one is configured (issue #76).
    /// </param>
    /// <param name="eventsPath">
    /// Optional path to a JSON Lines event history file or directory, under the same resolution and
    /// the same guard as <paramref name="path"/>. <see langword="null"/> is a valid, successful
    /// analysis (REQ-009).
    /// </param>
    /// <param name="staleDays">Optional <c>--stale-days</c> override (REQ-006). <see langword="null"/> uses the engine default.</param>
    /// <param name="flakyMinRuns">Optional <c>--flaky-min-runs</c> override. <see langword="null"/> uses the engine default.</param>
    /// <param name="fragileMinEnvErrors">Optional <c>--fragile-min-env-errors</c> override. <see langword="null"/> uses the engine default.</param>
    /// <param name="inconclusiveMin">Optional <c>--inconclusive-min</c> override. <see langword="null"/> uses the engine default.</param>
    /// <param name="maxFindings">
    /// Optional cap on how many findings the RESPONSE carries (issue #41). Never reaches the engine's
    /// command line — the analysis is always complete, and this only bounds what is relayed back, with
    /// <see cref="PlanCoverageResult.OmittedFindingCount"/> reporting the remainder. Out of
    /// <c>1..</c><see cref="PlanCoverageResponseBudget.MaxRequestedFindings"/> is REFUSED, not clamped.
    /// </param>
    /// <param name="cancellationToken">Cancels the pin handshake and the CLI invocation.</param>
    public async Task<PlanCoverageOutcome> PlanAsync(
        string path,
        string? eventsPath,
        int? staleDays,
        int? flakyMinRuns,
        int? fragileMinEnvErrors,
        int? inconclusiveMin,
        int? maxFindings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new PlanCoverageOutcome.InvalidArgument(
                "path is required and must be a non-empty suite file or directory path.");
        }

        // These two guards run BEFORE the workspace rebase below, and the order is load-bearing:
        // rebasing '-rf' onto a workspace root yields a path that no longer begins with '-', which
        // would launder an argument-injection attempt into an accepted absolute path.
        //
        // CapAndSanitisePathForDisplay rather than a bare sanitise (adjacent fix, same file): these
        // branches echoed the caller's path UNCAPPED, so a 200,000-character argument produced a
        // 200,000-character error message. That is the exact hole PathSafetyGuard.MaxDisplayedPathChars
        // exists to close, and it now applies to every path this method echoes rather than only the
        // ones the guard itself refuses.
        if (path.StartsWith('-'))
        {
            return new PlanCoverageOutcome.InvalidArgument(
                $"path must not begin with '-': '{PathSafetyGuard.CapAndSanitisePathForDisplay(path)}'. A leading " +
                "'-' would be interpreted as a command-line option, not a file path.");
        }

        if (eventsPath is not null && eventsPath.StartsWith('-'))
        {
            return new PlanCoverageOutcome.InvalidArgument(
                $"eventsPath must not begin with '-': '{PathSafetyGuard.CapAndSanitisePathForDisplay(eventsPath)}'. " +
                "A leading '-' would be interpreted as a command-line option, not a file path.");
        }

        var thresholdError = ValidateThresholds(staleDays, flakyMinRuns, fragileMinEnvErrors, inconclusiveMin);
        if (thresholdError is not null)
        {
            return new PlanCoverageOutcome.InvalidArgument(thresholdError);
        }

        // Validated HERE, beside the thresholds and ahead of the path guard's spawn-free refusals, so
        // a bad maxFindings costs no subprocess — the same placement every other argument check in
        // this method has. It is deliberately NOT an engine flag: `vouchfx plan` has no equivalent,
        // and inventing one would make the ANALYSIS partial rather than the RESPONSE bounded.
        if (ValidateMaxFindings(maxFindings) is { } maxFindingsError)
        {
            return new PlanCoverageOutcome.InvalidArgument(maxFindingsError);
        }

        // BEHAVIOUR CHANGE for a host with NO workspace configured (issue #76): the UNC arm is
        // unconditional, so a `\\host\share\...` path this tool used to hand straight to `vouchfx
        // plan` is now refused where nothing refused it before. Deliberate — the engine subprocess
        // was performing the forced-authentication SMB/NTLM handshake this guard exists to prevent.
        // The containment arm stays workspace-gated, exactly as everywhere else.
        //
        // Rebased FIRST, then checked, and the RESOLVED strings are what BuildArguments splices into
        // the CLI's argv below — the guard and the engine must never see different strings.
        // Deliberately ahead of the pin handshake: an unsafe path is refused without spawning
        // anything, including the version probe.
        var resolvedPath = PathSafetyGuard.ResolveCallerPath(path, _workspace);
        if (PathSafetyGuard.CheckLocalPath(resolvedPath, _workspace) is { } pathError)
        {
            return new PlanCoverageOutcome.PathRejected(pathError.Message);
        }

        // Guarded under EXACTLY the condition that puts it on argv — CarriesEventsPath is the ONE
        // predicate both this guard and BuildArguments consult, so the pairing is structural rather
        // than two copies a later edit could split (a code-review finding on the first version of
        // this seam): a blank eventsPath is dropped rather than passed, so there is no string for
        // the guard to be checking — and running it anyway would turn a value this tool has always
        // silently ignored into a new rejection for no security gain.
        var resolvedEventsPath = eventsPath;
        if (CarriesEventsPath(eventsPath))
        {
            resolvedEventsPath = PathSafetyGuard.ResolveCallerPath(eventsPath, _workspace);
            if (PathSafetyGuard.CheckLocalPath(resolvedEventsPath, _workspace) is { } eventsPathError)
            {
                return new PlanCoverageOutcome.PathRejected(eventsPathError.Message);
            }
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

        var arguments = BuildArguments(
            resolvedPath, resolvedEventsPath, staleDays, flakyMinRuns, fragileMinEnvErrors, inconclusiveMin);

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

        // Issue #41: the ONE place the report is bounded for the wire, applied after parsing and
        // before the outcome leaves this method — so every consumer of Completed (the tool today, any
        // future resource) gets the bounded shape without a second call site to remember. The engine's
        // analysis is never narrowed; only what is relayed back is, and every item dropped is counted
        // on the result itself.
        return new PlanCoverageOutcome.Completed(PlanCoverageResponseBudget.Apply(result, maxFindings));
    }

    /// <summary>
    /// Refuses an out-of-range <c>maxFindings</c> rather than clamping it — the rule
    /// <c>get_run_artifacts</c>' <c>tailLines</c> states: a clamp lets a host believe it asked for
    /// 10,000 findings and received all of them, which is the exact misreading a visible bound exists
    /// to prevent.
    /// </summary>
    private static string? ValidateMaxFindings(int? maxFindings)
    {
        if (maxFindings is not { } requested)
        {
            return null;
        }

        if (requested < 1 || requested > PlanCoverageResponseBudget.MaxRequestedFindings)
        {
            return $"maxFindings must be between 1 and {PlanCoverageResponseBudget.MaxRequestedFindings} "
                + $"(received {requested}). Omit it for as many findings as the response budget allows; "
                + "it can only ever return FEWER findings than that budget, never more, and the "
                + "analysis itself is complete either way (omittedFindingCount reports the remainder).";
        }

        return null;
    }

    /// <summary>
    /// Rejects an out-of-range threshold before it is ever spliced into the CLI's argument list —
    /// mirrors the engine's own <c>PlanExport.ValidateThresholds</c> bounds (a negative
    /// <c>--stale-days</c>, or any of the three count thresholds below 1, would otherwise either be
    /// rejected late by the CLI itself as exit 2, or — worse, if the CLI's own guard ever regressed —
    /// silently degenerate the corresponding classification into "every observed step matches").
    /// </summary>
    /// <summary>
    /// The ONE predicate deciding whether <c>eventsPath</c> carries a value — consulted by BOTH the
    /// path guard in <see cref="PlanAsync"/> and <c>BuildArguments</c>' argv test, so "guarded under
    /// exactly the condition that reaches argv" is structural, not two copies an edit could split.
    /// <c>[NotNullWhen(true)]</c> keeps the compiler's null-flow analysis working across the
    /// extraction — without it the call sites regress to CS8604.
    /// </summary>
    private static bool CarriesEventsPath([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? eventsPath) =>
        !string.IsNullOrWhiteSpace(eventsPath);

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
    /// <remarks>
    /// Both path arguments arrive here ALREADY resolved and already guard-approved (issue #76). The
    /// caller's raw strings must not reach this method: they are what the guard was not looking at.
    /// </remarks>
    private static List<string> BuildArguments(
        string path,
        string? eventsPath,
        int? staleDays,
        int? flakyMinRuns,
        int? fragileMinEnvErrors,
        int? inconclusiveMin)
    {
        // One limit of the guard above, stated plainly rather than implied away (the same standard
        // PathSafetyGuard's own remarks set for TOCTOU and hard links): `path` here can be a
        // DIRECTORY, and the containment check binds that analysed ROOT only — the engine's own
        // recursive discovery beneath it is not re-checked by this server, so a link inside a
        // contained root can still lead the engine's walk (and the paths in its report) outside it.
        // Every other path-taking tool resolves its own file set in-process and guards each member;
        // this is the one seam where discovery is subprocess-owned.
        var arguments = new List<string> { "plan", path, "--json" };

        if (CarriesEventsPath(eventsPath))
        {
            arguments.Add("--events");
            arguments.Add(eventsPath!);
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

    /// <summary>
    /// Deserialises the plan report and refuses one whose required members came back
    /// <see langword="null"/>, so every downstream consumer can treat them as non-null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the per-member checks exist at all.</b> The records in <c>PlanCoverageModels.cs</c>
    /// declare these members non-nullable, but that is a COMPILER-side promise about this server's own
    /// code, not a runtime guarantee about text a subprocess produced:
    /// <c>System.Text.Json</c> will happily bind a missing or explicitly-<c>null</c> JSON property to a
    /// non-nullable reference-typed constructor parameter, and the nullable annotation is erased by
    /// then. So <c>{"findings": null}</c> — or a stdout that simply omits the property — used to
    /// deserialise "successfully" into a result whose <c>Findings</c> was null, and the first thing to
    /// touch it (<see cref="PlanCoverageResponseBudget.Apply"/>'s own enumeration) threw a
    /// <see cref="NullReferenceException"/> that ESCAPED this method's
    /// <see cref="JsonException"/> handler entirely, surfacing as an unhandled tool fault instead of a
    /// catalogued error.
    /// </para>
    /// <para>
    /// <b>Why a <see cref="JsonException"/> rather than a new outcome case.</b> This is precisely
    /// "stdout that could not be parsed as the plan report document" —
    /// <see cref="PlanCoverageOutcome.PlanFailed"/>'s existing meaning, and the caller already maps
    /// that to <c>VFX-E-1603</c>. Throwing the same exception type the deserialiser throws lands on
    /// the same handler, so a malformed report has ONE answer regardless of which way it was
    /// malformed. Never a fabricated empty list: a report this server cannot read must not be
    /// presented as a report with nothing in it, which a host would read as "no gaps found".
    /// </para>
    /// </remarks>
    private static PlanCoverageResult ParsePlanReport(string json)
    {
        var result = JsonSerializer.Deserialize<PlanCoverageResult>(json, ReportJsonOptions)
            ?? throw new JsonException("Plan report document deserialised to null.");

        if (result.Findings is null)
        {
            throw new JsonException("Plan report document is missing its 'findings' array.");
        }

        // The ELEMENTS too, not just the array — a review measured `{"findings": [null]}` binding to a
        // one-element list whose single entry is null, which is a DIFFERENT hole from a null array and
        // was not covered by the check above. PlanCoverageResponseBudget.BuildSelectionOrder
        // dereferences every element's .Kind to rank it, so a null entry threw a
        // NullReferenceException past this method's JsonException handler — exactly the escape the
        // array checks were added to close.
        //
        // The findings array is the ONLY one that needs this. The six inventory arrays are COUNTED and
        // sliced (Take/Count), never dereferenced, so a null element in one of them serialises back out
        // as a null and harms nothing — safe by inspection rather than by luck, and stated here so it
        // stays true by intent: a future rule that reads INTO an inventory element (a suite's Path, a
        // dependency's Type) must extend this guard to that array before it does.
        if (result.Findings.Any(finding => finding is null))
        {
            throw new JsonException("Plan report document carries a null entry in its 'findings' array.");
        }

        if (result.Thresholds is null)
        {
            throw new JsonException("Plan report document is missing its 'thresholds' object.");
        }

        if (result.Inventory is null)
        {
            throw new JsonException("Plan report document is missing its 'inventory' object.");
        }

        // Checked individually rather than as a group so the message NAMES the absent array: the
        // whole point of this guard is that a host (or a maintainer reading a support report) learns
        // which part of the engine's output was unreadable, not merely that some part was.
        var inventory = result.Inventory;
        var missingInventoryArray =
            inventory.Suites is null ? "suites"
            : inventory.Services is null ? "services"
            : inventory.Dependencies is null ? "dependencies"
            : inventory.StepTypes is null ? "stepTypes"
            : inventory.UnanalysableSuites is null ? "unanalysableSuites"
            : inventory.UnmappableDependencies is null ? "unmappableDependencies"
            : null;

        if (missingInventoryArray is not null)
        {
            throw new JsonException(
                $"Plan report document is missing its 'inventory.{missingInventoryArray}' array.");
        }

        return result;
    }

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
