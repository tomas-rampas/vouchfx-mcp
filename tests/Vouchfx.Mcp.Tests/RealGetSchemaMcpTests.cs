using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-01's <c>get_schema</c> behaviour over the REAL MCP wire protocol (the in-memory
/// paired-stream harness — see <see cref="McpTestHarness"/>), which is what "Real*" means in this
/// repo's test names: the real server, the real JSON-RPC framing, the real result shape a host
/// receives — never the real <c>vouchfx</c> engine CLI.
/// </summary>
/// <remarks>
/// The real-CLI half of the story's acceptance criteria lives in
/// <see cref="RealGetSchemaAgainstPinnedCliTests"/>, which self-gates on the pinned CLI's presence
/// the same way <see cref="RealPlanCoverageAgainstPinnedCliTests"/> does.
/// </remarks>
public class RealGetSchemaMcpTests
{
    /// <summary>
    /// The eight KB the acceptance criteria bound <c>format: "summary"</c> at, restated here
    /// against the wire rather than against the renderer (<c>SchemaSummaryRendererTests</c> covers
    /// the renderer): the promise is about what a host actually receives.
    /// </summary>
    private const int MaxSummaryBytes = 8 * 1024;

    // ── Offline (no CLI at all) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSchema_WithNoCliInstalled_ReturnsTheEmbeddedVendoredSchemaSuccessfully()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema", new Dictionary<string, object?>(), cancellationToken: cts.Token);

        // CLI-OPTIONAL, not CLI-backed: a missing engine is not an error for this tool.
        Assert.False(result.IsError ?? false);

        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent from get_schema.");

        Assert.Equal("full", payload.GetProperty("section").GetString());
        Assert.Equal(VendoredSchemaVersion.Value, payload.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
            SchemaJsonCanonicaliser.Canonicalise(payload.GetProperty("jsonSchema").GetRawText()));

        // `summary` and `diagnostics` are omitted, not emitted as null — the acceptance shape is
        // { meta, schemaVersion, section, jsonSchema? , summary? }.
        Assert.False(payload.TryGetProperty("summary", out _));
        Assert.False(payload.TryGetProperty("diagnostics", out _));

