using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-05's three Gherkin scenarios, over the real MCP wire through
/// <see cref="McpTestHarness"/> and <see cref="FakeVouchfxCli"/>: the catalogue tools carry every
/// spec §5.2 <c>ProviderInfo</c> field this server can derive or relay, omit — never default — the
/// one that belongs to the provider hub, and change nothing about the shape they returned before.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two tools, still two tools.</b> Plan D4 keeps <c>list_step_types</c> (a cheap list) and
/// <c>describe_step_type</c> (an expensive per-type lookup) separate rather than merging them into
/// the spec's single <c>list_providers</c>; this file therefore asserts the enrichment on BOTH
/// surfaces, because a field added to one and forgotten on the other is the drift that split
/// invites. The one deliberate difference is <c>example</c>, which only the expensive lookup
/// carries.
/// </para>
/// <para>
/// <b>What engine v1.0.0-rc.6 changed here.</b> Upstream ask U5 landed: the engine's
/// <c>list --json</c> now reports <c>tier</c>, <c>supportedVerifyModes</c>, <c>docsUrl</c> and
/// <c>example</c>, and the default <see cref="RichListJsonFixture"/> carries them in the shape the
/// pinned engine emits for a Core type. The four fields this file used to assert ABSENT are now
/// asserted RELAYED; <c>vouched</c>, which the engine deliberately never emits, is the one field
/// still asserted absent.
/// </para>
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

    /// <summary>
    /// The properties the enrichment ADDS to a <c>describe_step_type</c> result for a type the
    /// engine reports every U5 member for: <c>requiredResources</c> (US-S2-05) and the four engine
    /// v1.0.0-rc.6 relays.
    /// </summary>
    private static readonly string[] DescribeEnrichment =
    [
        "requiredResources", "tier", "supportsVerifyMode", "docsUrl", "example",
    ];

    /// <summary>
    /// The same for one <c>list_step_types</c> entry — every relay except <c>example</c>, which
    /// would make the cheap list the expensive one.
    /// </summary>
    private static readonly string[] ListEntryEnrichment =
    [
        "requiredResources", "tier", "supportsVerifyMode", "docsUrl",
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

        // tier / supportsVerifyMode / docsUrl / example — RELAYED from the engine's own export
        // (engine v1.0.0-rc.6, upstream ask U5), exactly as the fixture's engine wrote them.
        // supportsVerifyMode is spec §5.2's boolean "RETRY-capable", read off the engine's
        // supportedVerifyModes list.
        Assert.Equal("core", payload.GetProperty("tier").GetString());
        Assert.True(payload.GetProperty("supportsVerifyMode").GetBoolean());
        Assert.Equal(
            "https://vouchfx.io/language-reference/#mq-expectkafka",
            payload.GetProperty("docsUrl").GetString());
        Assert.Equal(
            "steps:\n  - id: example\n    type: mq-expect.kafka\n",
            payload.GetProperty("example").GetString());

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
    public async Task DescribeStepType_PopulatesNoHubOwnedFieldWithAFabricatedValue()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await DescribeAsync(harness, "mq-expect.kafka", cts.Token);

        foreach (var absent in ProviderInfoContract.HubOwned)
        {
            Assert.False(
                payload.TryGetProperty(absent, out _),
                $"describe_step_type emitted the hub-owned field '{absent}'. It must be absent, not defaulted.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Scenario 2: the hub-owned field is documented as absent, not defaulted ─────────────────

    [Fact]
    public async Task ListStepTypes_NoEntryCarriesAHubOwnedField_OrTheExample()
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
            var type = entry.GetProperty("type").GetString();
            foreach (var absent in ProviderInfoContract.HubOwned)
            {
                Assert.False(
                    entry.TryGetProperty(absent, out _),
                    $"list_step_types emitted the hub-owned field '{absent}' on '{type}'. It must be "
                    + "absent, not defaulted.");
            }

            // The engine reports an example for every one of these types, and the list still leaves
            // it out: 25 whole suites would make the cheap list the expensive one.
            Assert.False(
                entry.TryGetProperty("example", out _),
                $"list_step_types carried the example suite on '{type}'; only describe_step_type does.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ListStepTypes_ATypeTheVendoredSchemaCannotAnswerFor_OmitsRequiredResourcesEntirely()
    {
        // m1 (second-reviewer follow-up): the null -> omitted arm had unit coverage
        // (RequiredResourceCatalogueTests) but zero WIRE coverage. A live catalogue that carries a
        // type the vendored schema does not define — the exact shape of the engine gaining a
        // provider ahead of a `sync-vendored.ps1 -Update` resync — must serialise that entry with NO
        // requiredResources key at all, and specifically NOT `"requiredResources": null`. This drives
        // such a type (mq-publish.pulsar, absent from the vendored catalogue) through the real MCP
        // wire and asserts the entry's property set is byte-for-byte the pre-story StepTypeSummary
        // shape.
        const string listJsonWithUnknownType = """
            {
              "schemaVersion": 1,
              "engineVersion": "1.0.0-pulsar-fixture",
              "stepTypes": [
                {
                  "type": "http.rest",
                  "family": "http",
                  "provider": "rest",
                  "requiredFields": ["method", "path", "target"],
                  "optionalFields": ["body", "expect", "headers"],
                  "captureSupported": true,
                  "familyIntent": "Call HTTP endpoints on services under test and assert responses."
                },
                {
                  "type": "mq-publish.pulsar",
                  "family": "mq-publish",
                  "provider": "pulsar",
                  "requiredFields": ["target", "topic"],
                  "optionalFields": ["payload"],
                  "captureSupported": true,
                  "familyIntent": "Publish messages to a broker."
                }
              ]
            }
            """;

        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cli = FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version),
            listJsonWithUnknownType);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var payload = await ListAsync(harness, cts.Token);
        var pulsar = payload.GetProperty("families").EnumerateArray()
            .SelectMany(f => f.GetProperty("types").EnumerateArray())
            .Single(e => e.GetProperty("type").GetString() == "mq-publish.pulsar");

        // No requiredResources key at all — not present as null, not present as [].
        Assert.False(
            pulsar.TryGetProperty("requiredResources", out _),
            "A type the vendored schema cannot answer for must omit requiredResources entirely, "
            + "never emit it as null or [].");

        // The property set is EXACTLY the shape a StepTypeSummary carried before this story. The
        // same fixture doubles as an engine older than v1.0.0-rc.6, which emits none of the U5
        // members: each is omitted as well, never defaulted.
        Assert.Equal(ListEntryShapeBeforeThisStory.Order(StringComparer.Ordinal), PropertyNames(pulsar));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task BothCatalogueTools_OmitEveryRelayTheEngineReportsAsNull()
    {
        // The engine's documented shape for a type it cannot answer every U5 member for (a
        // non-Core entry from a library caller): docsUrl and example null, tier null when no Core
        // set was supplied. supportedVerifyModes, which the engine never nulls, is nulled too, to
        // cover that relay's defensive arm. Each relay must be OMITTED from both tools' results —
        // never emitted as null, and never replaced by a default this server made up.
        const string listJsonWithNullRelays = """
            {
              "schemaVersion": 1,
              "engineVersion": "1.0.0-null-relay-fixture",
              "stepTypes": [
                {
                  "type": "http.rest",
                  "family": "http",
                  "provider": "rest",
                  "requiredFields": ["method", "path", "target"],
                  "optionalFields": ["body", "expect", "headers"],
                  "captureSupported": true,
                  "familyIntent": "Call HTTP endpoints on services under test and assert responses.",
                  "tier": null,
                  "supportedVerifyModes": null,
                  "docsUrl": null,
                  "example": null
                }
              ]
            }
            """;

        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cli = FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version),
            listJsonWithNullRelays);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var entry = Assert.Single(
            (await ListAsync(harness, cts.Token)).GetProperty("families").EnumerateArray()
                .SelectMany(f => f.GetProperty("types").EnumerateArray()));
        Assert.Equal(
            ListEntryShapeBeforeThisStory.Concat(["requiredResources"]).Order(StringComparer.Ordinal),
            PropertyNames(entry));

        var described = await DescribeAsync(harness, "http.rest", cts.Token);
        Assert.Equal(
            DescribeShapeBeforeThisStory.Concat(["requiredResources"]).Order(StringComparer.Ordinal),
            PropertyNames(described));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task BothCatalogueTools_NameTheRelayedFields_AndStateWhichFieldIsAbsentAndWhy()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);

        foreach (var (name, relays) in new[]
        {
            ("list_step_types", new[] { "tier", "supportsVerifyMode", "docsUrl" }),
            ("describe_step_type", new[] { "tier", "supportsVerifyMode", "docsUrl", "example" }),
        })
        {
            var description = Assert.Single(tools, t => t.Name == name).Description!;

            foreach (var relay in relays)
            {
                Assert.Contains(relay, description, StringComparison.Ordinal);
            }

            foreach (var absent in ProviderInfoContract.HubOwned)
            {
                Assert.Contains(absent, description, StringComparison.Ordinal);
            }

            // The notice must be the SHARED constant, spliced in verbatim — not prose re-typed per
            // tool. Pasting the sentence in by hand (dropping the `+ AbsentFieldsNotice`
            // composition) would still satisfy the field-name checks above but fail here, which is
            // the point: the single source is what keeps a field changing sides from stranding a
            // stale claim in a description.
            Assert.Contains(ProviderInfoContract.AbsentFieldsNotice, description, StringComparison.Ordinal);

            // U5 has landed. A description still calling any field pending it is the stale claim
            // engine v1.0.0-rc.6 retired.
            Assert.DoesNotContain("U5", description, StringComparison.Ordinal);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Scenario 3: enrichment does not change the existing successful-path shape ──────────────

    [Fact]
    public async Task ListStepTypes_KeepsEveryPreExistingProperty_AndAddsOnlyTheProviderInfoFields()
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
                ListEntryShapeBeforeThisStory.Concat(ListEntryEnrichment).Order(StringComparer.Ordinal),
                PropertyNames(entry));
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_KeepsEveryPreExistingProperty_AndAddsOnlyTheProviderInfoFields()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var payload = await DescribeAsync(harness, "db-assert.postgres", cts.Token);

        Assert.Equal(
            DescribeShapeBeforeThisStory.Concat(DescribeEnrichment).Order(StringComparer.Ordinal),
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
