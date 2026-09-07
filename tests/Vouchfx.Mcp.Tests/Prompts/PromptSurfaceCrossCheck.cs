using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Prompts;
using Vouchfx.Mcp.Schema;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// Cross-checks a RENDERED prompt against the surface this server actually advertises: every tool it
/// names exists, every resource URI it names resolves, and every spelled-out ARGUMENT VALUE is one
/// the owning tool accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, stated plainly: the presence-only check it replaces could not see the two
/// defects that shipped.</b> US-S5-02's first render tests asserted that identifiers like
/// <c>get_schema</c> and <c>normalize_suite</c> APPEAR in the text. Both did. What shipped anyway was
/// <c>format: markdown</c> — not one of <see cref="GetSchemaOrchestrator.Formats"/>, so step 1 of the
/// procedure failed deterministically with VFX-E-1006 — and a <c>normalize_suite</c> step that never
/// named <c>normalize: true</c>, so the flag defaulted false, <c>normalizedYaml</c> came back null,
/// and the hand-off step had nothing to write. A test that checks names can only ever catch a wrong
/// NAME; these were wrong VALUES, and no amount of identifier presence would have found them.
/// </para>
/// <para>
/// <b>Built as the seam US-S5-03 and US-S5-04 run through.</b> Three more prompts follow, each
/// naming tools and values of its own. <see cref="AssertRenderedPromptMatchesAdvertisedSurface"/>
/// takes a rendered string and a live harness and does the whole check, so a new prompt's tests are
/// one call rather than a fresh set of hand-written assertions that will drift from this one.
/// </para>
/// <para>
/// <b>What it can and cannot catch.</b> It verifies that every identifier the prompt spells out is
/// real and that every argument value it spells out is accepted — the mechanical half. It cannot
/// verify the procedure is GOOD ADVICE, and does not pretend to: that stays a reviewer's job. The
/// value-checking half is also necessarily enumerated (see <see cref="ArgumentValueRules"/>), because
/// there is no way to derive "this literal is an argument of that tool" from prose; adding a rule
/// when a prompt starts spelling out a new tool's argument is the maintenance cost, and it is stated
/// here rather than discovered later.
/// </para>
/// </remarks>
internal static class PromptSurfaceCrossCheck
{
    /// <summary>
    /// A backticked token in the rendered markdown — the form every tool name, resource URI and
    /// argument value in these prompts is written in.
    /// </summary>
    private static readonly Regex BacktickedToken = new(@"`(?<token>[^`\r\n]+)`", RegexOptions.Compiled);

    /// <summary>A bare tool-name-shaped token: lower snake case, at least one underscore.</summary>
    private static readonly Regex ToolNameShape = new(@"^[a-z][a-z0-9]*(_[a-z0-9]+)+$", RegexOptions.Compiled);

    /// <summary>A <c>key: value</c> pair written inside backticks, e.g. <c>format: summary</c>.</summary>
    private static readonly Regex ArgumentPair =
        new(@"^(?<key>[A-Za-z][A-Za-z0-9]*)\s*:\s*(?<value>[^`\r\n]+?)$", RegexOptions.Compiled);

