using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1209 — an asynchronous step type left on the default <c>IMMEDIATE</c> verify mode, so it
/// asserts once against a result that has not arrived yet. Spec §5.5: a warning WITH a fix.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only rule in the set that ships a machine-applicable
/// <see cref="DiagnosticFix.Replacement"/></b>, and the reason it can is that its remedy is one
/// literal line with no authoring judgement in it. Every other code's fix would require choosing a
/// name, a value, or a position, which is the author's call — and a "fix" a host applies blind must
/// never be a guess.
/// </para>
/// <para>
/// <b>The fix text is composed to pass the choke point, deliberately.</b>
/// <see cref="SemanticAnalyser"/> checks both halves of a <see cref="DiagnosticFix"/> for
/// <c>${…}</c>, so the replacement is a compile-time constant with no document-derived material in
/// it at all. That is not a constraint this rule chafes against — a replacement built out of suite
/// content would be a different, more dangerous feature.
/// </para>
/// <para>
/// <b>Which types are "asynchronous", and the one QUALIFIER that keeps this rule usable.</b>
/// <c>mq-expect.*</c> and <c>webhook-listen.*</c> are unconditionally asynchronous: both wait for
/// something another system will do. <c>db-assert.*</c> is not — a read-your-writes assertion after
/// an HTTP call is synchronous, and flagging every one would fire on most suites in the corpus. So
/// spec §5.5's own qualifier is honoured literally: a <c>db-assert.*</c> step is reported only when
/// some EARLIER step published to a broker (<c>mq-publish.*</c>), which is exactly the shape where
/// the write arrives out of band.
/// </para>
/// <para>
/// <b>An explicit <c>verifyMode: IMMEDIATE</c> is reported too</b>, not just an omitted one: the
/// finding is about an async assertion being polled zero times, and it is polled zero times either
/// way. (A step already on <c>RETRY</c> is silent here; whether its TIMEOUT is stated well is
/// VFX-D-1206's question.)
/// </para>
/// </remarks>
internal sealed class AsyncVerifyModeRule : ISemanticRule
{
    /// <summary>Step-type family prefixes that are asynchronous regardless of what precedes them.</summary>
    /// <remarks>
    /// Exposed rather than private so <c>AsyncVerifyModeRuleTests</c> can gate every prefix against
    /// <see cref="StepTypeCatalogue.All"/>: a prefix matching no real step type is a rule arm that
    /// can never fire, which an <c>ENGINE_PIN</c> bump renaming a family would produce silently.
    /// </remarks>
    public static IReadOnlyList<string> AlwaysAsyncPrefixes { get; } = ["mq-expect.", "webhook-listen."];

    /// <summary>Step types that are asynchronous only after a publish — see the class remarks.</summary>
    private const string AfterPublishPrefix = "db-assert.";

    /// <summary>The family whose presence earlier in the suite arms <see cref="AfterPublishPrefix"/>.</summary>
    private const string PublishPrefix = "mq-publish.";

    /// <summary>The one line the fix tells a host to add.</summary>
    /// <remarks>
    /// A compile-time constant, with no document-derived material — see the class remarks on the
    /// choke point. Spelled exactly as the composed schema's <c>verifyMode</c> enum spells it: the
    /// vocabulary is case-sensitive, so a lower-cased replacement would be a fix that fails
    /// validation.
    /// </remarks>
    private const string RetryReplacement = "verifyMode: RETRY";

    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.AsyncStepWithoutRetry;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();
        var publishSeen = false;

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            var type = SuiteDocument.StringProperty(step, "type");
            if (type is null)
            {
                continue;
            }

            if (IsAsynchronous(type, publishSeen) &&
                !string.Equals(SuiteDocument.StringProperty(step, "verifyMode"), "RETRY", StringComparison.Ordinal))
            {
                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Warning,
                    $"Step type {SemanticFinding.Identifier(type)} waits on work another system "
                    + "does, but this step asserts once instead of polling. Set verifyMode: RETRY so "
                    + "the engine polls with bounded exponential backoff.",
                    SuitePath.Step(index),
                    new DiagnosticFix(
                        "Add verifyMode: RETRY to this step so the engine polls for the expected "
                        + "result instead of asserting once.",
                        RetryReplacement)));
            }

            // Updated AFTER the check, so a mq-publish step does not arm the rule for itself.
            if (type.StartsWith(PublishPrefix, StringComparison.Ordinal))
            {
                publishSeen = true;
            }
        }

        return findings;
    }

    private static bool IsAsynchronous(string type, bool publishSeen)
    {
        foreach (var prefix in AlwaysAsyncPrefixes)
        {
            if (type.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return publishSeen && type.StartsWith(AfterPublishPrefix, StringComparison.Ordinal);
    }
}
