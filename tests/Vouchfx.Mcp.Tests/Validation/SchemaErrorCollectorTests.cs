using System.Text.Json;
using Json.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

public class SchemaErrorCollectorTests
{
    [Fact]
    public void Collect_ValidEvaluation_StillReportsUnknownStepType()
    {
        using var document = JsonDocument.Parse("""
            { "steps": [{ "type": "unregistered.example" }] }
            """);
        var evaluation = JsonSchema.FromText("{}").Evaluate(document.RootElement);
        Assert.True(evaluation.IsValid);

        var errors = SchemaErrorCollector.Collect(evaluation, document.RootElement, yamlRoot: null);

        var error = Assert.Single(errors);
        Assert.Equal("VFX-D-1201", error.Code);
        Assert.Equal("/steps/0/type", error.InstancePath);
        Assert.Contains("unregistered.example", error.Message, StringComparison.Ordinal);
        Assert.Null(error.Line);
        Assert.Null(error.Column);
    }

    [Fact]
    public void Collect_SchemaAndUnknownStepErrors_PreservesChannelOrderAndSourceLine()
    {
        const string yaml = """
            steps:
              - id: one
                type: unregistered.example
            """;
        using var document = YamlToJsonConverter.Convert(yaml);
        var evaluation = JsonSchema.FromText("""
            { "required": ["version"] }
            """).Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(evaluation.IsValid);

        var errors = SchemaErrorCollector.Collect(
            evaluation, document.RootElement, YamlLineResolver.TryParseYamlRoot(yaml));

        Assert.Collection(errors,
            schema =>
            {
                Assert.Equal("VFX-D-1101", schema.Code);
                Assert.Equal(string.Empty, schema.InstancePath);
                Assert.Contains("version", schema.Message, StringComparison.Ordinal);
                Assert.Null(schema.Line);
            },
            unknown =>
            {
                Assert.Equal("VFX-D-1201", unknown.Code);
                Assert.Equal("/steps/0/type", unknown.InstancePath);
                Assert.Equal(3, unknown.Line);
            });
    }
}
