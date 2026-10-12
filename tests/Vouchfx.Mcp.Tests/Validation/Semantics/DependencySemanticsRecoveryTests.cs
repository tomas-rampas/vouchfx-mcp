using System.Text.Json;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

public class DependencySemanticsRecoveryTests
{
    [Theory]
    [InlineData("s3")]
    [InlineData("minio")]
    public void StorageAssertion_AcceptsEitherManagedObjectStorageKind(string kind)
    {
        using var fixture = SemanticRuleFixture.For(StorageSuite(kind));
        Assert.Empty(fixture.Run(new UndeclaredDependencyRule()));
    }

    [Fact]
    public void Catalogue_DescribesStorageKindsAsAlternatives()
    {
        Assert.Equal(["s3", "minio"], RequiredResourceCatalogue.For("storage-assert.s3"));
    }

    [Theory]
    [InlineData("elasticsearch", "discovery.type")]
    [InlineData("elasticsearch", "xpack.security.enabled")]
    [InlineData("elasticsearch", "ES_JAVA_OPTS")]
    [InlineData("elasticsearch", "cluster.routing.allocation.disk.threshold_enabled")]
    [InlineData("minio", "MINIO_ROOT_USER")]
    [InlineData("minio", "MINIO_ROOT_PASSWORD")]
    [InlineData("s3", "RUSTFS_ACCESS_KEY")]
    [InlineData("s3", "RUSTFS_SECRET_KEY")]
    [InlineData("azureservicebus", "ACCEPT_EULA")]
    [InlineData("azureservicebus", "MSSQL_SA_PASSWORD")]
    [InlineData("azureservicebus", "SQL_SERVER")]
    public void ReservedDependencyName_IsAnAuthoringErrorWithoutEchoingItsValue(string kind, string key)
    {
        using var fixture = SemanticRuleFixture.For(EnvSuite(kind, key, "distinctive-private-value-37", false));
        var finding = Assert.Single(SemanticAnalyser.Analyse(fixture.Context).Findings, x => x.Code == "VFX-D-1213");
        Assert.Equal("error", finding.Severity);
        Assert.Contains(kind, finding.Message, StringComparison.Ordinal);
        Assert.Contains(key, finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("distinctive-private-value-37", JsonSerializer.Serialize(finding), StringComparison.Ordinal);
        Assert.NotNull(finding.Location);
        Assert.Equal(SemanticRuleFixture.SourceName, finding.Location.File);
        Assert.Contains(".env", finding.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("elasticsearch", "es_java_opts")]
    [InlineData("Elasticsearch", "ES_JAVA_OPTS")]
    [InlineData("postgres", "ES_JAVA_OPTS")]
    [InlineData("minio", "RUSTFS_ACCESS_KEY")]
    [InlineData("s3", "MINIO_ROOT_PASSWORD")]
    [InlineData("custom", "SQL_SERVER")]
    [InlineData("unknown", "MINIO_ROOT_USER")]
    public void UnreservedPairs_ProduceNoFinding(string kind, string key)
    {
        using var fixture = SemanticRuleFixture.For(EnvSuite(kind, key, "allowed", false));
        Assert.DoesNotContain(SemanticAnalyser.Analyse(fixture.Context).Findings, x => x.Code == "VFX-D-1213");
    }

    [Theory]
    [InlineData("ES_JAVA_OPTS")]
    [InlineData("MINIO_ROOT_PASSWORD")]
    [InlineData("RUSTFS_SECRET_KEY")]
    [InlineData("SQL_SERVER")]
    public void CustomerServiceEnvironment_IsNotManagedDependencyEnvironment(string key)
    {
        using var fixture = SemanticRuleFixture.For(EnvSuite("elasticsearch", key, "allowed", true));
        Assert.DoesNotContain(SemanticAnalyser.Analyse(fixture.Context).Findings, x => x.Code == "VFX-D-1213");
    }

    [Fact]
    public void ARegisteredExternalDriverReference_DoesNotInventManagedDependencyFacts()
    {
        using var fixture = SemanticRuleFixture.For("""
            environment:
              source:
                driver: docker-compose
                config: compose.vouchfx.yaml
            steps:
              - id: storage
                type: storage-assert.s3
                target: objects
                bucket: invoices
                key: current.json
                expect:
                  exists: true
            """);
        var findings = SemanticAnalyser.Analyse(fixture.Context).Findings;
        Assert.DoesNotContain(findings, x => x.Code is "VFX-D-1202" or "VFX-D-1205" or "VFX-D-1213");
        Assert.Contains(findings, x => x.Code == "VFX-D-1212");
    }

    [Fact]
    public void FullValidation_RefusesReservedNames_ButSchemaOnlyKeepsItsOwnChannel()
    {
        var yaml = EnvSuite("elasticsearch", "ES_JAVA_OPTS", "private", false);
        var full = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full);
        Assert.Empty(full.Errors);
        Assert.False(full.Valid);
        Assert.Contains(full.SemanticDiagnostics, x => x.Code == "VFX-D-1213" && x.Severity == "error");
        var schema = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Schema);
        Assert.True(schema.Valid);
        Assert.Empty(schema.SemanticDiagnostics);
    }

