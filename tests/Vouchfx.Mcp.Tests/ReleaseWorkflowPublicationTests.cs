using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests;

public class ReleaseWorkflowPublicationTests
{
    [Fact]
    public void Workflow_OnlyPublishedReleaseAndNonpublishingDispatchCanTrigger()
    {
        var triggers = Mapping(LoadWorkflow(), "on");
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
        var publish = Mapping(Mapping(LoadWorkflow(), "jobs"), "publish-release");
        Assert.Equal("github.event_name == 'release' && github.event.action == 'published'",
            Scalar(publish.Children[new YamlScalarNode("if")]));
        Assert.Equal(["build-pack", "required-real-probes"],
            Assert.IsType<YamlSequenceNode>(publish.Children[new YamlScalarNode("needs")])
                .Children.Select(Scalar).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PublishingScripts_UploadOnceWithoutOverwritingOrCreatingARelease()
    {
        var publish = Mapping(Mapping(LoadWorkflow(), "jobs"), "publish-release");
        var scripts = Assert.IsType<YamlSequenceNode>(publish.Children[new YamlScalarNode("steps")])
            .Children.OfType<YamlMappingNode>()
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("run")))
            .Select(step => Scalar(step.Children[new YamlScalarNode("run")]))
            .ToArray();
        var upload = Assert.Single(scripts, script => script.Contains("gh release upload", StringComparison.Ordinal));
        Assert.DoesNotContain("--clobber", upload, StringComparison.Ordinal);
        Assert.All(scripts, script => Assert.DoesNotContain("gh release create", script, StringComparison.Ordinal));
    }

    private static YamlMappingNode LoadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(SourceGuardScan.RepoRoot.FullName,
            ".github", "workflows", "release.yml"));
        var workflow = new YamlStream();
        workflow.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(workflow.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key) =>
        Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value!;
}
