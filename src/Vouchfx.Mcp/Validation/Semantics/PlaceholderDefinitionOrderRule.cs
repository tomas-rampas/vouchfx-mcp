using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1203 — a <c>{placeholder}</c> is interpolated before any <c>capture</c> or root
/// <c>variables</c> entry provides its value.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one rule in the set that genuinely needs DOCUMENT ORDER</b>, which is why the seam hands
/// rules the parsed document and not just the fact sets: the same token is a defect in step 0 and
/// correct in step 2, and no set-membership question can tell those apart. The JSON projection
/// preserves array order, so walking <c>steps</c> in order IS the order the engine runs them in.
/// </para>
/// <para>
/// <b>What counts as "provided", and why the set is <c>variables ∪ earlier captures</c>.</b> Root
/// <c>variables</c> are loaded into the shared variable context before the first step runs, so they
/// are available everywhere — that is precisely why <see cref="SuiteFacts.Variables"/> exists with
/// no counterpart on the published summary. A capture, by contrast, is PRODUCED BY running its
/// step, so it becomes available to step <c>n+1</c> and not to step <c>n</c> itself. A rule that
/// added a step's own captures before scanning it would silently miss the commonest form of this
/// mistake (capturing and interpolating in the same step).
/// </para>
/// <para>
/// <b>Reserved forms are never reported.</b> <c>{svc::orders-api.baseUrl}</c> and
/// <c>{conn::orders-db}</c> resolve from the ENVIRONMENT, not from the variable context, so testing
/// them against captures and variables would report a wrong finding on a perfectly valid suite —
/// the one failure mode a semantic rule must not have. <see cref="PlaceholderScanner.IsReservedForm"/>
/// is the shared discriminator, so this rule and the digest cannot disagree about what a reserved
/// token looks like.
/// </para>
/// <para>
/// <b>A host-owned LISTENER or RECEIVER name is defined document-wide, not by an earlier step.</b>
/// <c>webhook-listen.*</c>'s <c>listener</c> and <c>trace-expect.*</c>'s <c>receiver</c> name
/// infrastructure the ENGINE stands up, and the language reference is explicit that the engine
/// stages the value at the plain Vars key precisely so an EARLIER step can use it:
/// "stages its URL at svc::&lt;listener&gt; (and at the plain &lt;listener&gt; Vars key so an
/// earlier step can interpolate {&lt;listener&gt;})" (<c>vendored/language-reference.md:514</c>), and
/// the same sentence for <c>receiver</c> at <c>vendored/language-reference.md:502</c>
/// ("so an earlier step can hand it to the SUT's OTel SDK configuration"). The canonical suite —
/// step 0 configures a callback URL with <c>{callbacks}</c>, step 1 is a
/// <c>webhook-listen.http</c> with <c>listener: callbacks</c> — is therefore CORRECT, and a
/// naive document-order reading reported it. These names are collected across the whole document
/// before the ordered walk starts, which is what "staged before the first step runs" actually
/// means.
/// </para>
/// <para>
/// <b>A <c>script.*</c> step's <c>code</c> and <c>file</c> are not scanned</b> — the shared
/// scanner's <c>excludeScriptStepSource</c> switch, passed here and by the digest alike. C#
/// interpolation is spelled with the same braces, so <c>$"order {id} created"</c> in a script body
/// is not a suite placeholder and reporting it is a wrong finding on a valid suite. See
/// <see cref="PlaceholderScanner.Scan"/>.
/// </para>
/// <para>
/// <b>Located at the STEP, not at the offending scalar.</b> A token can appear anywhere in a step's
/// nested structure (a header value, a URL, an element of a JSON body), and pointing at whichever
/// leaf happened to carry it first would be precise about the wrong thing: the author's fix is to
/// reorder steps or add a capture, which is a decision about the step. The message names the token,
/// so the leaf is findable.
/// </para>
/// </remarks>
internal sealed class PlaceholderDefinitionOrderRule : ISemanticRule
{
    /// <summary>
    /// The step-type family prefixes whose named field the ENGINE stages as a plain Vars key, and
    /// the field each one uses — see the class remarks and
    /// <c>vendored/language-reference.md</c> lines 502 and 514.
    /// </summary>
    /// <remarks>
    /// Prefix-keyed on the FAMILY rather than on the full <c>family.provider</c> type, so a second
    /// provider added upstream (a <c>webhook-listen.grpc</c>, say) inherits the staging rather than
    /// silently reintroducing the false positive. <c>PlaceholderDefinitionOrderRuleTests</c> gates
    /// both field names against the vendored schema's own required-field lists, the way
    /// <c>UndeclaredDependencyRuleTests</c> gates its table.
    /// </remarks>
    public static IReadOnlyList<(string TypePrefix, string Field)> StagedVarsKeyFields { get; } =
    [
        ("webhook-listen.", "listener"),
        ("trace-expect.", "receiver"),
    ];

    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.PlaceholderUsedBeforeDefinition;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();

        // Seeded from the fact set (uncapped, unfiltered) rather than the summary, for the reason
        // SemanticAnalysisContext.Facts' remarks give: a variable named after a reference, or the
        // 1 001st variable in a large suite, is still declared.
        var defined = new HashSet<string>(context.Facts.Variables, StringComparer.Ordinal);

        // DOCUMENT-WIDE, and before the ordered walk: the engine stages a listener's/receiver's
        // plain Vars key before any step runs, precisely so an EARLIER step can interpolate it. See
        // the class remarks for the language reference's own wording.
        AddStagedVarsKeys(context.Document, defined);

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            var used = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // The SHARED scanner, so "what is a placeholder" is the same question here as it is for
            // the digest and the fact set. Applied to this step's subtree only — the whole point is
            // WHICH step used the token.
            PlaceholderScanner.Scan(
                step,
                name =>
                {
                    if (seen.Add(name))
                    {
                        used.Add(name);
                    }
                },
                excludeScriptStepSource: true);

            foreach (var name in used)
            {
                if (PlaceholderScanner.IsReservedForm(name) || defined.Contains(name))
                {
                    continue;
                }

                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Warning,
                    $"Placeholder {SemanticFinding.Identifier(name)} is interpolated here, but no "
                    + "earlier step captures it and no root variable declares it. Move the step that "
                    + "captures it earlier, or declare the name under variables.",
                    SuitePath.Step(index)));
            }

            // Only AFTER scanning: a capture is produced by running this step, so it cannot resolve
            // a token this same step interpolates.
            AddCaptureNames(step, defined);
        }

        return findings;
    }

    /// <summary>
    /// Adds every host-owned listener/receiver name the document declares, from anywhere in it.
    /// </summary>
    private static void AddStagedVarsKeys(JsonElement root, HashSet<string> defined)
    {
        foreach (var (_, step) in SuiteDocument.Steps(root))
        {
            if (SuiteDocument.StringProperty(step, "type") is not { } type)
            {
                continue;
            }

            foreach (var (prefix, field) in StagedVarsKeyFields)
            {
                if (type.StartsWith(prefix, StringComparison.Ordinal) &&
                    SuiteDocument.StringProperty(step, field) is { } name)
                {
                    defined.Add(name);
                }
            }
        }
    }

    private static void AddCaptureNames(JsonElement step, HashSet<string> defined)
    {
        if (step.TryGetProperty("capture", out var capture) && capture.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in capture.EnumerateObject())
            {
                defined.Add(entry.Name);
            }
        }
    }
}
