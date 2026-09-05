using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1206 — a <c>verifyMode: RETRY</c> step whose polling window is not stated well: no
/// <c>timeout</c> at all (the engine's default silently applies), or one above this server's
/// advisory maximum.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both arms of spec §5.5's row, one code</b> — "verifyMode: RETRY without timeout (warning:
/// default applies) / timeout above policy max". They are one subject with one remedy ("state a
/// sensible timeout"), and their messages, not a host's switch statement, are where a human needs
/// the distinction. Same reasoning VFX-E-1152's catalogue entry gives for its two shapes.
/// </para>
/// <para>
/// <b>DEVIATION, recorded rather than hidden: there is no engine-published "policy max".</b> The
/// spec table names one; nothing in the pinned engine, the composed schema, or
/// <c>vendored/language-reference.md</c> defines a number, and there is no upstream ask tracking
/// one. Two options were available — implement only the missing-timeout arm and declare the second
/// gated, or state an explicit, server-owned advisory bound. This rule takes the second, because
/// the finding is a WARNING whose whole content is advice: <see cref="AdvisoryMaximumSeconds"/> is
/// named in the message and on the code's catalogue page, so a reader can see it is this server's
/// opinion rather than the engine's rule, and no verdict turns on it. Should the engine ever
/// publish a real bound, this constant is the single place that changes.
/// </para>
/// <para>
/// <b>An unparseable timeout produces nothing.</b> The composed schema types <c>timeout</c> as a
/// string OR a number, and a malformed duration is the schema pass's finding to make; a second,
/// differently-worded complaint from here would be noise, and throwing on one would cost the call
/// its other nine rules.
/// </para>
/// </remarks>
internal sealed class RetryTimeoutRule : ISemanticRule
{
    /// <summary>
    /// The longest polling window this server advises for a single RETRY step, in seconds.
    /// </summary>
    /// <remarks>
    /// Five minutes. Chosen against the constraint a reader can check rather than by taste: a RETRY
    /// step polls INSIDE a run, and a run that spends longer than this waiting on one assertion is
    /// past the point where the failure will be diagnosed as "slow" rather than "hung". See the
    /// class remarks for why this server states a number at all.
    /// </remarks>
    public const int AdvisoryMaximumSeconds = 300;

    /// <summary>The verify mode this rule is scoped to.</summary>
    private const string RetryVerifyMode = "RETRY";

    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.RetryTimeoutPolicy;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            // Case-sensitive, matching the composed schema's own vocabulary convention (its
            // dependency.type $comment: exactly one canonical spelling per DSL term). A
            // differently-cased value is a schema rejection, not this rule's business.
            if (!string.Equals(SuiteDocument.StringProperty(step, "verifyMode"), RetryVerifyMode, StringComparison.Ordinal))
            {
                continue;
            }

            if (!step.TryGetProperty("timeout", out var timeout))
            {
                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Warning,
                    "This step polls with verifyMode: RETRY but declares no timeout, so the engine's "
                    + "default bounds the polling window. State a timeout so the step's own budget is "
                    + "visible in the suite.",
                    SuitePath.Step(index)));

                continue;
            }

            if (TryReadSeconds(timeout) is { } seconds && seconds > AdvisoryMaximumSeconds)
            {
                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Warning,
                    $"This RETRY step's timeout is {seconds.ToString("0.##", CultureInfo.InvariantCulture)}s, "
                    + $"above the {AdvisoryMaximumSeconds}s this server advises for a single polling "
                    + "step. Shorten it, or split the wait across steps.",
                    SuitePath.Step(index).Property("timeout")));
            }
        }

        return findings;
    }

    /// <summary>
    /// Reads a <c>timeout</c> value as a number of seconds, or <see langword="null"/> when it is
    /// not a shape this rule understands.
    /// </summary>
    /// <remarks>
    /// The forms the composed schema's <c>timeout</c> description names — a bare number of seconds,
    /// or a duration string such as <c>30s</c> — plus the minute/hour/millisecond suffixes the same
    /// convention implies. Anything else returns <see langword="null"/> and this rule says nothing;
    /// see the class remarks.
    /// </remarks>
    private static double? TryReadSeconds(JsonElement timeout)
    {
        if (timeout.ValueKind == JsonValueKind.Number)
        {
            return timeout.TryGetDouble(out var numeric) ? numeric : null;
        }

        if (timeout.ValueKind != JsonValueKind.String || timeout.GetString() is not { Length: > 0 } text)
        {
            return null;
        }

        var trimmed = text.Trim();

        // Longest suffix first: "ms" must be tested before "m", or 500ms reads as 500 minutes.
        (string Suffix, double Multiplier)[] units =
        [
            ("ms", 0.001),
            ("s", 1),
            ("m", 60),
            ("h", 3600),
        ];

        foreach (var (suffix, multiplier) in units)
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal) &&
                double.TryParse(
                    trimmed[..^suffix.Length],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return value * multiplier;
            }
        }

        return double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }
}
