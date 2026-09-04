using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-05's three Gherkin scenarios, over the real MCP wire through
/// <see cref="McpTestHarness"/> and <see cref="FakeVouchfxCli"/>: the catalogue tools carry every
/// spec §5.2 <c>ProviderInfo</c> field this server can derive, omit — never default — the ones
/// pending upstream ask U5, and change nothing about the shape they returned before.
/// </summary>
/// <remarks>
/// <b>Two tools, still two tools.</b> Plan D4 keeps <c>list_step_types</c> (a cheap list) and
/// <c>describe_step_type</c> (an expensive per-type lookup) separate rather than merging them into
/// the spec's single <c>list_providers</c>; this file therefore asserts the enrichment on BOTH
/// surfaces, because a field added to one and forgotten on the other is the drift that split
/// invites.
/// </remarks>
public class RealCatalogueEnrichmentMcpTests
{
    /// <summary>
    /// Every top-level property <c>describe_step_type</c> returned BEFORE US-S2-05, transcribed as
    /// a literal golden rather than derived from the record — a derived list would rename itself
    /// alongside any breaking edit and prove nothing.
    /// </summary>
    private static readonly string[] DescribeShapeBeforeThisStory =
    [
        "type", "family", "provider", "description", "fields", "requiredOneOf",
        "requiredFields", "optionalFields", "captureSupported", "familyIntent", "meta",
    ];

    /// <summary>The same golden for one entry of <c>list_step_types</c>' per-family type array.</summary>
    private static readonly string[] ListEntryShapeBeforeThisStory =
    [
        "type", "provider", "description", "captureSupported", "familyIntent",
    ];

    // ── Scenario 1: derivable fields appear without an engine change ───────────────────────────

    [Fact]
    public async Task DescribeStepType_MqExpectKafka_CarriesEveryDerivableProviderInfoField()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await DescribeAsync(harness, "mq-expect.kafka", cts.Token);

        // stepType / family / provider — spec §5.2's identity trio. This server has always spelled
        // ProviderInfo.stepType as "type" (the dotted name the engine itself uses); the name is
        // load-bearing for existing hosts, so it is NOT duplicated under a second spelling.
        Assert.Equal("mq-expect.kafka", payload.GetProperty("type").GetString());
        Assert.Equal("mq-expect", payload.GetProperty("family").GetString());
        Assert.Equal("kafka", payload.GetProperty("provider").GetString());

        // summary — carried as familyIntent (and mirrored on description), from the live catalogue.
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("familyIntent").GetString()));

        // parameters — carried as the requiredFields/optionalFields/fields triple.
        Assert.NotEmpty(payload.GetProperty("fields").EnumerateArray());
        Assert.NotEmpty(payload.GetProperty("requiredFields").EnumerateArray());

        // requiredResources — NEW, and genuinely derived: the vendored schema's step-type set
        // crossed with the step-type -> dependency-kind table. Not a guess, not a default.
        Assert.Equal(
            ["kafka"],
            payload.GetProperty("requiredResources").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_HttpRest_ReportsAnEmptyRequiredResources_NotAnOmittedOne()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await DescribeAsync(harness, "http.rest", cts.Token);

        // "Needs no dependency kind" is a DERIVED answer for this type, so it is stated. Only a
        // type this repo cannot answer for gets the field omitted (RequiredResourceCatalogueTests).
        Assert.True(payload.TryGetProperty("requiredResources", out var resources));
        Assert.Empty(resources.EnumerateArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_PopulatesNoU5GatedFieldWithAFabricatedValue()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await DescribeAsync(harness, "mq-expect.kafka", cts.Token);

        foreach (var gated in ProviderInfoContract.U5Gated)
        {
            Assert.False(
                payload.TryGetProperty(gated, out _),
                $"describe_step_type emitted the U5-gated field '{gated}'. It must be absent, not defaulted.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Scenario 2: U5-gated fields are documented as absent, not defaulted ────────────────────

    [Fact]
    public async Task ListStepTypes_NoEntryCarriesAnyU5GatedField()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await ListAsync(harness, cts.Token);
        var entries = payload.GetProperty("families").EnumerateArray()
            .SelectMany(f => f.GetProperty("types").EnumerateArray())
            .ToArray();

        Assert.Equal(25, entries.Length);

        foreach (var entry in entries)
        {
            foreach (var gated in ProviderInfoContract.U5Gated)
            {
                Assert.False(
                    entry.TryGetProperty(gated, out _),
                    $"list_step_types emitted the U5-gated field '{gated}' on "
                    + $"'{entry.GetProperty("type").GetString()}'. It must be absent, not defaulted.");
            }
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task BothCatalogueTools_StateThatTheGatedFieldsAwaitUpstreamAskU5()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);

        foreach (var name in new[] { "list_step_types", "describe_step_type" })
        {
            var description = Assert.Single(tools, t => t.Name == name).Description!;

            Assert.Contains("U5", description, StringComparison.Ordinal);
            foreach (var gated in ProviderInfoContract.U5Gated)
            {
                Assert.Contains(gated, description, StringComparison.Ordinal);
            }
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Scenario 3: enrichment does not change the existing successful-path shape ──────────────

    [Fact]
    public async Task ListStepTypes_KeepsEveryPreExistingProperty_AndAddsOnlyRequiredResources()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await ListAsync(harness, cts.Token);

        Assert.Equal(["families", "meta"], PropertyNames(payload));

        var family = payload.GetProperty("families").EnumerateArray().First();
        Assert.Equal(["family", "familyIntent", "types"], PropertyNames(family));

        foreach (var entry in payload.GetProperty("families").EnumerateArray()
                     .SelectMany(f => f.GetProperty("types").EnumerateArray()))
        {
            Assert.Equal(
                ListEntryShapeBeforeThisStory.Concat(["requiredResources"]).Order(StringComparer.Ordinal),
                PropertyNames(entry));
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_KeepsEveryPreExistingProperty_AndAddsOnlyRequiredResources()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await DescribeAsync(harness, "db-assert.postgres", cts.Token);

        Assert.Equal(
            DescribeShapeBeforeThisStory.Concat(["requiredResources"]).Order(StringComparer.Ordinal),
            PropertyNames(payload));

        // Same MEANING, not merely the same names: requiredOneOf is still the null it always was
        // for a live-catalogue entry, and captureSupported is still the engine's own boolean.
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("requiredOneOf").ValueKind);
        Assert.True(payload.GetProperty("captureSupported").GetBoolean());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_UnknownType_StillFailsClosedWithTheSameCode()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // Enrichment is additive to the SUCCESS path only — the error behaviour of both tools is
        // untouched, and stays fail-closed.
        var result = await harness.Client.CallToolAsync(
            "describe_step_type",
            new Dictionary<string, object?> { ["type"] = "mq-expect.pulsar" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var error = Structured(result);
        Assert.Equal("VFX-E-1250", error.GetProperty("code").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static async Task<JsonElement> ListAsync(McpTestHarness harness, CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync("list_step_types", cancellationToken: cancellationToken);
        Assert.False(result.IsError ?? false);
        return Structured(result);
    }

    private static async Task<JsonElement> DescribeAsync(
        McpTestHarness harness,
        string type,
        CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(
            "describe_step_type",
            new Dictionary<string, object?> { ["type"] = type },
            cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false);
        return Structured(result);
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static JsonElement Structured(CallToolResult result) =>
        result.StructuredContent ?? throw new InvalidOperationException("Result carried no structured content.");
}
