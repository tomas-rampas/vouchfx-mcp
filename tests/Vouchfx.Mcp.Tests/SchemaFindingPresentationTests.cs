namespace Vouchfx.Mcp.Tests;

public class SchemaFindingPresentationTests
{
    [Fact]
    public void ExactComparison_AdaptsOnlyRecognisedPrefixes_AndKeepsWordingAndLocation()
    {
        var original = SchemaFindingPresentation.ExactPairs([("[required] Required properties [\"path\"] are not present", 7)]);

        Assert.Equal(original, SchemaFindingPresentation.ExactPairs([("Required properties [\"path\"] are not present", 7)]));
        Assert.NotEqual(original, SchemaFindingPresentation.ExactPairs([("Required properties [\"target\"] are not present", 7)]));
        Assert.NotEqual(original, SchemaFindingPresentation.ExactPairs([("Required properties [\"path\"] are not present", 8)]));
        Assert.NotEqual(original, SchemaFindingPresentation.ExactPairs([("[unrecognised] Required properties [\"path\"] are not present", 7)]));
    }

    [Fact]
    public void LicensedWordingGap_StillRejectsChangedPropertyRequiredSetAndMultiplicity()
    {
        var original = Semantic(("[unevaluatedProperties] Unknown property 'bogus' on step type 'http.rest'", 7));
        Assert.Equal(original, Semantic(("Unknown property 'bogus' on step type 'http.rest'", 7)));
        Assert.NotEqual(original, Semantic(("Unknown property 'other' on step type 'http.rest'", 7)));
        Assert.NotEqual(original, Semantic(("Unknown property 'bogus' on step type 'http.rest'", 8)));
        Assert.NotEqual(original, Semantic(
            ("Unknown property 'bogus' on step type 'http.rest'", 7),
            ("Unknown property 'bogus' on step type 'http.rest'", 7)));

        Assert.NotEqual(
            Semantic(("[required] Required properties [\"target\"] are not present", 7)),
            Semantic(("Required properties [\"path\"] are not present", 7)));
        Assert.NotEqual(
            Semantic(("[properties] Property 'project' is not valid on service 'app'", 7)),
            Semantic(("Property 'image' cannot be combined with 'project' on service 'app'", 7)));
    }

    [Fact]
    public void EnumGap_RequiresTheSamePropertyAsTheMcpSourceLine()
    {
        const string yaml = "environment:\n  dependencies:\n    db:\n      type: cassandra\n";
        var mcp = SchemaFindingPresentation.SemanticPairs(
            [("[enum] Value should match one of the values specified by the enum", 4)], yaml);
        var cli = SchemaFindingPresentation.SemanticPairs(
            [("Value 'cassandra' is not one of the accepted values for 'type': postgres, redis", 4)], yaml);

        Assert.Equal(mcp, cli);
        Assert.NotEqual(mcp, SchemaFindingPresentation.SemanticPairs(
            [("Value 'cassandra' is not one of the accepted values for 'profile': tls, mtls", 4)], yaml));
        Assert.NotEqual(mcp, SchemaFindingPresentation.SemanticPairs(
            [("Value 'cassandra' is not one of the accepted values for 'type': postgres, redis", 3)], yaml));
    }

    [Fact]
    public void UnrecognisedWording_DoesNotCollapseToAnErrorKindOrLine()
    {
        Assert.NotEqual(Semantic(("Unrecognised error alpha", 7)), Semantic(("Unrecognised error beta", 7)));
        Assert.NotEqual(
            Semantic(("[enum] Unknown property 'bogus' on step type 'http.rest'", 7)),
            Semantic(("Unknown property 'bogus' on step type 'http.rest'", 7)));
    }

    [Fact]
    public void CorpusEnrichments_KeepPatternPropertyAndConstExpectedValue()
    {
        const string yaml = "schemaVersion: v2\nid: { exists: true }\n";
        var constant = SchemaFindingPresentation.SemanticPairs(
            [("[const] Expected \"\\u0022v1\\u0022\"", 1)], yaml);
        Assert.Equal(constant, SchemaFindingPresentation.SemanticPairs(
            [("'v2' is not a language schema version this engine recognises - write 'v1', or omit the field.", 1)], yaml));
        Assert.NotEqual(constant, SchemaFindingPresentation.SemanticPairs(
            [("'v2' is not a language schema version this engine recognises - write 'v3', or omit the field.", 1)], yaml));

        var pattern = SchemaFindingPresentation.SemanticPairs(
            [("[pattern] The string value is not a match for the indicated regular expression", 2)], yaml);
        Assert.Equal(pattern, SchemaFindingPresentation.SemanticPairs(
            [("Property name 'id' begins with an engine-reserved bookkeeping prefix - see this field's own description for the reserved prefix list.", 2)], yaml));
        Assert.NotEqual(pattern, SchemaFindingPresentation.SemanticPairs(
            [("Property name 'other' begins with an engine-reserved bookkeeping prefix - see this field's own description for the reserved prefix list.", 2)], yaml));
    }

    private static List<SchemaFindingPresentation.Identity> Semantic(params (string Message, long? Line)[] findings) =>
        SchemaFindingPresentation.SemanticPairs(findings, "");
}
