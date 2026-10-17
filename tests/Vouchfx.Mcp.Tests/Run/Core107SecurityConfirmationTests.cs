using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

public class Core107SecurityConfirmationTests
{
    // Mirrors SecurityConfirmationEvent and SecurityConfirmationEventEmissionTests at Core
    // 20d837a2f735c1c785db6593b58ebe10b3dd9f05. These are source-contract fixtures,
    // not evidence of a published CLI probe or a successful security confirmation.
    [Theory]
    [InlineData("TransportConfirmed", "mtls", true, false)]
    [InlineData("AuthenticatedRoundTrip", "mtls", true, false)]
    [InlineData("AuthenticatedRoundTrip", "tls", false, true)]
    public void RawRelay_PreservesSecurityConfirmationFacts(
        string level, string profile, bool identityResolved, bool replayed)
    {
        var line = Confirmation(level, profile, identityResolved, replayed);
        using var document = JsonDocument.Parse(line);

        var relayed = RawEventRelay.Relay(document.RootElement, RawEventRelay.ByteCountOf(line));

        Assert.False(relayed.Truncated);
        Assert.Equal(document.RootElement.EnumerateObject().Count(), relayed.Element.EnumerateObject().Count());
        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), relayed.Element.GetProperty(property.Name).GetRawText());
        }
    }

    [Theory]
    [InlineData("TransportConfirmed", "mtls", true, false)]
    [InlineData("AuthenticatedRoundTrip", "mtls", true, false)]
    [InlineData("AuthenticatedRoundTrip", "tls", false, true)]
    public void SummaryParser_SecurityConfirmationDoesNotInventAVerdictOrStep(
        string level, string profile, bool identityResolved, bool replayed)
    {
        var line = Confirmation(level, profile, identityResolved, replayed);

        var confirmationOnly = SuiteEventParser.Parse(line);
        Assert.Null(confirmationOnly.AggregateVerdict);
        Assert.Empty(confirmationOnly.Steps);
        Assert.Empty(confirmationOnly.EnvironmentErrors);

        var completed = SuiteEventParser.Parse(line + "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}""");
        Assert.Equal(RunVerdict.Inconclusive, completed.AggregateVerdict);
        Assert.Empty(completed.Steps);
        Assert.Empty(completed.EnvironmentErrors);
    }

    private static string Confirmation(string level, string profile, bool identityResolved, bool replayed) =>
        JsonSerializer.Serialize(new
        {
            v = 1,
            schemaVersion = "v1",
            type = "security-confirmation",
            ts = "2026-10-17T00:00:00+00:00",
            runId = "run",
            targetName = "api",
            targetKind = "service",
            declaredProfile = profile,
            declaredEndpoint = "https",
            observedAddress = "localhost:443",
            observedProtocol = "Tls13",
            clientIdentityResolved = identityResolved,
            level,
            replayed,
        });
}
