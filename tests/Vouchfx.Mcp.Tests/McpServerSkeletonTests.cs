using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers the todo-2 / REQ-002 server skeleton: the MCP initialize handshake and the tool registry
/// (all thirteen tools advertised with the right names, descriptions, and input schemas). All tools
/// are real — including Spec D <c>plan_coverage</c>, Spec B <c>scaffold_suite</c>, Spec C
/// <c>diagnose_run</c>, US-S1-05's <c>explain_diagnostic</c>, US-S2-01's <c>get_schema</c>,
/// US-S2-04's <c>normalize_suite</c>, and US-S3-05's <c>get_run_events</c> — see
/// <c>Real*McpTests</c> for behavioural coverage.
/// </summary>
/// <remarks>
/// Drives the server the same way production does — via <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/>
/// — but over an in-memory paired-stream transport (<c>WithStreamServerTransport</c> +
/// <see cref="StreamClientTransport"/> over a pair of <c>Pipe</c>s, see <see cref="McpTestHarness"/>)
/// instead of real stdio, per the SDK's own documented testing pattern. Because the pipe carries
/// nothing but the JSON-RPC frames the transport writes to it, a stray write to stdout can only
/// be caught by separately capturing <see cref="Console.Out"/> — see <see cref="ConsoleOutCapture"/>.
/// </remarks>
public class McpServerSkeletonTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "describe_step_type",
        "diagnose_run",
        "explain_diagnostic",
        "explain_run",
        "get_run_events",
        "get_schema",
        "list_step_types",
        "normalize_suite",
        "plan_coverage",
        "run_suite",
        "scaffold_suite",
        "search_docs",
        "validate_suite",
    ];

    [Fact]
    public async Task Initialize_ReportsConfiguredServerNameAndVersion()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        Assert.Equal(ServerIdentity.Name, harness.Client.ServerInfo.Name);
        Assert.Equal(ServerIdentity.Version, harness.Client.ServerInfo.Version);

        // Also pin the literal <Version> from Vouchfx.Mcp.csproj: comparing only against
        // ServerIdentity.Version would pass even if that csproj property were edited to
        // something else, since ServerIdentity always reflects whatever it currently is.
        Assert.Equal("0.1.0", harness.Client.ServerInfo.Version);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ListTools_ReturnsExactlyTheThirteenAdvertisedTools()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var actualNames = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(ExpectedToolNames, actualNames);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("validate_suite")]
    [InlineData("list_step_types")]
    [InlineData("describe_step_type")]
    [InlineData("search_docs")]
    [InlineData("plan_coverage")]
    [InlineData("scaffold_suite")]
    [InlineData("run_suite")]
    [InlineData("explain_run")]
    [InlineData("diagnose_run")]
    [InlineData("explain_diagnostic")]
    [InlineData("get_schema")]
    [InlineData("normalize_suite")]
    [InlineData("get_run_events")]
    public async Task EveryTool_HasNonEmptyDescription(string toolName)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var tool = Assert.Single(tools, t => t.Name == toolName);

        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ValidateSuite_Schema_HasOptionalPathYamlAndLevel()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "validate_suite", cts.Token);

        // US-S2-02: `path` stopped being REQUIRED when `yaml` joined it, because the real rule is
        // "exactly one of the two" — which JSON Schema's `required` cannot express as a keyword
        // that MCP hosts reliably honour, and which this server therefore enforces itself and
        // reports as VFX-E-1152. Marking either one required would make the other unusable.
        Assert.Empty(GetRequired(schema));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "path"), "string"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "yaml"), "string"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "level"), "string"));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_Schema_HasOptionalPathYamlAndNormalizeAndNoLevel()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "normalize_suite", cts.Token);

        // Same "exactly one of path/yaml" rule as validate_suite, enforced the same way and reported
        // as the same VFX-E-1152 — so, for the same reason, neither is marked required here either.
        Assert.Empty(GetRequired(schema));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "path"), "string"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "yaml"), "string"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "normalize"), "boolean"));

        // No `level`: normalize_suite always validates at "full". A caller choosing "schema" would
        // silence the VFX-D-1207 secret-literal check on a result whose whole purpose is text a host
        // may write back over the author's file (US-S2-04's third Gherkin scenario).
        var props = schema.GetProperty("properties");
        Assert.False(props.TryGetProperty("level", out _), "normalize_suite must not accept a level.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ListStepTypes_Schema_HasNoRequiredParameters()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "list_step_types", cts.Token);

        Assert.Empty(GetRequired(schema));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DescribeStepType_Schema_HasRequiredStringType()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "describe_step_type", cts.Token);

        Assert.Equal(["type"], GetRequired(schema));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "type"), "string"));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainDiagnostic_Schema_HasRequiredStringCode()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "explain_diagnostic", cts.Token);

        Assert.Equal(["code"], GetRequired(schema));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "code"), "string"));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task SearchDocs_Schema_HasRequiredStringQuery()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "search_docs", cts.Token);

        Assert.Equal(["query"], GetRequired(schema));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "query"), "string"));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// <c>run_suite</c> v2's input surface (US-S3-02): <c>path</c> is no longer REQUIRED, because
    /// <c>paths</c> is the alternative — the exactly-one-of rule between them is a runtime check
    /// (<c>VFX-E-1503</c>) that no JSON Schema keyword the SDK generates can express, exactly as
    /// <c>validate_suite</c>'s <c>path</c>/<c>yaml</c> pair already is.
    /// </summary>
    /// <remarks>
    /// The <c>required</c> assertion is deliberately EMPTY-and-exact rather than "does not contain
    /// path": an empty required set is the whole point of the change and a future edit that made any
    /// argument mandatory again would break every caller using the other input shape, which is worth
    /// failing here rather than discovering on the wire.
    /// </remarks>
    [Fact]
    public async Task RunSuite_Schema_HasNoRequiredArgumentsAndCarriesBothPathInputs()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "run_suite", cts.Token);
        var required = GetRequired(schema);

        Assert.Empty(required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "path"), "string"));

        var pathsSchema = GetProperty(schema, "paths");
        Assert.True(SchemaTypeIncludes(pathsSchema, "array"));
        Assert.True(SchemaTypeIncludes(pathsSchema.GetProperty("items"), "string"));

        var tagsSchema = GetProperty(schema, "tags");
        Assert.True(SchemaTypeIncludes(tagsSchema, "array"));
        Assert.True(SchemaTypeIncludes(tagsSchema.GetProperty("items"), "string"));

        Assert.True(SchemaTypeIncludes(GetProperty(schema, "timeoutSeconds"), "integer"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "labels"), "object"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "keepEnvironment"), "boolean"));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "wait"), "boolean"));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainRun_Schema_HasOptionalStringEventsPath()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "explain_run", cts.Token);
        var required = GetRequired(schema);

        Assert.DoesNotContain("eventsPath", required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "eventsPath"), "string"));
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DiagnoseRun_Schema_HasOptionalStringEventsPathAndNoFreeTextOrSuitePathRequired()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "diagnose_run", cts.Token);
        var required = GetRequired(schema);

        Assert.DoesNotContain("eventsPath", required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "eventsPath"), "string"));

        // Free text is NOT a tool parameter (Spec C / REQ-004); suite path is optional / not required for v1.
        var props = schema.GetProperty("properties");
        Assert.False(props.TryGetProperty("prompt", out _), "diagnose_run must not accept free-text prompt.");
        Assert.False(props.TryGetProperty("goal", out _), "diagnose_run must not accept free-text goal.");
        Assert.False(props.TryGetProperty("freeText", out _), "diagnose_run must not accept freeText.");
        Assert.DoesNotContain("suitePath", required);
        Assert.DoesNotContain("path", required);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ScaffoldSuite_Schema_HasRequiredStepsArray()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "scaffold_suite", cts.Token);
        var required = GetRequired(schema);

        Assert.Contains("steps", required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "steps"), "array"));
        Assert.DoesNotContain("services", required);
        Assert.DoesNotContain("dependencies", required);

        // Free text is NOT a tool parameter (Spec B / REQ-007).
        var props = schema.GetProperty("properties");
        Assert.False(props.TryGetProperty("prompt", out _), "scaffold_suite must not accept free-text prompt.");
        Assert.False(props.TryGetProperty("goal", out _), "scaffold_suite must not accept free-text goal.");
        Assert.False(props.TryGetProperty("freeText", out _), "scaffold_suite must not accept freeText.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PlanCoverage_Schema_HasRequiredStringPathAndNoFreeTextParameter()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "plan_coverage", cts.Token);
        var required = GetRequired(schema);

        Assert.Equal(["path"], required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "path"), "string"));

        Assert.DoesNotContain("eventsPath", required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "eventsPath"), "string"));

        // Optional threshold overrides — all integers, none required.
        foreach (var thresholdProperty in new[]
                 {
                     "staleDays", "flakyMinRuns", "fragileMinEnvErrors", "inconclusiveMin",
                 })
        {
            Assert.DoesNotContain(thresholdProperty, required);
            Assert.True(SchemaTypeIncludes(GetProperty(schema, thresholdProperty), "integer"));
        }

        // REQ-012: the tool schema contains no free-text/goal parameter.
        var props = schema.GetProperty("properties");
        Assert.False(props.TryGetProperty("prompt", out _), "plan_coverage must not accept free-text prompt.");
        Assert.False(props.TryGetProperty("goal", out _), "plan_coverage must not accept free-text goal.");
        Assert.False(props.TryGetProperty("freeText", out _), "plan_coverage must not accept freeText.");
        Assert.False(props.TryGetProperty("query", out _), "plan_coverage must not accept a free-text query.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static async Task<JsonElement> GetInputSchemaAsync(McpClient client, string toolName, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var tool = Assert.Single(tools, t => t.Name == toolName);
        return tool.ProtocolTool.InputSchema;
    }

    private static JsonElement GetProperty(JsonElement schema, string propertyName) =>
        schema.GetProperty("properties").GetProperty(propertyName);

    private static string[] GetRequired(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];

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
