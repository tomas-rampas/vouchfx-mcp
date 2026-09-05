using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03 review — the addressing helper every semantic finding is located by: its JSONPath
/// spelling, the sanitisation the wire half gets, and the pointer half that must NOT get it.
/// </summary>
public class SuitePathTests
{
    /// <summary>A right-to-left override, written as an escape rather than as the invisible glyph.</summary>
    /// <remarks>
    /// A source file carrying a literal bidi override is the same hazard these tests are about; an
    /// escape is reviewable.
    /// </remarks>
    private const string RightToLeftOverride = "\u202E";

    [Fact]
    public void AnOrdinaryNameTakesTheDottedForm_AndAHyphenatedOneTakesBrackets()
    {
        // RFC 9535's shorthand member name is [A-Za-z_][A-Za-z0-9_]* — no hyphen. `orders-db` is a
        // perfectly ordinary suite identifier, and a dotted `$.environment.dependencies.orders-db`
        // is what some JSONPath implementations read as a subtraction. Brackets are unambiguous
        // everywhere.
        Assert.Equal("$.steps", SuitePath.Root.Property("steps").JsonPath);
        Assert.Equal(
            "$.steps[0].capture.orderId",
            SuitePath.Step(0).Property("capture").Property("orderId").JsonPath);
        Assert.Equal("$['orders-db']", SuitePath.Root.Property("orders-db").JsonPath);
        Assert.Equal("$._private", SuitePath.Root.Property("_private").JsonPath);
    }

    [Theory]
    // The three characters no Path assertion had ever seen before this review: a right-to-left
    // override, a zero-width space, and a byte-order mark. All three are legal YAML mapping keys,
    // all three reach a host, and the first can reorder the visible text of everything after it.
    [InlineData("\u202e", "\\u202e")]
    [InlineData("\u200b", "\\u200b")]
    [InlineData("\ufeff", "\\ufeff")]
    public void ANonPrintableCharacterInAKeyIsEscapedInTheJsonPath(string raw, string escaped)
    {
        var path = SuitePath.Root.Property($"head{raw}er");

        Assert.Equal($"$['head{escaped}er']", path.JsonPath);
        Assert.DoesNotContain(raw, path.JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePointerHalfKeepsTheRawName_BecauseLineResolutionWalksIt()
    {
        // The deliberate asymmetry. YamlLineResolver matches this pointer's segments against the
        // representation model's real keys, so an escaped pointer would resolve nothing and every
        // finding on a non-ASCII key would silently lose its DiagnosticLocation — a wrong answer on
        // a valid suite. The pointer never leaves the worker process; the JSONPath is the half that
        // ships.
        var path = SuitePath.Root.Property($"head{RightToLeftOverride}er");

        Assert.Equal($"/head{RightToLeftOverride}er", path.Pointer);
        Assert.Contains(RightToLeftOverride, path.Pointer, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverLongKeyIsCapped()
    {
        var path = SuitePath.Root.Property(new string('k', 400));

        // Capped by VfxCode.SanitiseForEcho at 64 characters plus its ellipsis marker, so a 1 MB
        // mapping key cannot become a 1 MB Diagnostic.Path on the wire.
        Assert.True(path.JsonPath.Length < 100, $"JsonPath was {path.JsonPath.Length} characters long.");
        Assert.Contains("…", path.JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretReferenceInAKeySurvivesSanitisationSoTheChokePointCanStillSeeIt()
    {
        // THE ordering dependency, pinned rather than left as a comment. SemanticAnalyser's hygiene
        // guard fails the call when Diagnostic.Path contains the literal `${`. Both `$` (0x24) and
        // `{` (0x7B) are printable ASCII, so SanitiseForDisplay passes them through — which is what
        // keeps the guard armed AFTER this call. A sanitiser that escaped either character would
        // silently disarm it, and this assertion is what would notice.
        var path = SuitePath.Root.Property("${secret:vault/prod-db-password}");

        Assert.Contains("${", path.JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilderReconstructsExactlyWhatTheStepwiseFormWouldHaveBuilt()
    {
        // SuitePathBuilder exists purely to defer allocation; it must not change the ANSWER. Both
        // halves are compared, because the pointer is what resolves a line and the JSONPath is what
        // a host reads.
        var builder = new SuitePathBuilder();
        builder.PushIndex(3);
        Assert.True(builder.TryPushProperty("headers"));
        Assert.True(builder.TryPushProperty("orders-db"));

        var built = builder.Build();
        var stepwise = SuitePath.Root.Index(3).Property("headers").Property("orders-db");

        Assert.Equal(stepwise.JsonPath, built.JsonPath);
        Assert.Equal(stepwise.Pointer, built.Pointer);

        builder.Pop();
        Assert.Equal(SuitePath.Root.Index(3).Property("headers").JsonPath, builder.Build().JsonPath);
    }

    [Fact]
    public void TheBuilderDeclinesToPushANameCarryingASecretReference()
    {
        // The hygiene rule made structural: a name carrying a reference is not pushed, so the
        // subtree under it is addressed at the PARENT rather than smuggling the reference out
        // through Diagnostic.Path. The caller pops only what it pushed, which is what the boolean
        // return is for.
        var builder = new SuitePathBuilder();
        Assert.True(builder.TryPushProperty("capture"));
        Assert.False(builder.TryPushProperty("${secret:vault/prod-db-password}"));

        Assert.Equal("$.capture", builder.Build().JsonPath);
    }
}
