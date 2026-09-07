using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Diagnosis;
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

        // run_suite's `wait`. Removed in the previous round as DEAD — no prompt spelled it out then —
        // and restored here under that removal's own stated rule ("add it when a prompt actually
        // writes `wait: true`"), because heal_run now does: its re-run step must be synchronous for
        // the outcome comparison to be possible.
        //
        // The accepted set is the language's booleans, but the value that matters operationally is
        // `true`: run_suite REFUSES `wait: false` with VFX-E-1504 pre-U4, so a prompt that spelled the
        // other one would fail on that call. Both are listed because both are legal JSON for the
        // argument, and the refusal is the tool's to report — this check's job is to catch a value the
        // argument cannot take at all, such as `wait: sync`.
        ["wait"] = new("run_suite", ["true", "false"]),
    };

    /// <param name="OwningSurface">Named in the failure message, so a reader knows what to go and check.</param>
    /// <param name="AcceptedValues">Every value that surface accepts.</param>
    private sealed record ArgumentValueRule(string OwningSurface, IReadOnlyList<string> AcceptedValues);

    /// <summary>
    /// snake_case tokens a prompt may legitimately name that are NOT tools — resolved against the
    /// production vocabularies that own them, never a hand-written exclusion list.
    /// </summary>
    /// <remarks>
    /// <b>Vocabularies, not exclusions, and the distinction is the whole point.</b> An "ignore these
    /// tokens" list would silence the one token someone remembered and leave the next one — including
    /// a genuine typo — unverified. Reading the real sets means a prompt naming
    /// <c>capture_unmet</c> is checked against <c>VerdictReasonKinds.All</c> and a prompt naming
    /// <c>capture_unmett</c> still fails.
    /// <para>
    /// <c>SpecEditScopes</c> is here because <c>heal_run</c> names its members; none is snake_case
    /// today, so it contributes nothing yet and costs nothing — it is included so a future
    /// multi-word scope does not reopen this.
    /// </para>
    /// <para>
    /// <b>A LIMIT worth stating: single-word tokens are not checked at all.</b>
    /// <see cref="ToolNameShape"/> requires at least one underscore, so <c>pull</c>, <c>seed</c>,
    /// <c>timeout</c> and every other single-word reason kind pass through unexamined — a prompt
    /// naming <c>pul</c> would not be caught here. Widening the shape to bare words is NOT the fix:
    /// every backticked ordinary noun in these prompts (<c>capture</c>, <c>match</c>, <c>errors</c>,
    /// <c>path</c>) would then have to be a known vocabulary member, and the union would grow into an
    /// allow-list of English. The gap is covered instead where it is cheap and exact —
    /// <c>HealRunPromptTests.TheReasonKindsTheProcedureLists_AreExactlyTheOnesAHostCanObserve</c>
    /// asserts that vocabulary as a SET against <c>VerdictReasonKinds.All</c>, which catches a
    /// misspelling by absence rather than by shape.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> KnownNonToolVocabulary =
        new(VerdictReasonKinds.All.Concat(SpecEditScopes.All), StringComparer.Ordinal);

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
                // A snake_case token is USUALLY a tool name — but not always, and the difference is
                // resolved against real vocabularies rather than an exclusion list.
                //
                // `capture_unmet` is the case that forced this (a review simulated the regexes against
                // heal_run and found it would fail): it is a VerdictReasonKinds value, snake_case, and
                // not a tool. Adding it to a bare "ignore these" list would have been the wrong fix —
                // the next such token would be invisible again, and an ignore list cannot tell a
                // legitimate vocabulary member from a typo. Checking membership of the UNION of the
                // real vocabularies keeps every token verified against something.
                Assert.True(
                    tools.Contains(token) || KnownNonToolVocabulary.Contains(token),
                    $"The prompt names `{token}`, which is neither a tool this server advertises nor a "
                    + "member of a vocabulary it publishes. "
                    + $"Advertised tools: {string.Join(", ", tools.OrderBy(name => name, StringComparer.Ordinal))}.");
                toolsChecked++;
            }
        }

        // ── PER-KIND FLOORS, EACH CONDITIONAL ON THAT KIND BEING PRESENT ─────────────────────────
        //
        // The floors exist to catch a PATTERN REGRESSION — a markdown restyling that stops a token
        // kind matching, silently reducing this check to a subset of itself. They must not also demand
        // that every prompt name every kind, which is what an unconditional floor does.
        //
        // Measured: written unconditionally for author_scenario (which names three resources), the
        // resource floor then failed `review_spec` and `explain_failure` — two tool-driven procedures
        // that legitimately reference no resource at all. That was the assertion being wrong, not the
        // prompts.
        //
        // So each floor asks: does the raw text contain anything of this kind? If yes, the matcher
        // must have examined at least one. If no, there is nothing to examine and nothing to prove.
        if (ContainsToolShapedToken(rendered))
        {
            Assert.True(
                toolsChecked > 0,
                "The rendered prompt contains a snake_case backticked token but the cross-check "
                + "verified no TOOL name — the token pattern has stopped matching.");
        }

        if (ContainsResourceUri(rendered))
        {
            Assert.True(
                resourcesChecked > 0,
                "The rendered prompt names a vouchfx resource URI but the cross-check verified none — "
                + "the URI pattern has stopped matching.");
        }

        if (ContainsRuledArgumentPair(rendered))
        {
            Assert.True(
                valuesChecked > 0,
                "The rendered prompt spells out a `key: value` pair this check has a rule for, but no "
                + "ARGUMENT VALUE was verified. This is the half that exists because two argument-value "
                + "defects shipped past an identifier-presence check, so it failing to fire is the "
                + "specific regression this assertion guards.");
        }

        // And SOMETHING must have been checked, whatever the mix — a prompt whose every backticked
        // token went unexamined means the tokeniser itself stopped working.
        Assert.True(
            toolsChecked + resourcesChecked + valuesChecked > 0,
            "The cross-check examined nothing at all — either the rendered prompt has no backticked "
            + "identifiers, or the token patterns have stopped matching the prompts' markdown style.");
    }

    /// <summary>Whether the text carries a backticked snake_case token at all.</summary>
    private static bool ContainsToolShapedToken(string rendered) =>
        BacktickedToken.Matches(rendered).Any(match => ToolNameShape.IsMatch(match.Groups["token"].Value.Trim()));

    /// <summary>Whether the text names a vouchfx resource URI at all.</summary>
    private static bool ContainsResourceUri(string rendered) =>
        BacktickedToken.Matches(rendered).Any(match => IsResourceUri(match.Groups["token"].Value.Trim()));

    /// <summary>
    /// Whether the text spells out a <c>key: value</c> pair this check actually has a rule for.
    /// </summary>
    /// <remarks>
    /// Ruled pairs specifically, not any pair: <c>explain_failure</c> writes <c>runId: run-42</c>,
    /// which matches the pair shape and has no constrained vocabulary to check it against — demanding
    /// a rule for it would be demanding a rule for every identifier a prompt ever echoes.
    /// </remarks>
    private static bool ContainsRuledArgumentPair(string rendered) =>
        BacktickedToken.Matches(rendered).Any(match =>
            ArgumentPair.Match(match.Groups["token"].Value.Trim()) is { Success: true } pair
            && ArgumentValueRules.ContainsKey(pair.Groups["key"].Value));

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