    /// <summary>
    /// The argument VALUES this cross-check knows how to verify, keyed by the argument name as the
    /// prompts spell it, each with the set of values the owning tool actually accepts.
    /// </summary>
    /// <remarks>
    /// <b>Every entry reads its accepted set from the PRODUCTION constant</b>, never a re-typed list —
    /// that is what makes the check track the tool rather than a snapshot of it. Where a tool exposes
    /// no such constant the rule states the accepted values inline with a comment saying where they
    /// come from, which is weaker and is marked as such.
    /// </remarks>
    private static readonly Dictionary<string, ArgumentValueRule> ArgumentValueRules = new(StringComparer.Ordinal)
    {
        // get_schema's `format`. GetSchemaOrchestrator.Formats is public precisely so a caller can
        // discover the accepted set; this reads it rather than restating it.
        ["format"] = new("get_schema", GetSchemaOrchestrator.Formats),

        // validate_suite's / normalize_suite's `level`. ValidationLevels.All is the same shape.
        ["level"] = new("validate_suite", Vouchfx.Mcp.Validation.ValidationLevels.All),

        // normalize_suite's `normalize` is a boolean. The accepted set is the language's, not a tool
        // constant's, so it is stated inline — and the POINT of checking it is not that `true` is a
        // legal boolean but that the prompt spells the flag out at all, which B2 is the evidence for.
        ["normalize"] = new("normalize_suite", ["true", "false"]),

        // validate_suite's semanticDiagnostics severity, read from the production constant that
        // Diagnostic's own constructor validates against. Added because it was already MATCHING the
        // pair shape and being silently skipped for want of a rule (a spec review's finding) — the
        // worst of both worlds: it looked checked and was not.
        ["severity"] = new("a validate_suite diagnostic", [.. Diagnostic.ValidSeverities]),

        // The step-language values a prompt may quote. These are the engine's, taken from the
        // vendored language reference; a value outside them would be advice that cannot be followed.
        ["verifyMode"] = new("the .e2e.yaml language", ["IMMEDIATE", "RETRY"]),

        // DELIBERATELY ABSENT: `wait`. run_suite has such an argument, but no prompt spells it out —
        // a rule for it would be dead weight that reads as coverage, which is exactly the problem the
        // `level` rule had before the markdown was fixed to make it fire. Add it when a prompt
        // actually writes `wait: true`, not before.
    };

    /// <param name="OwningSurface">Named in the failure message, so a reader knows what to go and check.</param>
    /// <param name="AcceptedValues">Every value that surface accepts.</param>
    private sealed record ArgumentValueRule(string OwningSurface, IReadOnlyList<string> AcceptedValues);

    /// <summary>
    /// Asserts every tool, resource URI and argument value <paramref name="rendered"/> names is one
    /// this server advertises or accepts.
    /// </summary>
    /// <param name="rendered">The rendered prompt text.</param>
    /// <param name="harness">A started harness — the live advertised surface to check against.</param>
    /// <param name="cancellationToken">Bounds the harness calls.</param>
    public static async Task AssertRenderedPromptMatchesAdvertisedSurface(
        string rendered, McpTestHarness harness, CancellationToken cancellationToken)
    {
        var tools = (await harness.Client.ListToolsAsync(cancellationToken: cancellationToken))
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        var resources = (await harness.Client.ListResourcesAsync(cancellationToken: cancellationToken))
            .Select(resource => resource.Uri)
            .ToHashSet(StringComparer.Ordinal);

        var templates = (await harness.Client.ListResourceTemplatesAsync(cancellationToken: cancellationToken))
            .Select(template => template.ProtocolResourceTemplate.UriTemplate)
            .ToArray();

        // Anti-vacuity: an empty surface would make every membership check below trivially pass.
        Assert.NotEmpty(tools);
        Assert.NotEmpty(resources);
        Assert.NotEmpty(templates);

        // PER-KIND counters, not one union flag (a review's finding). A single `checkedSomething`
        // could be satisfied entirely by tool names, so the VALUE-checking half — the half that exists
        // because two argument-value defects shipped — could silently check nothing at all and still
        // report success. Each kind now has to prove it fired.
        var toolsChecked = 0;
        var resourcesChecked = 0;
        var valuesChecked = 0;

        foreach (Match match in BacktickedToken.Matches(rendered))
        {
            var token = match.Groups["token"].Value.Trim();

            if (IsResourceUri(token))
            {
                AssertResourceResolves(token, resources, templates);
                resourcesChecked++;
                continue;
            }

            if (ArgumentPair.Match(token) is { Success: true } pair)
            {
                if (AssertArgumentValueAccepted(pair, rendered))
                {
                    valuesChecked++;
                }

                continue;
            }

            if (ToolNameShape.IsMatch(token))
            {
                // A snake_case token that is not a tool is possible in principle (a field name like
                // `flowDescription` is camelCase, so it does not match; `classificationHints` likewise).
                // In practice every snake_case token in these prompts is a tool name, and treating one
                // as such is exactly the check that catches a prompt naming a tool that does not exist.
                Assert.True(
                    tools.Contains(token),
                    $"The prompt names `{token}`, which is not a tool this server advertises. "
                    + $"Advertised: {string.Join(", ", tools.OrderBy(name => name, StringComparer.Ordinal))}.");
                toolsChecked++;
            }
        }

        // Per-kind floors. The numbers are deliberately low — this asserts the MECHANISM fired, not
        // how rich the prompt is — but each one must be met independently, so a markdown change that
        // stopped one token kind from matching fails here instead of quietly reducing coverage.
        Assert.True(
            toolsChecked > 0,
            "The cross-check verified no TOOL name — the token pattern has stopped matching the "
            + "prompts' markdown style.");
        Assert.True(
            resourcesChecked > 0,
            "The cross-check verified no RESOURCE URI — the prompt no longer names one, or the URI "
            + "pattern has stopped matching.");
        Assert.True(
            valuesChecked > 0,
            "The cross-check verified no ARGUMENT VALUE. This is the half that exists because two "
            + "argument-value defects shipped past an identifier-presence check, so it failing to fire "
            + "is the specific regression this assertion guards: either the prompt stopped spelling a "
            + "`key: value` pair out inside ONE backtick span, or every pair it writes has no rule in "
            + "ArgumentValueRules.");
    }

