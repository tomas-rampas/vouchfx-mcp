using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests;

/// <summary>Issue #126: the pinned engine's own storage scaffold must pass full MCP validation.</summary>
public class RealScaffoldSuiteAgainstPinnedCliTests
{
    private readonly ITestOutputHelper _testOutput;

    public RealScaffoldSuiteAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    [RequiredRealProbeFact]
    public async Task StorageScaffold_AgainstPinnedInstalledCli_EmitsS3AndPassesFullValidation()
    {
        RequiredStepAttemptEvidence.RequireMode(
            Environment.GetEnvironmentVariable(RequiredRealProbeFactAttribute.EnvironmentVariable));
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var realCli = new VouchfxCliProcessRunner();
        var pinCheck = await new CliPinVerifier(realCli, pin).VerifyAsync(cts.Token);
        RequiredStepAttemptEvidence.RequirePin(pinCheck, pin.Version);

        // The official catalogue example is emitted by the engine scaffolder, including its
        // managed dependency outline. A steps-only MCP intent does not infer that outline.
        var invocation = await realCli.RunAsync(
            ["list", "--json"],
            VouchfxCliProcessRunner.MaxListJsonOutputBytes,
            VouchfxCliProcessRunner.DefaultTimeout,
            cts.Token);
        Assert.True(invocation is { Launched: true, ExitCode: 0 },
            $"Pinned CLI list failed: {invocation.ExitCode}; {invocation.Stderr}");
        using var catalogue = JsonDocument.Parse(invocation.Stdout!);
        var entry = Assert.Single(catalogue.RootElement.GetProperty("stepTypes").EnumerateArray(),
            candidate => candidate.GetProperty("type").GetString() == "storage-assert.s3");
        var yaml = entry.GetProperty("example").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(yaml));
        _testOutput.WriteLine(yaml);

