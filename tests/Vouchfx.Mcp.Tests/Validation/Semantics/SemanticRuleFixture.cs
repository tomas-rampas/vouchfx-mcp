using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// Builds a <see cref="SemanticAnalysisContext"/> for one rule under test <b>the same way
/// production does</b> — one YAML→JSON conversion, one <see cref="YamlLineResolver"/> parse, one
/// <see cref="SuiteSummaryBuilder"/> walk — so a rule test never accidentally proves something about
/// a hand-built context that the real pipeline would never hand it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fixture rather than a literal <c>new SemanticAnalysisContext(...)</c> per test.</b> The
/// fact set is the seam's set-membership authority, and hand-writing one per test is exactly how a
/// rule test comes to assert against facts the real builder would not have produced (a capture the
/// builder collects but the test forgot, a placeholder the scanner's <c>$</c> guard drops). Deriving
/// it from the YAML under test keeps the rule and its input honest together.
/// </para>
/// <para>
/// <b>Disposable, and the disposal matters.</b> <see cref="SemanticAnalysisContext.Document"/> is a
/// view into a <see cref="JsonDocument"/> whose lifetime this fixture owns — the same lifetime rule
/// <c>SuiteValidator</c>'s <c>using</c> enforces in production. A test that let this escape its
/// <c>using</c> would be reading a disposed document, which is the failure the seam's own remarks
/// warn rule authors about.
/// </para>
/// </remarks>
internal sealed class SemanticRuleFixture : IDisposable
{
    private readonly JsonDocument _document;

    private SemanticRuleFixture(JsonDocument document, SemanticAnalysisContext context)
    {
        _document = document;
        Context = context;
    }

    /// <summary>The context the rule under test is handed.</summary>
    public SemanticAnalysisContext Context { get; }

    /// <summary>
    /// The suite path every fixture reports, so a test asserting on
    /// <see cref="DiagnosticLocation.File"/> has one literal to compare against.
    /// </summary>
    public const string SourceName = "fixture.e2e.yaml";

    /// <summary>Builds the context for <paramref name="yaml"/> exactly as the worker would.</summary>
    public static SemanticRuleFixture For(string yaml)
    {
        var document = YamlToJsonConverter.Convert(yaml);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        return new SemanticRuleFixture(
            document,
            new SemanticAnalysisContext(
                document.RootElement,
                YamlLineResolver.TryParseYamlRoot(yaml),
                digest.Summary,
                digest.Facts,
                SourceName));
    }

    /// <summary>Runs <paramref name="rule"/> through the real choke point, never around it.</summary>
    /// <remarks>
    /// Deliberately <see cref="SemanticAnalyser.Analyse(SemanticAnalysisContext, IReadOnlyList{ISemanticRule})"/>
    /// rather than <c>rule.Evaluate(context)</c>: every finding a rule test asserts on must have
    /// survived the no-secret-echo guard, so a rule that would fail the call in production fails its
    /// own unit test rather than passing one and crashing a worker later.
    /// </remarks>
    public IReadOnlyList<Diagnostic> Run(ISemanticRule rule) => RunWithOutcome(rule).Findings;

    /// <summary>
    /// The same run, keeping the <see cref="SemanticAnalysisOutcome.Truncated"/> flag — for the
    /// tests that are about the channel's own cap rather than about a rule's verdict.
    /// </summary>
    public SemanticAnalysisOutcome RunWithOutcome(ISemanticRule rule) =>
        SemanticAnalyser.Analyse(Context, [rule]);

    public void Dispose() => _document.Dispose();
}
