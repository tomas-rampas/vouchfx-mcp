using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers the todo-2 / REQ-002 server skeleton: the MCP initialize handshake, the tool registry
/// (all six tools advertised with the right names, descriptions, and input schemas), and that
/// every stub handler returns a tool-level error rather than crashing the server or writing
/// anything to stdout.
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
        "explain_run",
        "list_step_types",
        "run_suite",
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
    public async Task ListTools_ReturnsExactlyTheSixAdvertisedTools()
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
    [InlineData("run_suite")]
    [InlineData("explain_run")]
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
    public async Task ValidateSuite_Schema_HasRequiredStringPath()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "validate_suite", cts.Token);

        Assert.Equal(["path"], GetRequired(schema));
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "path"), "string"));
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

    [Fact]
    public async Task RunSuite_Schema_HasRequiredPathAndOptionalTagsAndTimeoutSeconds()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schema = await GetInputSchemaAsync(harness.Client, "run_suite", cts.Token);
        var required = GetRequired(schema);

        Assert.Equal(["path"], required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "path"), "string"));

        var tagsSchema = GetProperty(schema, "tags");
        Assert.DoesNotContain("tags", required);
        Assert.True(SchemaTypeIncludes(tagsSchema, "array"));
        Assert.True(SchemaTypeIncludes(tagsSchema.GetProperty("items"), "string"));

        Assert.DoesNotContain("timeoutSeconds", required);
        Assert.True(SchemaTypeIncludes(GetProperty(schema, "timeoutSeconds"), "integer"));

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

    [Theory]
    // Only the two tools neither todo 4 (REQ-003/REQ-004) nor todo 5 (REQ-005) implemented remain
    // stubs. validate_suite, list_step_types, and describe_step_type are real (see
    // RealToolsMcpTests), and search_docs is real too now (see RealDocsMcpTests).
    [InlineData("run_suite", "path")]
    [InlineData("explain_run", null)]
    public async Task EveryRemainingStub_ReturnsToolLevelErrorWithoutCrashingServer(string toolName, string? requiredArgumentName)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var arguments = requiredArgumentName is null
            ? null
            : new Dictionary<string, object?> { [requiredArgumentName] = "does-not-matter-yet" };

        var result = await harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("not implemented", content.Text, StringComparison.OrdinalIgnoreCase);

        // The stub error must be a normal tool result, not a crash: the server has to still be
        // responding to further requests afterwards.
        var toolsAfterError = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(ExpectedToolNames.Length, toolsAfterError.Count);

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