        // Do not supply the dependency kind: this must exercise the engine's generated default.
        var document = new YamlStream();
        document.Load(new StringReader(yaml));
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(document.Documents).RootNode);
        var environment = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("environment")]);
        var dependencies = Assert.IsType<YamlMappingNode>(environment.Children[new YamlScalarNode("dependencies")]);
        var dependency = Assert.Single(dependencies.Children);
        var declaration = Assert.IsType<YamlMappingNode>(dependency.Value);
        Assert.Equal("s3", Assert.IsType<YamlScalarNode>(declaration.Children[new YamlScalarNode("type")]).Value);
        var steps = Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("steps")]);
        var step = Assert.IsType<YamlMappingNode>(Assert.Single(steps.Children));
        Assert.Equal("storage-assert.s3", Assert.IsType<YamlScalarNode>(step.Children[new YamlScalarNode("type")]).Value);
        Assert.Equal(
            Assert.IsType<YamlScalarNode>(dependency.Key).Value,
            Assert.IsType<YamlScalarNode>(step.Children[new YamlScalarNode("target")]).Value);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: realCli, enginePin: pin);
        var validation = await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = yaml, ["level"] = "full" },
            cancellationToken: cts.Token);
        Assert.False(validation.IsError ?? false);
        var payload = validation.StructuredContent!.Value;
        Assert.True(payload.GetProperty("valid").GetBoolean(), payload.GetRawText());
        Assert.Empty(payload.GetProperty("errors").EnumerateArray());
        var diagnostics = payload.GetProperty("semanticDiagnostics").EnumerateArray().ToArray();
        Assert.DoesNotContain(diagnostics, finding => finding.GetProperty("code").GetString() == "VFX-D-1205");
        // The generated skeleton has no owner; informational authoring advice is not a validation error.
        Assert.All(diagnostics, finding =>
        {
            Assert.Equal("VFX-D-1211", finding.GetProperty("code").GetString());
            Assert.Equal("info", finding.GetProperty("severity").GetString());
        });
        Assert.Empty(consoleOut.Writer.ToString());

        _testOutput.WriteLine($"MEASURED pinned CLI {pin.Version}+{pin.CommitSha}: generated s3; full validation valid; no errors or D-1205.");
        _testOutput.WriteLine("MEASURED storage catalogue example: emitted s3; full validation valid; no errors or D-1205.");
        _testOutput.WriteLine(yaml);
    }

    [RequiredRealProbeFact]
    public async Task StorageScaffold_WithExplicitS3Outline_AgainstPinnedInstalledCli_PassesFullValidation()
    {
        RequiredStepAttemptEvidence.RequireMode(
            Environment.GetEnvironmentVariable(RequiredRealProbeFactAttribute.EnvironmentVariable));
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var realCli = new VouchfxCliProcessRunner();
        var pinCheck = await new CliPinVerifier(realCli, pin).VerifyAsync(cts.Token);
        RequiredStepAttemptEvidence.RequirePin(pinCheck, pin.Version);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: realCli, enginePin: pin);
        // Explicit supported outline: generation is required, dependency inference is not.
        // The real orchestrator invokes `vouchfx scaffold --intent <temporary-json>`.
        var scaffold = await harness.Client.CallToolAsync(
            "scaffold_suite",
            new Dictionary<string, object?>
            {
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "check-storage", ["type"] = "storage-assert.s3" },
                },
                ["dependencies"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "s3", ["type"] = "s3" },
                },
            },
            cancellationToken: cts.Token);
        Assert.False(scaffold.IsError ?? false);
        var yaml = scaffold.StructuredContent!.Value.GetProperty("yaml").GetString()!;
        Assert.Contains($"{CliVersionNormaliser.Normalise(pin.Version)}+{pin.CommitSha}", yaml, StringComparison.Ordinal);
        _testOutput.WriteLine(yaml);

        var document = new YamlStream();
        document.Load(new StringReader(yaml));
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(document.Documents).RootNode);
        var environment = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("environment")]);
        var dependencies = Assert.IsType<YamlMappingNode>(environment.Children[new YamlScalarNode("dependencies")]);
        var dependency = Assert.Single(dependencies.Children);
        var declaration = Assert.IsType<YamlMappingNode>(dependency.Value);
        Assert.Equal("s3", Assert.IsType<YamlScalarNode>(declaration.Children[new YamlScalarNode("type")]).Value);
        var steps = Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("steps")]);
        var step = Assert.IsType<YamlMappingNode>(Assert.Single(steps.Children));
        Assert.Equal("storage-assert.s3", Assert.IsType<YamlScalarNode>(step.Children[new YamlScalarNode("type")]).Value);
        Assert.Equal(
            Assert.IsType<YamlScalarNode>(dependency.Key).Value,
            Assert.IsType<YamlScalarNode>(step.Children[new YamlScalarNode("target")]).Value);

        var validation = await harness.Client.CallToolAsync(
            "validate_suite",
            new Dictionary<string, object?> { ["yaml"] = yaml, ["level"] = "full" },
            cancellationToken: cts.Token);
        Assert.False(validation.IsError ?? false);
        var payload = validation.StructuredContent!.Value;
        Assert.True(payload.GetProperty("valid").GetBoolean(), payload.GetRawText());
        Assert.Empty(payload.GetProperty("errors").EnumerateArray());
        var diagnostics = payload.GetProperty("semanticDiagnostics").EnumerateArray().ToArray();
        Assert.DoesNotContain(diagnostics, finding => finding.GetProperty("code").GetString() == "VFX-D-1205");
        // The generated skeleton has no owner; informational authoring advice is not a validation error.
        Assert.All(diagnostics, finding =>
        {
            Assert.Equal("VFX-D-1211", finding.GetProperty("code").GetString());
            Assert.Equal("info", finding.GetProperty("severity").GetString());
        });
        Assert.Empty(consoleOut.Writer.ToString());

        _testOutput.WriteLine($"MEASURED pinned CLI {pin.Version}+{pin.CommitSha}: generated s3; full validation valid; no errors or D-1205.");
        _testOutput.WriteLine("MEASURED storage explicit scaffold: emitted s3; full validation valid; no errors or D-1205.");
        _testOutput.WriteLine(yaml);
    }
}