    [Theory]
    [InlineData("environment: null")]
    [InlineData("environment: { dependencies: null }")]
    [InlineData("environment: { dependencies: [] }")]
    [InlineData("environment: { dependencies: { backend: null } }")]
    [InlineData("environment: { dependencies: { backend: { type: 42, env: { ES_JAVA_OPTS: value } } } }")]
    [InlineData("environment: { dependencies: { backend: { type: elasticsearch, env: [] } } }")]
    public void MalformedShapes_RemainSchemaFindings_WithoutCrashingTheRule(string environment)
    {
        using var fixture = SemanticRuleFixture.For(environment + "\nsteps: []\n");
        Assert.Empty(fixture.Run(new ReservedDependencyEnvironmentRule()));
    }

    [Fact]
    public void ASecretNamedDependencyAndSecretReferenceValue_AreNeverEchoed()
    {
        var yaml = EnvSuite("minio", "MINIO_ROOT_PASSWORD", "${secret:env/PRIVATE_NAME}", false)
            .Replace("backend:", "\"${secret:vault/private-layout}\":", StringComparison.Ordinal);
        using var fixture = SemanticRuleFixture.For(yaml);
        var finding = Assert.Single(fixture.Run(new ReservedDependencyEnvironmentRule()));
        var serialized = JsonSerializer.Serialize(finding);
        Assert.DoesNotContain("private-layout", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_NAME", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("${", serialized, StringComparison.Ordinal);
        Assert.Equal("$.environment.dependencies", finding.Path);
    }

    [Theory]
    [InlineData("ES_JAVA_OPTS ")]
    [InlineData(" ES_JAVA_OPTS")]
    [InlineData("ES_JAVA_OPTS=bad")]
    public void InvalidEnvNames_UseTheSchemaChannel_WithoutTrimmingToAReservedName(string key)
    {
        var analysis = SuiteValidator.AnalyseYaml(EnvSuite("elasticsearch", key, "private", false), ValidationLevel.Full);
        Assert.NotEmpty(analysis.Errors);
        Assert.DoesNotContain(analysis.SemanticDiagnostics, x => x.Code == "VFX-D-1213");
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("minio")]
    public void StorageSuite_FullCurrentSchemaAndSemanticAnalysis_Agree(string kind)
    {
        var analysis = SuiteValidator.AnalyseYaml(StorageSuite(kind), ValidationLevel.Full);
        Assert.True(analysis.Valid, string.Join("; ", analysis.Errors.Select(x => x.Message)));
        Assert.DoesNotContain(analysis.SemanticDiagnostics, x => x.Code is "VFX-D-1202" or "VFX-D-1205");
    }

    internal static string EnvSuite(string kind, string key, string value, bool service) => $$"""
        metadata:
          name: reserved-env-regression
          owner: tests
          tags: [regression]
        environment:
          {{(service ? "services" : "dependencies")}}:
            backend:
              {{(service ? "image: example/backend:1.0" : "type: " + kind)}}
              env:
                {{JsonSerializer.Serialize(key)}}: {{JsonSerializer.Serialize(value)}}
        steps:
          - id: check
            type: script.csharp
            code: "return;"
        """;

    internal static string StorageSuite(string kind) => $$"""
        metadata:
          name: storage-alias
          owner: tests
          tags: [regression]
        environment:
          dependencies:
            objects:
              type: {{kind}}
        steps:
          - id: storage
            type: storage-assert.s3
            target: objects
            bucket: invoices
            key: current.json
            expect:
              exists: true
        """;
}
