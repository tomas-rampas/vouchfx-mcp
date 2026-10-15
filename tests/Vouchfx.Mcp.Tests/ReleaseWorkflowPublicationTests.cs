using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests;

public class ReleaseWorkflowPublicationTests
{
    [Fact]
    public void Workflow_OnlyPublishedReleaseAndNonpublishingDispatchCanTrigger()
    {
        var triggers = Mapping(LoadWorkflow("release.yml"), "on");
        Assert.Equal(["release", "workflow_dispatch"], triggers.Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value)
            .Order(StringComparer.Ordinal));
        var release = Mapping(triggers, "release");
        Assert.Equal("published", Scalar(Assert.Single(
            Assert.IsType<YamlSequenceNode>(release.Children[new YamlScalarNode("types")]).Children)));
    }

    [Fact]
    public void PublishingJob_RequiresPublishedReleaseAndBothValidationJobs()
    {
        var publish = Mapping(Mapping(LoadWorkflow("release.yml"), "jobs"), "publish-release");
        Assert.Equal("github.event_name == 'release' && github.event.action == 'published'",
            Scalar(publish.Children[new YamlScalarNode("if")]));
        Assert.Equal(["build-pack", "required-real-probes"],
            Assert.IsType<YamlSequenceNode>(publish.Children[new YamlScalarNode("needs")])
                .Children.Select(Scalar).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PublishingScripts_UseTheExactReleaseAssetGateWithoutDirectOverwrite()
    {
        var steps = JobSteps("release.yml", "publish-release");
        var gate = Assert.Single(steps, step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name)
            && Scalar(name) == "Ensure immutable nupkg release asset");
        var environment = Mapping(gate, "env");
        Assert.Equal("${{ github.repository }}", Scalar(environment.Children[new YamlScalarNode("GH_REPO")]));
        Assert.Equal("${{ github.event.release.id }}", Scalar(environment.Children[new YamlScalarNode("RELEASE_ID")]));
        var command = Scalar(gate.Children[new YamlScalarNode("run")]);
        Assert.Contains("python3 scripts/publish-release-asset.py", command, StringComparison.Ordinal);
        Assert.Contains("--package \"release-upload/Vouchfx.Mcp.${VERSION}.nupkg\"", command, StringComparison.Ordinal);
        Assert.Contains("--repository \"$GH_REPO\" --release-id \"$RELEASE_ID\" --tag \"$TAG\"", command, StringComparison.Ordinal);
        var checkout = Assert.Single(steps, step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name)
            && Scalar(name) == "Checkout release asset verifier");
        Assert.Equal("${{ github.sha }}", Scalar(Mapping(checkout, "with").Children[new YamlScalarNode("ref")]));
        foreach (var step in steps.Where(step => step.Children.ContainsKey(new YamlScalarNode("run"))))
        {
            var script = Scalar(step.Children[new YamlScalarNode("run")]);
            Assert.DoesNotContain("--clobber", script, StringComparison.Ordinal);
            Assert.DoesNotContain("gh release upload", script, StringComparison.Ordinal);
            Assert.DoesNotContain("gh release create", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("build.yml", "build")]
    [InlineData("release.yml", "build-pack")]
    public void ValidationJobs_RunInertReleaseAssetRegressions(string workflowFile, string jobName)
    {
        var tests = Assert.Single(JobSteps(workflowFile, jobName), step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name)
            && Scalar(name) == "Release asset gate tests");
        Assert.Equal("python3 -B -m unittest discover -s scripts/tests -p test_publish_release_asset.py -v",
            Scalar(tests.Children[new YamlScalarNode("run")]));
    }

    private static IEnumerable<YamlMappingNode> JobSteps(string workflowFile, string jobName) =>
        Assert.IsType<YamlSequenceNode>(Mapping(Mapping(LoadWorkflow(workflowFile), "jobs"), jobName)
            .Children[new YamlScalarNode("steps")]).Children.OfType<YamlMappingNode>();

    private static YamlMappingNode LoadWorkflow(string workflowFile)
    {
        using var reader = File.OpenText(Path.Combine(SourceGuardScan.RepoRoot.FullName,
            ".github", "workflows", workflowFile));
        var workflow = new YamlStream();
        workflow.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(workflow.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key) =>
        Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value!;
}
