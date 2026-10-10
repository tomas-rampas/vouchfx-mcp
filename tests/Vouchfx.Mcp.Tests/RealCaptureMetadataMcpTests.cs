using System.Text.Json;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Tests.Diagnosis;

namespace Vouchfx.Mcp.Tests;

public sealed class RealCaptureMetadataMcpTests
{
    private static readonly string[] ReasonPropertyNames = ["kind", "hint"];

    [Fact]
    public async Task CapturePathAndVerifiedProposal_ReachTheActualMcpWireWithoutNewFieldsOrValues()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-capture-wire-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var validEvent = CaptureMetadataDiagnosticsTests.MeasuredRc5Step.Replace(
            "\"matched\":false", "\"matched\":false,\"value\":\"CAPTURE_WIRE_VALUE_CANARY\"", StringComparison.Ordinal);
        const string envelope = """
            {"type":"reproducibility-envelope","secretReferences":["${secret:env/WIRE_ENVELOPE_REFERENCE_CANARY}"]}
            """;
        const string unsafeName = "missing-é-${secret:env/UNRESOLVED_CAPTURE_REFERENCE}";
        const string unsafeId = "declaring-é";
        var unsafeMetadata = JsonSerializer.Serialize(new[]
        {
            new { name = unsafeName, path = "$.own", matched = false, value = "CAPTURE_WIRE_VALUE_CANARY" },
        });
        var oversized = CaptureMetadataDiagnosticsTests.Event("oversized", """[{"name":"missing","path":"$.own","matched":false,"value":"CAPTURE_WIRE_VALUE_CANARY"}]""");
        oversized = oversized[..^1] + ",\"future\":\"" + new string('x', CaptureMetadataParser.MaxEventChars) + "\"}";
        var cases = new (string Events, string? ExpectedPath, bool ExpectedProposal)[]
        {
            (validEvent, "$.doesNotExist", true),
            (CaptureMetadataDiagnosticsTests.Event(unsafeId, unsafeMetadata, unsafeName), "$.own", false),
            (CaptureMetadataDiagnosticsTests.Event("malformed", """[{"name":"missing","path":"$.own","matched":"false","value":"CAPTURE_WIRE_VALUE_CANARY"}]"""), null, false),
            (CaptureMetadataDiagnosticsTests.Event("ambiguous", """[{"name":"missing","path":"$.own","matched":false},{"name":"missing","path":"$.other","matched":true}]"""), null, false),
            (oversized, null, false),
        };
        try
        {
            foreach (var sample in cases)
            {
                await File.WriteAllTextAsync(path, envelope + "\n" + sample.Events, cts.Token);
                foreach (var tool in new[] { "explain_run", "diagnose_run" })
                {
                    var response = await harness.Client.CallToolAsync(tool,
                        new Dictionary<string, object?> { ["eventsPath"] = path }, cancellationToken: cts.Token);
                    Assert.False(response.IsError ?? false);
                    var payload = response.StructuredContent!.Value;
                    var diagnosis = tool == "diagnose_run" ? payload.GetProperty("diagnosis") : payload;
                    var step = Assert.Single(diagnosis.GetProperty("notableSteps").EnumerateArray());
                    var reason = step.GetProperty("reason");
                    Assert.Equal(ReasonPropertyNames, reason.EnumerateObject().Select(property => property.Name));
                    var hint = reason.GetProperty("hint").GetString()!;
                    if (sample.ExpectedPath is { } expectedPath)
                    {
                        Assert.Contains(expectedPath, hint, StringComparison.Ordinal);
                    }
                    else
                    {
                        Assert.DoesNotContain("$.own", hint, StringComparison.Ordinal);
                        Assert.Contains("reported unmet", hint, StringComparison.Ordinal);
                    }

                    if (sample.Events.Contains(unsafeId, StringComparison.Ordinal))
                    {
                        Assert.Equal(TextSanitiser.SanitiseForDisplay(unsafeId), step.GetProperty("stepId").GetString());
                        Assert.Contains("\\u00e9", hint, StringComparison.Ordinal);
                    }

                    Assert.False(step.TryGetProperty("captured", out _));
                    Assert.DoesNotContain("CAPTURE_WIRE_VALUE_CANARY", payload.GetRawText(), StringComparison.Ordinal);
                    Assert.DoesNotContain("WIRE_ENVELOPE_REFERENCE_CANARY", payload.GetRawText(), StringComparison.Ordinal);
                    Assert.DoesNotContain("secretReferences", payload.GetRawText(), StringComparison.Ordinal);
                    Assert.DoesNotContain("captureDeclarations", payload.GetRawText(), StringComparison.Ordinal);
                    Assert.DoesNotContain("captureTargetVerified", payload.GetRawText(), StringComparison.Ordinal);
                    if (tool == "diagnose_run")
                    {
                        var proposals = payload.GetProperty("specEditProposals").EnumerateArray().ToArray();
                        if (sample.ExpectedProposal)
                        {
                            var proposal = Assert.Single(proposals);
                            Assert.Equal("capture-present-and-missing", proposal.GetProperty("stepId").GetString());
                            Assert.Equal("capture", proposal.GetProperty("scope").GetString());
                            Assert.Contains("$.doesNotExist", proposal.GetProperty("suggestedEdit").GetString(), StringComparison.Ordinal);
                        }
                        else
                        {
                            Assert.Empty(proposals);
                        }
                    }
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

