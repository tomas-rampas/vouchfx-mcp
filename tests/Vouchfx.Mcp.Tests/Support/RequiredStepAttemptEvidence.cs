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
        // The suite intentionally fails its RETRY assertion, so 1 is its expected completed exit.
        if (run.Termination != RunTermination.CompletedNormally || run.ExitCode != 1)
        {
            Fail("process", $"Expected CompletedNormally, exit 1; observed {run.Termination}, "
                + $"exit {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}. Output tail: {outputTail}");
        }

        if (!eventsExist)
        {
            Fail("events-file", $"The completed probe wrote no events file. Output tail: {outputTail}");
        }
    }

    internal static void RequireEvents(IReadOnlyList<JsonElement> events)
    {
        if (events.Any(e => FieldEquals(e, "type", "environment-error")))
        {
            Fail("events-environment", "The probe emitted environment-error; no successful topology execution is established.");
        }

        if (!events.Any(e => FieldEquals(e, "type", "step-attempt") && FieldEquals(e, "stepId", "retry-probe")))
        {
            Fail("retry-attempts", "The required retry-probe produced no step-attempt events.");
        }

        RequireCompletion(events, "immediate-probe", "PASS");
        RequireCompletion(events, "retry-probe", "FAIL");

        if (!events.Any(e => FieldEquals(e, "type", "scenario-completed")
            && FieldEquals(e, "scenarioId", "step-attempt envelope probe") && FieldEquals(e, "verdict", "FAIL")))
        {
            Fail("scenario-completed", "The required probe scenario did not complete with its expected FAIL verdict.");
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

    private static void RequireCompletion(IReadOnlyList<JsonElement> events, string stepId, string verdict)
    {
        if (!events.Any(e => FieldEquals(e, "type", "step-completed")
            && FieldEquals(e, "stepId", stepId) && FieldEquals(e, "verdict", verdict)))
        {
            Fail("step-completed", $"The required {stepId} did not complete with its expected {verdict} verdict.");
        }
    }

    private static bool FieldEquals(JsonElement element, string field, string expected) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == expected;
}