        // US-S1-02's provenance stamp rides every successful result, get_schema included.
        var meta = payload.GetProperty("meta");
        Assert.Equal(VendoredSchemaVersion.Value, meta.GetProperty("schemaVersion").GetString());
        Assert.Equal(ServerIdentity.Version, meta.GetProperty("serverVersion").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_StepSection_ReturnsOnlyThatStepDefinitionAndEchoesTheSection()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema",
            new Dictionary<string, object?> { ["section"] = "step:mq-expect.kafka" },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent!.Value;

        Assert.Equal("step:mq-expect.kafka", payload.GetProperty("section").GetString());

        var jsonSchema = payload.GetProperty("jsonSchema").GetRawText();
        Assert.Contains("mq-expect.kafka", jsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("http.rest", jsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("db-assert.postgres", jsonSchema, StringComparison.Ordinal);

        // A subtree, emphatically not the whole document.
        Assert.True(
            jsonSchema.Length < VendoredComposedSchema.RawJson.Length / 2,
            "Expected a step subtree far smaller than the whole composed schema.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_UnknownStepProvider_ReturnsASchemaValidationRangeErrorNotAnEmptySuccess()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema",
            new Dictionary<string, object?> { ["section"] = "step:mq-expect.nonexistent-provider" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);

        var error = result.StructuredContent!.Value;
        var code = error.GetProperty("code").GetString()!;
        Assert.Equal(VfxCodeCatalogue.SchemaSectionNotFound, code);

        // The acceptance criterion is about the RANGE, not just the constant: 1100-1199 is the
        // schema-validation area (spec §4.4), and this failure is about addressing the schema.
        Assert.StartsWith("VFX-E-", code, StringComparison.Ordinal);
        var codeNumber = int.Parse(code["VFX-E-".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(codeNumber, 1100, 1199);

        Assert.Contains("mq-expect.nonexistent-provider", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_UnknownNamedSection_ReturnsTheSameSchemaValidationRangeError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema",
            new Dictionary<string, object?> { ["section"] = "definitely-not-a-section" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        Assert.Equal(VfxCodeCatalogue.SchemaSectionNotFound, result.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_UnknownFormat_ReturnsInvalidToolArgument()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema",
            new Dictionary<string, object?> { ["format"] = "yaml" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        // A rejected argument value is the pre-existing InvalidToolArgument condition, not a new
        // code: the caller must change the call, exactly as for every other tool.
        Assert.Equal(VfxCodeCatalogue.InvalidToolArgument, result.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── format: "summary" ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("full")]
    [InlineData("metadata")]
    [InlineData("environment")]
    [InlineData("variables")]
    [InlineData("steps")]
    [InlineData("step:http.rest")]
    public async Task GetSchema_SummaryFormat_ReturnsAMarkdownDigestUnderEightKilobytesWithNoInventedProse(string section)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema",
            new Dictionary<string, object?> { ["section"] = section, ["format"] = "summary" },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent!.Value;

        Assert.Equal(section, payload.GetProperty("section").GetString());

        var summary = payload.GetProperty("summary").GetString()!;
        Assert.NotEmpty(summary);
        Assert.True(
            Encoding.UTF8.GetByteCount(summary) <= MaxSummaryBytes,
            $"Summary for '{section}' was {Encoding.UTF8.GetByteCount(summary)} bytes, over {MaxSummaryBytes}.");

        // No annotation content is invented: an unannotated field is omitted, never filled with
        // filler prose. These are the filler strings a naive renderer would reach for.
        foreach (var placeholder in new[] { "TODO", "No description", "no description available", "(none)" })
        {
            Assert.DoesNotContain(placeholder, summary, StringComparison.Ordinal);
        }

        // `jsonSchema` is omitted in summary format — asking for a digest and receiving the whole
        // 150 KB document alongside it would defeat the point of the digest.
        Assert.False(payload.TryGetProperty("jsonSchema", out _));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_SummaryForAStepType_QuotesTheEnginesOwnDescriptionVerbatim()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "get_schema",
            new Dictionary<string, object?> { ["section"] = "step:mq-expect.kafka", ["format"] = "summary" },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var summary = result.StructuredContent!.Value.GetProperty("summary").GetString()!;

        var kafka = StepTypeCatalogue.Find("mq-expect.kafka")
            ?? throw new InvalidOperationException("The vendored schema no longer defines mq-expect.kafka.");
        Assert.NotNull(kafka.Description);
        Assert.Contains(kafka.Description!, summary, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Live mode: cross-verification against `vouchfx schema` ──────────────────────────────────

    [Fact]
    public async Task GetSchema_WhenThePinnedCliAgreesWithTheVendoredSchema_ReturnsNoDiagnostics()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cli = FakeVouchfxCli.WithExports(
            CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version),
            listJson: RichListJsonFixture.Json,
            schemaJson: VendoredComposedSchema.RawJson);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var result = await harness.Client.CallToolAsync(
            "get_schema", new Dictionary<string, object?>(), cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.False(result.StructuredContent!.Value.TryGetProperty("diagnostics", out _));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_WhenThePinnedCliDisagreesWithTheVendoredSchema_SurfacesADiagnosticNotASilentOverride()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        const string DivergentSchema = """
            { "x-vouchfx-schema-version": "v1", "type": "object", "properties": { "somethingElse": {} } }
            """;

        var cli = FakeVouchfxCli.WithExports(
            CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version),
            listJson: RichListJsonFixture.Json,
            schemaJson: DivergentSchema);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var result = await harness.Client.CallToolAsync(
            "get_schema", new Dictionary<string, object?>(), cancellationToken: cts.Token);

        // A mismatch is DATA on a successful call (spec §4.4's diagnostics-are-not-errors rule),
        // never isError — the caller still gets a usable schema.
        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent!.Value;

        var diagnostics = payload.GetProperty("diagnostics").EnumerateArray().ToArray();
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(VfxCodeCatalogue.LiveSchemaMismatch, diagnostic.GetProperty("code").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(
            VfxCodeCatalogue.DocsUrlFor(VfxCodeCatalogue.LiveSchemaMismatch),
            diagnostic.GetProperty("docsUrl").GetString());

        // Not a silent override in EITHER direction: the served schema is still the vendored one.
        Assert.Equal(
            SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
            SchemaJsonCanonicaliser.Canonicalise(payload.GetProperty("jsonSchema").GetRawText()));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_DiagnosticCodeIsCatalogued_SoExplainDiagnosticCanExplainIt()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // The whole point of US-S1-04/05's catalogue: any code this tool can emit is explainable
        // through the same server, without the host leaving MCP.
        foreach (var code in new[] { VfxCodeCatalogue.LiveSchemaMismatch, VfxCodeCatalogue.SchemaSectionNotFound })
        {
            var result = await harness.Client.CallToolAsync(
                "explain_diagnostic",
                new Dictionary<string, object?> { ["code"] = code },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            Assert.Equal(code, result.StructuredContent!.Value.GetProperty("code").GetString());
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Read-only / hygiene ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSchema_AdvertisesTwoOptionalStringArgumentsAndNothingElse()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var tool = Assert.Single(tools, t => t.Name == "get_schema");

        var schema = tool.ProtocolTool.InputSchema;
        Assert.False(
            schema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0,
            "get_schema's arguments are all optional.");

        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("section", out var sectionSchema));
        Assert.True(properties.TryGetProperty("format", out var formatSchema));
        Assert.True(SchemaTypeIncludes(sectionSchema, "string"));
        Assert.True(SchemaTypeIncludes(formatSchema, "string"));

        // No free-text/path parameters: this tool never touches the filesystem or a model.
        Assert.False(properties.TryGetProperty("path", out _));
        Assert.False(properties.TryGetProperty("prompt", out _));

        Assert.False(string.IsNullOrWhiteSpace(tool.Description));

        // An MCP input schema cannot express "one of these five names, OR 'step:' plus any dotted
        // type name", so the accepted tokens are restated in prose for the host LLM. That prose is a
        // SECOND copy of SchemaSectionResolver.NamedSections / GetSchemaOrchestrator.Formats, and
        // this is what keeps it from drifting from the list the server actually switches on.
        var sectionDescription = sectionSchema.GetProperty("description").GetString()!;
        foreach (var named in SchemaSectionResolver.NamedSections)
        {
            Assert.Contains(named, sectionDescription, StringComparison.Ordinal);
        }

        Assert.Contains(SchemaSectionResolver.StepSectionPrefix, sectionDescription, StringComparison.Ordinal);

        var formatDescription = formatSchema.GetProperty("description").GetString()!;
        foreach (var supported in GetSchemaOrchestrator.Formats)
        {
            Assert.Contains(supported, formatDescription, StringComparison.Ordinal);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>Mirrors <c>McpServerSkeletonTests.SchemaTypeIncludes</c> — a nullable parameter's
    /// advertised <c>type</c> is legitimately either a bare string or a union array.</summary>
    private static bool SchemaTypeIncludes(JsonElement property, string expectedType)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == expectedType,
            JsonValueKind.Array => type.EnumerateArray().Any(t => t.GetString() == expectedType),
            _ => false,
        };
    }
}
