using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

public class ExternalEnvironmentSourceTests
{
    internal const string SourceYaml = """
        metadata:
          name: source-suite
          owner: tests
          tags: [source]
        environment:
          source:
            driver: another-driver
            config: nested/driver.yaml
        steps:
          - id: call
            type: http.rest
            target: api
            method: GET
            path: /health
          - id: query
            type: db-assert.postgres
            target: db
            query: SELECT 1
            expect:
              rowCount: 1
        """;

    [Fact]
    public void SourceBindingsRemainUnknown_WithoutInventingAbsentInfrastructure()
    {
        var analysis = SuiteValidator.AnalyseYaml(SourceYaml, ValidationLevel.Semantic);

        Assert.True(analysis.Valid); // This level deliberately does not evaluate the root schema.
        Assert.Empty(analysis.Errors);
        Assert.Empty(analysis.Summary!.Services);
        Assert.Empty(analysis.Summary.Dependencies);
        var finding = Assert.Single(analysis.SemanticDiagnostics);
        Assert.Equal(VfxCodeCatalogue.ExternalEnvironmentSourceUnresolved, finding.Code);
        Assert.Equal("info", finding.Severity);
        Assert.Equal("$.environment.source", finding.Path);
        Assert.Contains("normal engine validation", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Inline YAML has no filesystem base", finding.Message, StringComparison.Ordinal);
        Assert.True(finding.Message.Length < 500);
    }

    [Fact]
    public void InlineMissingInfrastructure_StillWarnsWithoutAnExternalAdvisory()
    {
        var yaml = SourceYaml.Replace("  source:\n    driver: another-driver\n    config: nested/driver.yaml", "  services: {}", StringComparison.Ordinal);
        var analysis = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Semantic);

        Assert.Contains(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.DanglingTargetReference);
        Assert.Contains(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.UndeclaredDependencyType);
        Assert.DoesNotContain(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.ExternalEnvironmentSourceUnresolved);
    }

    [Fact]
    public void TheRc6SchemaStillRejectsSource_WhileSemanticAdviceStaysSeparate()
    {
        var analysis = SuiteValidator.AnalyseYaml(SourceYaml, ValidationLevel.Full);

        Assert.False(analysis.Valid);
        Assert.Contains(analysis.Errors, e => e.Code == VfxCodeCatalogue.SchemaViolation && e.InstancePath == "/environment/source");
        Assert.Single(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.ExternalEnvironmentSourceUnresolved);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{driver: compose}")]
    [InlineData("{driver: compose, config: ''}")]
    [InlineData("{driver: [], config: driver.yaml}")]
    public void MalformedReferencesRemainSchemaFindings_WithoutClaimingAValidSource(string descriptor)
    {
        var yaml = "environment:\n  source: " + descriptor + "\nsteps: []\n";
        var analysis = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full);

        Assert.False(analysis.Valid);
        Assert.NotEmpty(analysis.Errors);
        Assert.DoesNotContain(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.ExternalEnvironmentSourceUnresolved);
    }

    [Fact]
    public void SourceValuesAndSecretReferences_NeverEnterTheAdvisory()
    {
        var yaml = SourceYaml.Replace("another-driver", "private-driver-marker", StringComparison.Ordinal)
            .Replace("nested/driver.yaml", "${secret:vault/private-config-marker}", StringComparison.Ordinal);
        var analysis = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Semantic);
        var finding = Assert.Single(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.ExternalEnvironmentSourceUnresolved);

        Assert.DoesNotContain("private", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("${", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisationPreservesRelativeSourceReferences_AndIsIdempotent()
    {
        var first = SuiteValidator.NormaliseYaml(SourceYaml, ValidationLevel.Full);
        Assert.NotNull(first.NormalizedYaml);
        using var document = YamlToJsonConverter.Convert(first.NormalizedYaml!);
        var source = document.RootElement.GetProperty("environment").GetProperty("source");
        Assert.Equal("another-driver", source.GetProperty("driver").GetString());
        Assert.Equal("nested/driver.yaml", source.GetProperty("config").GetString());
        Assert.False(first.Validation.Valid); // Formatting does not add support to the rc.6 schema.
        Assert.Equal(first.NormalizedYaml, SuiteValidator.NormaliseYaml(first.NormalizedYaml!, ValidationLevel.Full).NormalizedYaml);
    }
}
