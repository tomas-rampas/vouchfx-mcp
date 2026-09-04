using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1204 — a step's <c>capture</c> declares a variable name nothing in the suite ever
/// interpolates (spec §5.5: a warning).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the finding the seam's own remarks use as their worked example of the hazard</b>, and
/// it is worth restating at the site: <c>"capture '{name}' is never used"</c>, with the name taken
/// from <see cref="SemanticAnalysisContext.Facts"/>, is both the obvious wording and an instant
/// failure of the whole call — because the fact set deliberately retains identifiers literally
/// spelled <c>${secret:vault/prod-db-password}</c>, and <see cref="SemanticAnalyser"/>'s choke point
/// refuses to publish one. Every name here therefore goes through
/// <see cref="SemanticFinding.Identifier"/>, and the PATH is built the same way (see below).
/// </para>
/// <para>
/// <b>Two paths, chosen by the name.</b> An ordinary capture is addressed precisely —
/// <c>$.steps[0].capture.orderId</c> — because that is what lets a host jump to the exact line. A
/// capture whose name carries a reference is addressed at its CONTAINER
/// (<c>$.steps[0].capture</c>) instead: a path segment naming it would smuggle the reference out
/// through <see cref="Diagnostic.Path"/>, which the choke point checks for precisely that reason.
/// The finding is still reported, and still located to the right step.
/// </para>
/// <para>
/// <b>"Used" means "appears in the document's placeholder set", not "appears in a LATER step".</b>
/// Ordering is VFX-D-1203's question; this code's is existence. Keeping them separate is what lets
/// a suite that captures and then uses a value out of order get both findings — the ordering
/// problem and no spurious "never used" — rather than one confusing hybrid.
/// </para>
/// <para>
/// <b>A <c>script.csharp</c> step consumes captures WITHOUT a placeholder, and this rule has to
/// know that or it fires on valid suites.</b> A script reads the shared context directly —
/// <c>Vars["orderId"]</c> — so a capture used only by a script appears in no <c>{token}</c>
/// anywhere and the placeholder set says, truthfully and uselessly, that nothing interpolates it.
/// Two mitigations, in increasing order of bluntness:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Inline <c>code</c>: an ordinal substring test.</b> A capture whose name appears anywhere in
/// any script body is treated as used. Deliberately a plain <c>Contains</c> rather than a parse:
/// this server does not host a C# compiler, and the failure modes are asymmetric — a substring hit
/// on an unrelated identifier suppresses one advisory warning, while a miss reports a wrong finding
/// on a working suite. Given only those two, over-suppressing is the correct bias for an advisory
/// code.
/// </description></item>
/// <item><description>
/// <b><c>file</c>: the rule stops entirely.</b> A script step that names a FILE has its source
/// outside the document, and this server never reads a suite's neighbouring files — so the evidence
/// that would answer "is this capture used?" is unreachable BY CONSTRUCTION, not merely absent.
/// Reporting anyway would be guessing, and suppressing only the captures the rule happened to find
/// mentioned would be guessing with extra steps. The rule yields nothing for such a document and
/// says so here.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class UnusedCaptureRule : ISemanticRule
{
    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.UnusedCapture;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();

        var scriptSources = new List<string>();
        if (CollectScriptSources(context.Document, scriptSources) is ScriptSourceReach.Unreadable)
        {
            // A script whose body lives in a file this server never opens: no capture in this
            // document can be shown to be unused. See the class remarks.
            return findings;
        }

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            if (!step.TryGetProperty("capture", out var capture) || capture.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var entry in capture.EnumerateObject())
            {
                // The fact set, not the summary: a capture past the summary's 1 000-entry cap, or
                // one filtered out of it for carrying a reference, is still a capture — and a
                // placeholder past the same cap is still a use.
                if (context.Facts.Placeholders.Contains(entry.Name) ||
                    IsReadByAnyScript(entry.Name, scriptSources))
                {
                    continue;
                }

                var capturePath = SuitePath.Step(index).Property("capture");
                var carriesReference = entry.Name.Contains("${", StringComparison.Ordinal);

                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Warning,
                    $"Capture {SemanticFinding.Identifier(entry.Name)} is never interpolated by any "
                    + "step in this suite. Remove it, or use it in a later step.",
                    carriesReference ? capturePath : capturePath.Property(entry.Name)));
            }
        }

        return findings;
    }

    /// <summary>How far this rule can see into the suite's script sources.</summary>
    private enum ScriptSourceReach
    {
        /// <summary>Every script's source is inline, or there are no scripts at all.</summary>
        Readable,

        /// <summary>At least one script names a <c>file</c> whose contents this server never reads.</summary>
        Unreadable,
    }

    /// <summary>
    /// Collects every inline <c>script.*</c> body in the document, and reports whether any script
    /// instead names a file.
    /// </summary>
    private static ScriptSourceReach CollectScriptSources(JsonElement root, List<string> sources)
    {
        var reach = ScriptSourceReach.Readable;

        foreach (var (_, step) in SuiteDocument.Steps(root))
        {
            if (!PlaceholderScanner.IsScriptStep(step))
            {
                continue;
            }

            if (SuiteDocument.StringProperty(step, "file") is not null)
            {
                reach = ScriptSourceReach.Unreadable;
            }

            if (SuiteDocument.StringProperty(step, "code") is { } code)
            {
                sources.Add(code);
            }
        }

        return reach;
    }

    /// <summary>
    /// Whether <paramref name="name"/> appears anywhere in any inline script body — see the class
    /// remarks for why an ordinal substring test is the right instrument here.
    /// </summary>
    private static bool IsReadByAnyScript(string name, List<string> sources)
    {
        foreach (var source in sources)
        {
            if (source.Contains(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
