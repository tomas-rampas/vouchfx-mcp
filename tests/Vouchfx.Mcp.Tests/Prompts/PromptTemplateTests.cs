using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// The prompt template renderer — the seam US-S5-03 and US-S5-04's three remaining prompts will
/// reuse, so its behaviour is pinned here rather than only through <c>author_scenario</c>'s content.
/// </summary>
/// <remarks>
/// Deliberately a tiny, closed syntax: a value placeholder and two section forms. Prompts are prose
/// with a handful of optional paragraphs; anything more expressive would be a template ENGINE, which
/// is a dependency and a class of bug this repository has no reason to take on. Everything the four
/// prompts need is here, and what is not supported throws rather than rendering something surprising.
/// </remarks>
public class PromptTemplateTests
{
    private static readonly Dictionary<string, string?> Empty =
        new Dictionary<string, string?>();

    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    // ── Value substitution ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlaceholder_IsReplacedByItsValue() =>
        Assert.Equal("hello world", PromptTemplate.Render("hello {{name}}", Values(("name", "world"))));

    [Fact]
    public void APlaceholderAppearingTwice_IsReplacedBothTimes() =>
        Assert.Equal("a/a", PromptTemplate.Render("{{x}}/{{x}}", Values(("x", "a"))));

    [Fact]
    public void AnAbsentPlaceholder_RendersAsNothingRatherThanAsItsOwnName() =>
        // A template that leaked "{{specPath}}" into a host's prompt would be worse than one that
        // said nothing there — the host would try to use the literal as a path.
        Assert.Equal("path: ", PromptTemplate.Render("path: {{specPath}}", Empty));

    [Fact]
    public void TextWithNoPlaceholders_IsReturnedUnchanged() =>
        Assert.Equal("plain prose.", PromptTemplate.Render("plain prose.", Empty));

    // ── Positive sections: {{#name}}…{{/name}} ─────────────────────────────────────────────────

    [Fact]
    public void APositiveSection_IsKeptWhenItsArgumentIsSupplied() =>
        Assert.Equal(
            "[id: x]",
            PromptTemplate.Render("[{{#flowId}}id: {{flowId}}{{/flowId}}]", Values(("flowId", "x"))));

    [Fact]
    public void APositiveSection_IsDroppedWhenItsArgumentIsAbsent() =>
        Assert.Equal("[]", PromptTemplate.Render("[{{#flowId}}id: {{flowId}}{{/flowId}}]", Empty));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void APositiveSection_IsDroppedWhenItsArgumentIsBlank(string value) =>
        // Blank is treated as absent throughout: a host that sends an empty string for an optional
        // argument means "I have nothing to say here", and rendering a labelled empty paragraph for it
        // would be noise a model has to reason about.
        Assert.Equal("[]", PromptTemplate.Render("[{{#flowId}}id{{/flowId}}]", Values(("flowId", value))));

    // ── Negative sections: {{^name}}…{{/name}} ─────────────────────────────────────────────────

    [Fact]
    public void ANegativeSection_IsKeptWhenItsArgumentIsAbsent() =>
        Assert.Equal("[choose one]", PromptTemplate.Render("[{{^specPath}}choose one{{/specPath}}]", Empty));

    [Fact]
    public void ANegativeSection_IsDroppedWhenItsArgumentIsSupplied() =>
        Assert.Equal(
            "[]",
            PromptTemplate.Render("[{{^specPath}}choose one{{/specPath}}]", Values(("specPath", "e2e/x.yaml"))));

    [Fact]
    public void ThePositiveAndNegativeFormsAreExactlyComplementary()
    {
        const string template = "{{#a}}yes{{/a}}{{^a}}no{{/a}}";

        Assert.Equal("yes", PromptTemplate.Render(template, Values(("a", "v"))));
        Assert.Equal("no", PromptTemplate.Render(template, Empty));
    }

    // ── Nesting ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SectionsNest()
    {
        const string template = "{{#a}}A{{#b}}B{{/b}}{{/a}}";

        Assert.Equal("AB", PromptTemplate.Render(template, Values(("a", "1"), ("b", "1"))));
        Assert.Equal("A", PromptTemplate.Render(template, Values(("a", "1"))));
        Assert.Equal(string.Empty, PromptTemplate.Render(template, Values(("b", "1"))));
    }

    [Fact]
    public void ASectionNestedInsideAnotherOfTheSameName_MatchesByDepth() =>
        // Not something the prompts do, but a matcher that took the FIRST closing tag would silently
        // mis-slice such a template rather than failing, so the depth counting is pinned.
        Assert.Equal(
            "outer inner",
            PromptTemplate.Render("{{#a}}outer {{#a}}inner{{/a}}{{/a}}", Values(("a", "1"))));

    // ── Malformed templates fail loudly ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{{#a}}never closed")]
    [InlineData("{{^a}}never closed")]
    [InlineData("{{#a}}mismatched{{/b}}")]
    public void AnUnbalancedSection_Throws(string template) =>
        // These are OUR files, embedded at build time, so a malformed one is a packaging fault and
        // must fail loudly at load — never render a half-template into a host's context window.
        Assert.Throws<PromptTemplateException>(() => PromptTemplate.Render(template, Empty));

    [Fact]
    public void AStraySectionClose_Throws() =>
        Assert.Throws<PromptTemplateException>(() => PromptTemplate.Render("dangling{{/a}}", Empty));

    // ── Argument values are inert ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnArgumentValueContainingTemplateSyntax_IsNotItselfRendered()
    {
        // A host-supplied value must never be able to reach the template engine — otherwise a
        // flowDescription of "{{#x}}…" could delete or duplicate part of the procedure. Substitution
        // is single-pass and values are copied verbatim.
        var rendered = PromptTemplate.Render("desc: {{d}}", Values(("d", "{{#a}}injected{{/a}}")));

        Assert.Equal("desc: {{#a}}injected{{/a}}", rendered);
    }

    [Fact]
    public void AnArgumentValueIsNotScannedForFurtherPlaceholders() =>
        Assert.Equal("{{other}}", PromptTemplate.Render("{{d}}", Values(("d", "{{other}}"), ("other", "X"))));
}