    private static bool IsResourceUri(string token) =>
        token.StartsWith("vouchfx://", StringComparison.Ordinal)
        || token.StartsWith("vouchfx-docs://", StringComparison.Ordinal);

    /// <summary>
    /// Asserts a named URI is either an advertised concrete resource or an instance of an advertised
    /// template.
    /// </summary>
    private static void AssertResourceResolves(string uri, HashSet<string> resources, string[] templates)
    {
        if (resources.Contains(uri))
        {
            return;
        }

        // Template match: compare segment counts and require every literal segment to agree, so
        // `vouchfx://examples/http-smoke` matches `vouchfx://examples/{name}` but
        // `vouchfx://exampels/http-smoke` matches nothing.
        var uriSegments = uri.Split('/');
        var matched = templates.Any(template =>
        {
            var templateSegments = template.Split('/');
            return templateSegments.Length == uriSegments.Length
                && templateSegments.Zip(uriSegments).All(pair =>
                    (pair.First.StartsWith('{') && pair.First.EndsWith('}'))
                    || string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
        });

        Assert.True(
            matched,
            $"The prompt names the resource `{uri}`, which is neither an advertised resource nor an "
            + $"instance of an advertised template. Templates: {string.Join(", ", templates)}.");
    }

    /// <summary>
    /// Asserts a spelled-out <c>key: value</c> pair names a value its owning tool accepts. Returns
    /// whether a rule applied.
    /// </summary>
    /// <remarks>
    /// <b>THIS is the check the two BLOCKERs needed.</b> `format: markdown` and a missing
    /// `normalize: true` are both argument-value facts; nothing about identifier presence could reach
    /// them. The value is compared ORDINALLY against the tool's own accepted set, because that is how
    /// the tool compares it.
    /// </remarks>
    private static bool AssertArgumentValueAccepted(Match pair, string rendered)
    {
        var key = pair.Groups["key"].Value;
        if (!ArgumentValueRules.TryGetValue(key, out var rule))
        {
            return false;
        }

        // Trim markdown emphasis a value may be wrapped in, and a trailing sentence comma.
        var value = pair.Groups["value"].Value.Trim().Trim('*', '`', ',', '.', '"', '\'');

        Assert.True(
            rule.AcceptedValues.Contains(value, StringComparer.Ordinal),
            $"The prompt instructs `{key}: {value}`, which {rule.OwningSurface} does not accept. "
            + $"Accepted: {string.Join(", ", rule.AcceptedValues)}. "
            + $"(Found in: \"{Excerpt(rendered, pair.Value)}\")");

        return true;
    }

    /// <summary>A short window of the rendered text around <paramref name="needle"/>, for the failure message.</summary>
    private static string Excerpt(string rendered, string needle)
    {
        var at = rendered.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0)
        {
            return needle;
        }

        var start = Math.Max(0, at - 60);
        var end = Math.Min(rendered.Length, at + needle.Length + 60);
        return rendered[start..end].ReplaceLineEndings(" ");
    }
}
