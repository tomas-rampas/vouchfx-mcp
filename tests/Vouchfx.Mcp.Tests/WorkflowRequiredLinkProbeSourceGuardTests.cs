using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Both automated full-suite gates must execute native containment probes, including releases
/// triggered without a preceding pull-request run.
/// </summary>
public class WorkflowRequiredLinkProbeSourceGuardTests
{
    [Theory]
    [InlineData("build.yml", "build")]
    [InlineData("release.yml", "build-pack")]
    public void UnitTestsStep_RequiresNativeLinkProbes(string workflowFile, string jobName)
    {
        var workflowPath = Path.Combine(SourceGuardScan.RepoRoot.FullName, ".github", "workflows", workflowFile);
        using var reader = File.OpenText(workflowPath);
        var workflow = new YamlStream();
        workflow.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(workflow.Documents).RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        var job = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode(jobName)]);
        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        var unitTests = Assert.Single(steps.Children.OfType<YamlMappingNode>(), step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name)
            && name is YamlScalarNode { Value: "Unit tests" });

        var command = Assert.IsType<YamlScalarNode>(unitTests.Children[new YamlScalarNode("run")]);
        Assert.StartsWith("dotnet test ", command.Value);
        Assert.True(unitTests.Children.TryGetValue(new YamlScalarNode("env"), out var environment),
            $"{workflowFile}:{jobName} Unit tests must require native containment link probes.");
        var variables = Assert.IsType<YamlMappingNode>(environment);
        Assert.True(variables.Children.TryGetValue(
            new YamlScalarNode(ContainmentLinkFactAttribute.EnvironmentVariable), out var requiredMode),
            $"{workflowFile}:{jobName} Unit tests must set {ContainmentLinkFactAttribute.EnvironmentVariable}=1.");
        Assert.Equal("1", Assert.IsType<YamlScalarNode>(requiredMode).Value);
    }
}
