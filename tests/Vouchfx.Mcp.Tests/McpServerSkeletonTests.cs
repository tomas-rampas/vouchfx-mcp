using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// <see cref="StreamClientTransport"/> over a pair of <see cref="Pipe"/>s) instead of real
/// stdio, per the SDK's own documented testing pattern. Because the pipe carries nothing but
/// the JSON-RPC frames the transport writes to it, a stray write to stdout can only be caught by
/// separately capturing <see cref="Console.Out"/> — see <see cref="ConsoleOutCapture"/>.
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
    [InlineData("validate_suite", "path")]
    [InlineData("list_step_types", null)]
    [InlineData("describe_step_type", "type")]
    [InlineData("search_docs", "query")]
    [InlineData("run_suite", "path")]
    [InlineData("explain_run", null)]
    public async Task EveryStub_ReturnsToolLevelErrorWithoutCrashingServer(string toolName, string? requiredArgumentName)
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

    /// <summary>
    /// Redirects <see cref="Console.Out"/> to a buffer for the lifetime of the instance and
    /// restores the original writer on <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// This guards against a future tool handler body mistakenly calling
    /// <c>Console.WriteLine</c> (or similar): the in-memory paired-stream transport carries
    /// nothing but the JSON-RPC frames the SDK writes to its own pipe, so a stray write
    /// elsewhere would otherwise go unnoticed here — it lands on a separate channel from the
    /// pipe, not on it. Capturing <see cref="Console.Out"/> makes that failure mode observable
    /// instead.
    /// <para>
    /// It does <b>not</b> exercise the production logging configuration (<c>Program.cs</c>
    /// redirects the console logging provider to stderr): this harness clears all logging
    /// providers rather than replicating that setup, so it says nothing about whether that
    /// redirection is wired correctly. <c>RealServerProcessTests</c> covers that, against the
    /// real built process over real stdio.
    /// </para>
    /// </remarks>
    private sealed class ConsoleOutCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;

        public StringWriter Writer { get; } = new();

        public ConsoleOutCapture() => Console.SetOut(Writer);

        public void Dispose() => Console.SetOut(_original);
    }

    /// <summary>
    /// Hosts the vouchfx-mcp server over an in-memory paired-stream transport and connects an
    /// MCP client to it, using the exact same <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/>
    /// configuration production startup uses — only the transport (stream pipes instead of real
    /// stdio) and logging (cleared, since these tests assert stdout cleanliness separately via
    /// <see cref="ConsoleOutCapture"/>) differ from <c>Program.cs</c>.
    /// </summary>
    private sealed class McpTestHarness : IAsyncDisposable
    {
        private readonly Pipe _clientToServerPipe;
        private readonly Pipe _serverToClientPipe;

        private McpTestHarness(IHost host, McpClient client, Pipe clientToServerPipe, Pipe serverToClientPipe)
        {
            Host = host;
            Client = client;
            _clientToServerPipe = clientToServerPipe;
            _serverToClientPipe = serverToClientPipe;
        }

        public IHost Host { get; }

        public McpClient Client { get; }

        public static async Task<McpTestHarness> StartAsync(CancellationToken cancellationToken)
        {
            var clientToServerPipe = new Pipe();
            var serverToClientPipe = new Pipe();

            // Fully qualified: the instance property McpTestHarness.Host below would otherwise
            // shadow the Microsoft.Extensions.Hosting.Host static factory class within this
            // nested type's scope.
            var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            hostBuilder.Logging.ClearProviders();
            hostBuilder.Services
                .AddVouchfxMcpServer()
                .WithStreamServerTransport(
                    clientToServerPipe.Reader.AsStream(),
                    serverToClientPipe.Writer.AsStream());

            var host = hostBuilder.Build();
            await host.StartAsync(cancellationToken);

            try
            {
                var clientTransport = new StreamClientTransport(
                    serverInput: clientToServerPipe.Writer.AsStream(),
                    serverOutput: serverToClientPipe.Reader.AsStream());

                var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

                return new McpTestHarness(host, client, clientToServerPipe, serverToClientPipe);
            }
            catch
            {
                // The host already started its background MCP session
                // (SingleSessionMcpServerHostedService) above: if the client-side handshake
                // throws or is cancelled, that host must not leak.
                await host.StopAsync(CancellationToken.None);
                host.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Host.StopAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    Host.Dispose();
                    CompletePipesQuietly();
                }
            }
        }

        private void CompletePipesQuietly()
        {
            // Client/Host teardown above already completes these via the Stream wrappers'
            // own Dispose in the ordinary case; this is a defensive backstop so the pipes
            // never outlive the harness even if that teardown path was skipped (e.g. StartAsync
            // failed before a client existed). PipeReader/PipeWriter.Complete() is idempotent,
            // but guard anyway rather than let one failure skip the rest.
            TryComplete(() => _clientToServerPipe.Reader.Complete());
            TryComplete(() => _clientToServerPipe.Writer.Complete());
            TryComplete(() => _serverToClientPipe.Reader.Complete());
            TryComplete(() => _serverToClientPipe.Writer.Complete());

            static void TryComplete(Action complete)
            {
                try
                {
                    complete();
                }
                catch (InvalidOperationException)
                {
                    // Already completed by the stream wrapper's own Dispose — fine.
                }
            }
        }
    }
}
