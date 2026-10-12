using Vouchfx.Mcp.Tests.Validation.Semantics;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

public class ExternalEnvironmentSourceWorkerTests
{
    [Fact]
    public async Task NestedFileSource_PreservesIdentityAndReferences_WithoutReadingExternalConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-source-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        var configDirectory = Path.Combine(nested, "nested");
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(nested, "source.e2e.yaml");
        var config = Path.Combine(configDirectory, "driver.yaml");
        const string configContent = "unparseable external content: [private-config-marker";
        await File.WriteAllTextAsync(path, ExternalEnvironmentSourceTests.SourceYaml);
        await File.WriteAllTextAsync(config, configContent);
        try
        {
            var workspace = Workspace.Resolve(root);
            var source = SuiteSource.FromPath(Path.GetRelativePath(root, path));
            var analysis = await ValidationWorkerClient.AnalyseAsync(source, ValidationLevel.Semantic, workspace);
            var advisory = Assert.Single(analysis.SemanticDiagnostics);
            Assert.Equal("VFX-D-1212", advisory.Code);
            Assert.Equal(path, advisory.Location!.File);
            Assert.DoesNotContain("private-config-marker", advisory.Message, StringComparison.Ordinal);

            var normalised = await ValidationWorkerClient.NormaliseAsync(source, ValidationLevel.Full, true, workspace);
            Assert.True(normalised.Validation.Valid); // Shape acceptance does not read or resolve the driver configuration.
            Assert.Empty(normalised.Validation.Errors);
            Assert.Single(normalised.Validation.SemanticDiagnostics, d => d.Code == "VFX-D-1212");
            Assert.NotNull(normalised.NormalizedYaml);
            Assert.Contains("nested/driver.yaml", normalised.NormalizedYaml!, StringComparison.Ordinal);
            Assert.Equal(ExternalEnvironmentSourceTests.SourceYaml, await File.ReadAllTextAsync(path));
            Assert.Equal(configContent, await File.ReadAllTextAsync(config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InlineSource_ReportsNoFilesystemBase_WithoutInventingAPath()
    {
        var analysis = await ValidationWorkerClient.AnalyseAsync(
            SuiteSource.FromInlineYaml(ExternalEnvironmentSourceTests.SourceYaml), ValidationLevel.Semantic, workspace: null);

        var advisory = Assert.Single(analysis.SemanticDiagnostics);
        Assert.Equal("VFX-D-1212", advisory.Code);
        Assert.Equal(SuiteSource.InlineSourceName, advisory.Location!.File);
        Assert.Contains("Inline YAML has no filesystem base", advisory.Message, StringComparison.Ordinal);
    }
}
