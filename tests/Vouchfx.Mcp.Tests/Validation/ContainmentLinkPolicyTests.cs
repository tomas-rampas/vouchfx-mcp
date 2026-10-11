using System.Reflection;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests.Validation;

public class ContainmentLinkPolicyTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "UnauthorizedAccessException", true)]
    [InlineData("1", "UnauthorizedAccessException", false)]
    [InlineData("1", null, false)]
    public void Discovery_OnlySkipsUnavailableOptionalProbes(string? mode, string? failure, bool skip)
    {
        var attribute = new ContainmentLinkFactAttribute(mode, failure);

        Assert.Equal(skip, attribute.Skip is not null);
        if (skip)
        {
            Assert.Contains(ContainmentLinkFactAttribute.EnvironmentVariable, attribute.Skip!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RequiredCiUnitTestStep_RequiresNativeContainmentLinks()
    {
        var workflow = Path.Combine(SourceGuardScan.RepoRoot.FullName, ".github", "workflows", "build.yml");
        var yaml = new YamlStream();
        using var reader = File.OpenText(workflow);
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        var build = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("build")]);
        var steps = Assert.IsType<YamlSequenceNode>(build.Children[new YamlScalarNode("steps")]);
        var unitTests = Assert.Single(steps.Children.OfType<YamlMappingNode>(), step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name) && name.ToString() == "Unit tests");
        Assert.True(unitTests.Children.TryGetValue(new YamlScalarNode("env"), out var variables),
            "The CI Unit tests step must set required native containment probe mode.");
        var environment = Assert.IsType<YamlMappingNode>(variables);

        Assert.Equal("1", environment.Children[new YamlScalarNode(ContainmentLinkFactAttribute.EnvironmentVariable)].ToString());
        Assert.Contains("dotnet test Vouchfx.Mcp.sln", unitTests.Children[new YamlScalarNode("run")].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(PathSafetyGuardTests.CheckLocalPath_SymlinkInsideTheRootPointingOutside_IsRejected))]
    [InlineData(nameof(PathSafetyGuardTests.CheckLocalPath_SymlinkInsideTheRootPointingBackInside_ReturnsNull))]
    [InlineData(nameof(PathSafetyGuardTests.CheckLocalPath_TwoHopSymlinkChainLandingOutsideTheRoot_IsRejected))]
    [InlineData(nameof(PathSafetyGuardTests.CheckLocalPath_DanglingSymlinkPointingOutsideTheRoot_IsRejected))]
    public void OriginalLinkAssertions_CannotRevertToUnconditionalGreenFacts(string methodName)
    {
        var method = typeof(PathSafetyGuardTests).GetMethod(methodName)!;

        Assert.NotNull(method.GetCustomAttribute<ContainmentLinkFactAttribute>());
        var trait = Assert.Single(method.GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(TraitAttribute));
        Assert.Equal("containment", trait.ConstructorArguments[0].Value);
        Assert.Equal("link", trait.ConstructorArguments[1].Value);
    }
}
