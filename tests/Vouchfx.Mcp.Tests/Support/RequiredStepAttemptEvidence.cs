using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;
using Xunit.Sdk;

namespace Vouchfx.Mcp.Tests;

/// <summary>The execution gate shared by both real step-attempt probes; missing evidence never passes.</summary>
internal static class RequiredStepAttemptEvidence
{
    internal const int MaxDiagnosticChars = 1_500;

    internal static void RequireMode(string? mode)
    {
        if (mode != "1")
        {
            Fail("mode", $"Set {RequiredRealProbeFactAttribute.EnvironmentVariable}=1 to require real probe execution.");
        }
    }

    internal static void RequirePin(CliPinResult pinCheck, string expectedVersion)
    {
        if (pinCheck is not CliPinResult.Ok)
        {
            Fail("cli-pin", $"No installed CLI matches ENGINE_PIN ({expectedVersion}); gate outcome: {pinCheck.GetType().Name}.");
        }
    }

    internal static void RequireProcess(SuiteProcessResult run, bool eventsExist, string outputTail)
    {
        // A RETRY timeout is INCONCLUSIVE even though each unmatched attempt is FAIL. The
        // production runner passes --fail-on-inconclusive, so this completed probe must exit 4.
        if (run.Termination != RunTermination.CompletedNormally || run.ExitCode != 4)
        {
            Fail("process", $"Expected CompletedNormally, exit 4; observed {run.Termination}, "
                + $"exit {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}. Output tail: {outputTail}");
        }

        if (!eventsExist)
        {
            Fail("events-file", $"The completed probe wrote no events file. Output tail: {outputTail}");
        }
    }

    internal static void RequireEvents(IReadOnlyList<JsonElement> events)
    {
        if (events.Any(e => !HasStringField(e, "type")))
        {
            Fail("events-shape", "The probe stream contains an event without an object/string type envelope.");
        }

        if (events.Any(e => FieldEquals(e, "type", "environment-error")))
        {
            Fail("events-environment", "The probe emitted environment-error; no successful topology execution is established.");
        }

        var retryAttempts = events.Where(e => FieldEquals(e, "type", "step-attempt")
            && FieldEquals(e, "stepId", "retry-probe")).ToArray();
        if (retryAttempts.Length == 0)
        {
            Fail("retry-attempts", "The required retry-probe produced no step-attempt events.");
        }

        if (retryAttempts.Any(e => !FieldEquals(e, "outcome", "FAIL")))
        {
            Fail("retry-attempts", "The required retry-probe must record FAIL for every unmatched attempt.");
        }

        RequireCompletion(events, "immediate-probe", "PASS");
        var retryCompleted = RequireCompletion(events, "retry-probe", "INCONCLUSIVE");
        if (!retryCompleted.TryGetProperty("observation", out var observation)
            || !FieldEquals(observation, "reason", "retry-timeout"))
        {
            Fail("retry-timeout", "The required retry-probe must complete with observation.reason=retry-timeout.");
        }

        var scenarioCompleted = events.Where(e => FieldEquals(e, "type", "scenario-completed")
            && FieldEquals(e, "scenarioId", "step-attempt envelope probe")).ToArray();
        if (scenarioCompleted.Length != 1 || !FieldEquals(scenarioCompleted[0], "verdict", "INCONCLUSIVE"))
        {
            Fail("scenario-completed", "The required probe scenario must complete exactly once with its expected INCONCLUSIVE verdict.");
        }
    }

    internal static void Fail(string stage, string detail)
    {
        // Bound before sanitising as well, since sanitising can expand control characters.
        var boundedDetail = detail.Length > MaxDiagnosticChars ? detail[..MaxDiagnosticChars] : detail;
        var diagnostic = Vouchfx.Mcp.TextSanitiser.SanitiseForDisplay(
            $"Required real probe evidence missing [{stage}]: {boundedDetail} "
            + "Check ENGINE_PIN, packaged Aspire DCP, and Docker Linux-container prerequisites.");
        if (diagnostic.Length > MaxDiagnosticChars)
        {
            diagnostic = diagnostic[..(MaxDiagnosticChars - 12)] + " [truncated]";
        }

        throw new XunitException(diagnostic);
    }

    private static JsonElement RequireCompletion(IReadOnlyList<JsonElement> events, string stepId, string verdict)
    {
        var completed = events.Where(e => FieldEquals(e, "type", "step-completed")
            && FieldEquals(e, "stepId", stepId)).ToArray();
        if (completed.Length != 1 || !FieldEquals(completed[0], "verdict", verdict))
        {
            Fail("step-completed", $"The required {stepId} must complete exactly once with its expected {verdict} verdict.");
        }

        return completed[0];
    }

    private static bool HasStringField(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool FieldEquals(JsonElement element, string field, string expected) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == expected;
}
