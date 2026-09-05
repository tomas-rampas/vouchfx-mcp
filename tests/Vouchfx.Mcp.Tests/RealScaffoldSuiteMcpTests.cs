using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spec B / REQ-007–REQ-008: <c>scaffold_suite</c> end-to-end through the in-memory MCP harness,
/// using <see cref="FakeVouchfxCli"/> so CI does not require a published engine with
/// <c>vouchfx scaffold</c>.
/// </summary>
public class RealScaffoldSuiteMcpTests
{
    [Fact]
    public async Task ScaffoldSuite_MultiTypeIntent_ReturnsYamlWithTypesAndIds()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "scaffold_suite",
            new Dictionary<string, object?>
            {
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "get-api", ["type"] = "http.rest", ["label"] = "GET api" },
                    new Dictionary<string, object?> { ["id"] = "check-db", ["type"] = "db-assert.postgres" },
                },
                ["services"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "api", ["image"] = "traefik/whoami" },
                },
                ["dependencies"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "db", ["type"] = "postgres" },
                },
            },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent.");
        var yaml = payload.GetProperty("yaml").GetString()
            ?? throw new InvalidOperationException("Expected yaml property.");

        Assert.Contains("http.rest", yaml, StringComparison.Ordinal);
        Assert.Contains("db-assert.postgres", yaml, StringComparison.Ordinal);
        Assert.Contains("get-api", yaml, StringComparison.Ordinal);
        Assert.Contains("check-db", yaml, StringComparison.Ordinal);
        Assert.Contains("Machine-drafted", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-literal-token", yaml, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ScaffoldSuite_UnknownType_ReturnsToolErrorNamingType()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "scaffold_suite",
            new Dictionary<string, object?>
            {
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "x", ["type"] = "nope.fake" },
                },
            },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("nope.fake", content.Text, StringComparison.Ordinal);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(16, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ScaffoldSuite_EmptySteps_ReturnsToolError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "scaffold_suite",
            new Dictionary<string, object?>
            {
                ["steps"] = Array.Empty<object>(),
            },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("at least one step", content.Text, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ScaffoldSuite_ThenValidateSuite_ReturnsValidTrue()
    {
        // REQ-008: scaffold output can be validated via validate_suite (temp file content path).
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var scaffoldResult = await harness.Client.CallToolAsync(
            "scaffold_suite",
            new Dictionary<string, object?>
            {
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "get-api", ["type"] = "http.rest" },
                    new Dictionary<string, object?> { ["id"] = "check-db", ["type"] = "db-assert.postgres" },
                },
                ["services"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "api", ["image"] = "traefik/whoami" },
                },
                ["dependencies"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "db", ["type"] = "postgres" },
                },
            },
            cancellationToken: cts.Token);

        Assert.False(scaffoldResult.IsError ?? false);
        var yaml = scaffoldResult.StructuredContent!.Value.GetProperty("yaml").GetString()!;

        var suitePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        try
        {
            await File.WriteAllTextAsync(suitePath, yaml, cts.Token);

            var validateResult = await harness.Client.CallToolAsync(
                "validate_suite",
                new Dictionary<string, object?> { ["path"] = suitePath },
                cancellationToken: cts.Token);

            Assert.False(validateResult.IsError ?? false);
            var payload = validateResult.StructuredContent
                ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.True(
                payload.GetProperty("valid").GetBoolean(),
                "Expected scaffolded YAML to pass validate_suite. Errors: "
                + payload.GetProperty("errors").GetRawText());
        }
        finally
        {
            try { File.Delete(suitePath); } catch { /* best-effort */ }
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
