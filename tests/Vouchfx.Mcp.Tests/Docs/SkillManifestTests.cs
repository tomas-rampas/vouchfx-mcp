using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests.Docs;

/// <summary>
/// US-S5-06: every identifier the repo-root <c>SKILL.md</c> names is one this server really
/// advertises, and the two rules its AC requires verbatim are actually stated.
/// </summary>
/// <remarks>
/// <para>
/// <b>The story's own convention is "no unit-test surface — a manual read-through gate", and that
/// still stands for the PROSE.</b> Whether the routing table sends a reader to the right prompt, and
/// whether the imperative voice works on a model, is a judgement a test cannot make. What a test can
/// make cheaply is the check this sprint kept catching in review: a documentation file making a
/// tool-contract claim that the advertised contract contradicts. Both blocked precedents are
/// US-S5-02's — a <c>format</c> VALUE the schema rejects, and a missing <c>normalize: true</c> that
/// silently returned a null result — argument-value defects invisible to a presence-only test.
/// (US-S5-04's three-argument hand-off looked like a third and was adjudicated CONFORMING; it is not
/// a precedent for anything.) US-S5-06 then produced its own: this file told the model to hand a
/// <c>runId</c> to <c>list_runs</c>, which takes no such argument — see
/// <see cref="EveryToolSkillMdHandsARunIdTo_ReallyDeclaresARunIdInput"/>, written red-first against
/// exactly that sentence.
/// </para>
/// <para>
/// So this class covers the identifier surface AND the contract claims made about it, and
/// deliberately nothing else.
/// </para>
/// <para>
/// Derived from the RUNNING SERVER rather than from a re-typed list, for the reason every parity
/// guard in this repo is: a hand-written expected set is a second copy that goes stale on its own.
/// </para>
/// </remarks>
public class SkillManifestTests
{
    /// <summary>
    /// A backticked token shaped like a tool or prompt name: lower snake_case with at least one
    /// underscore.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. <c>SKILL.md</c> backticks plenty of other things — YAML keys
    /// (<c>capture</c>, <c>timeout</c>), response fields (<c>semanticDiagnostics</c>, <c>runId</c>),
    /// verdict words, URIs and file extensions — and none of those are tool names. The snake_case
    /// shape is what every tool and prompt in this server uses and what nothing else in that file
    /// does, which makes the filter precise without a maintained exclusion list.
    /// </remarks>
    private static readonly Regex IdentifierShapedToken =
        new(@"`(?<name>[a-z][a-z0-9]*(?:_[a-z0-9]+)+)`", RegexOptions.Compiled);

    private static string SkillText()
    {
        var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, "SKILL.md");
        Assert.True(File.Exists(path), $"Expected SKILL.md at the repository root ('{path}').");
        return File.ReadAllText(path);
    }

    [Fact]
    public void SkillMd_HasSkillDiscoveryFrontMatter()
    {
        var text = SkillText();

        // The shape Claude Code's skill discovery expects: YAML front matter carrying a name and the
        // description it matches a task against. MEASURED against an installed skill
        // (agentic-framework's own), not assumed from documentation.
        Assert.StartsWith("---\n", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\nname: ", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\ndescription: ", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        // The description is the trigger. It must name what it triggers ON, or discovery never fires.
        var frontMatter = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n---", 2)[0];
        Assert.Contains(".e2e.yaml", frontMatter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryToolOrPromptSkillMdNames_IsOneTheServerAdvertises()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var real = (await harness.Client.ListToolsAsync(cancellationToken: cts.Token))
            .Select(tool => tool.Name)
            .Concat((await harness.Client.ListPromptsAsync(cancellationToken: cts.Token)).Select(prompt => prompt.Name))
            .ToHashSet(StringComparer.Ordinal);

        var named = IdentifierShapedToken
            .Matches(SkillText())
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Anti-vacuity: SKILL.md's whole purpose is to route to these, so finding none means the
        // backtick convention changed and this check has gone hollow.
        Assert.True(
            named.Length >= 10,
            $"SKILL.md names only {named.Length} tool/prompt-shaped identifiers — expected at least "
            + "ten. Has the backticking convention changed?");

        foreach (var name in named)
        {
            Assert.True(
                real.Contains(name),
                $"SKILL.md names '{name}', which this server advertises as neither a tool nor a "
                + "prompt. A skill that sends a session to a non-existent tool fails at the exact "
                + "moment it is trusted most.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Every tool <c>SKILL.md</c> tells the model to hand a <c>runId</c> to really declares a
    /// <c>runId</c> input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written red-first, and it caught the defect it was written for.</b> The first version of
    /// that sentence listed <c>list_runs</c> among the six — and <c>list_runs</c> takes
    /// <c>limit</c>/<c>cursor</c>/<c>label</c>/<c>since</c>, existing precisely for the case where you
    /// no longer HAVE a run id. Five of six passed; the sixth failed on measurement. Both reviewers
    /// found it by reading, which is the point: the previous checks in this class verified that every
    /// named identifier EXISTS, and existence was never the claim at risk. This one verifies what the
    /// sentence actually asserts about the tool's contract.
    /// </para>
    /// <para>
    /// Reads the ADVERTISED input schema (<c>tool.ProtocolTool.InputSchema</c>) rather than a
    /// hand-written list of run-scoped tools, so a tool that later drops or renames its <c>runId</c>
    /// parameter fails here instead of leaving the skill instructing a call that cannot be made.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryToolSkillMdHandsARunIdTo_ReallyDeclaresARunIdInput()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var schemasByTool = (await harness.Client.ListToolsAsync(cancellationToken: cts.Token))
            .ToDictionary(tool => tool.Name, tool => tool.ProtocolTool.InputSchema, StringComparer.Ordinal);

        var named = RunIdRecipients(SkillText());

        // Anti-vacuity: the sentence exists to route a run id, so an empty extraction means the
        // sentence was reworded past this pattern, not that the claim went away.
        Assert.True(
            named.Length >= 4,
            $"Extracted only {named.Length} run-id recipients from SKILL.md — expected at least four. "
            + "Has the 'Pass that id to …' sentence been reworded?");

        foreach (var tool in named)
        {
            Assert.True(
                schemasByTool.TryGetValue(tool, out var schema),
                $"SKILL.md hands a runId to '{tool}', which is not an advertised tool.");

            Assert.True(
                schema.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("runId", out _),
                $"SKILL.md tells the model to pass a runId to '{tool}', but that tool's advertised "
                + "input schema declares no 'runId' property. Either the prose names the wrong tool "
                + "or the tool's contract changed.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The tools named in <c>SKILL.md</c>'s "Pass that id to …" sentence.
    /// </summary>
    /// <remarks>
    /// Scoped to that ONE sentence rather than the whole file: every other backticked tool name in
    /// <c>SKILL.md</c> is named for some other reason, and a whole-file sweep would demand a
    /// <c>runId</c> from <c>validate_suite</c>. The sentence runs from its opening marker to the first
    /// full stop OR SEMICOLON, which is exactly the span that makes the claim — the fix for
    /// <c>list_runs</c> puts it in a following clause of the same sentence ("…; call
    /// <c>list_runs</c> when you no longer have the id"), and a sentence-wide span would still drag
    /// it in and keep failing on prose that is now correct.
    /// </remarks>
    private static string[] RunIdRecipients(string text)
    {
        const string marker = "Pass that id to";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected SKILL.md's 'Pass that id to …' sentence.");

        var end = text.IndexOfAny(['.', ';'], start);
        var sentence = end < 0 ? text[start..] : text[start..end];

        return [.. IdentifierShapedToken
            .Matches(sentence)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task SkillMd_NamesEveryAdvertisedPrompt()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var text = SkillText();

        // The other direction of the AC's "references the four Sprint 5 prompts by name": a fifth
        // prompt landing without a row in the routing table is a procedure no Claude Code session
        // would ever be pointed at.
        foreach (var prompt in await harness.Client.ListPromptsAsync(cancellationToken: cts.Token))
        {
            Assert.True(
                text.Contains(prompt.Name, StringComparison.Ordinal),
                $"SKILL.md never names the '{prompt.Name}' prompt.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public void SkillMd_NamesNoRetiredIdentifier()
    {
        // The same ban every prompt is held to (sprint exit checklist), applied to the one document a
        // Claude Code session reads before it reads any of them — and through the SAME hardened
        // assertion, which checks the raw text, a formatting-stripped copy and a whitespace-stripped
        // one. A plain DoesNotContain (what this used to do) misses `write\_spec`, `**write**_spec`
        // and a `write_`/`spec` line break, all of which read as one word to a model.
        foreach (var banned in Prompts.PromptTextAssertions.RetiredIdentifiers)
        {
            Prompts.PromptTextAssertions.AssertIdentifierAbsent(banned, SkillText());
        }
    }

    /// <summary>
    /// The availability sets <c>SKILL.md</c> states are internally consistent: all real, pairwise
    /// disjoint, and covering every tool the file recommends for authoring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this deliberately does NOT test, stated plainly:</b> that the tools listed as
    /// "offline" really succeed with no <c>vouchfx</c> on <c>PATH</c>. That claim is only measurable
    /// by spawning a server with a scrubbed <c>PATH</c> and calling each one — a real integration
    /// test, not a cheap guard, and inventing a fake for it would prove nothing about the claim. It
    /// stays a reviewed fact.
    /// </para>
    /// <para>
    /// <b>What it DOES catch is the defect a gatekeeper actually found here:</b> the file's reading
    /// list sends a model to <c>describe_step_type</c> while an earlier sentence claimed authoring
    /// works entirely offline — two true-sounding statements that contradict each other on a CLI-less
    /// host. The COVERAGE half below is what makes that structural: every tool recommended for
    /// authoring must appear in exactly one availability set, so a tool cannot be recommended without
    /// its availability being stated somewhere.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheAvailabilitySetsSkillMdStates_AreRealDisjointAndCoverWhatItRecommends()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var realTools = (await harness.Client.ListToolsAsync(cancellationToken: cts.Token))
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        var text = SkillText();
        var offline = ToolsInBullet(text, "- **Offline**");
        var needsCli = ToolsInBullet(text, "- **Needs the pinned");
        var needsRuntime = ToolsInBullet(text, "- **Needs the CLI and a container runtime**");

        // Anti-vacuity on all three: a reworded section must fail loudly rather than silently pass.
        Assert.True(offline.Length >= 5, $"The offline set names only {offline.Length} tools.");
        Assert.True(needsCli.Length >= 4, $"The CLI set names only {needsCli.Length} tools.");
        Assert.NotEmpty(needsRuntime);

        foreach (var tool in offline.Concat(needsCli).Concat(needsRuntime))
        {
            Assert.True(realTools.Contains(tool), $"SKILL.md's availability sets name '{tool}', which is not a tool.");
        }

        // Disjoint: a tool in two sets tells the model two different things about the same call.
        Assert.Empty(offline.Intersect(needsCli, StringComparer.Ordinal));
        Assert.Empty(offline.Intersect(needsRuntime, StringComparer.Ordinal));
        Assert.Empty(needsCli.Intersect(needsRuntime, StringComparer.Ordinal));

        // Coverage: everything the reading list recommends has its availability stated.
        var stated = offline.Concat(needsCli).Concat(needsRuntime).ToHashSet(StringComparer.Ordinal);

        foreach (var recommended in ToolsInSection(text, "## Read this before writing any YAML")
                     .Where(realTools.Contains))
        {
            Assert.True(
                stated.Contains(recommended),
                $"SKILL.md recommends '{recommended}' for authoring but never says whether it needs "
                + "the engine. That is how 'authoring works offline' came to sit beside a reading "
                + "list containing two pin-gated tools.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Each availability set holds the RIGHT tools — not merely real, disjoint ones — and the three
    /// together account for every tool the server advertises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The leg the membership/disjointness check left open</b> (a peer review's finding): moving
    /// <c>plan_coverage</c> from the CLI bullet to the offline one passed every assertion beside this
    /// one, and would have told a model on a CLI-less host that a pin-gated tool was available.
    /// Disjointness only proves the sets do not OVERLAP; it says nothing about which set a tool is in.
    /// </para>
    /// <para>
    /// <b>Why a re-typed map rather than a derivation from <c>src/</c>.</b> The obvious derivation —
    /// "does this tool's orchestrator reach <c>CliPinVerifier</c>?" — needs a tool-name → orchestrator
    /// -type mapping, and no such mapping exists in one place: the name lives in the tool's
    /// <c>Create()</c> factory, the dependency is structural, and two tools reach the CLI indirectly
    /// through <c>LiveStepCatalogue</c> rather than directly. Building it would mean hand-writing that
    /// mapping in this test and then walking source for a call — the SAME re-typed data, plus a
    /// scanner that can be subtly wrong while looking authoritative. The honest form of a
    /// hand-maintained fact is a hand-maintained list that says so. This one is transcribed from
    /// CLAUDE.md's five dependency classes, folded into the three a skill reader needs:
    /// CLI-free + CLI-optional + events-file readers + the lifecycle tool → offline;
    /// pinned-CLI-backed minus <c>run_suite</c> → needs the CLI; <c>run_suite</c> → needs a runtime
    /// too.
    /// </para>
    /// <para>
    /// What keeps the map from being merely a second copy of the prose is the PARTITION assertion:
    /// the three sets must account for all eighteen advertised tools, so a nineteenth tool cannot be
    /// added anywhere in this server without someone deciding, here and in <c>SKILL.md</c>, what a
    /// model needs installed to call it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EachAvailabilitySetHoldsTheRightTools_AndTheThreeCoverEveryAdvertisedTool()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var advertised = (await harness.Client.ListToolsAsync(cancellationToken: cts.Token))
            .Select(tool => tool.Name)
            .ToArray();

        var text = SkillText();

        AssertSetMatches("Offline", ExpectedOffline, ToolsInBullet(text, "- **Offline**"));
        AssertSetMatches("Needs the pinned CLI", ExpectedNeedsCli, ToolsInBullet(text, "- **Needs the pinned"));
        AssertSetMatches(
            "Needs the CLI and a container runtime",
            ExpectedNeedsRuntime,
            ToolsInBullet(text, "- **Needs the CLI and a container runtime**"));

        // The partition: every advertised tool is classified exactly once.
        var classified = ExpectedOffline.Concat(ExpectedNeedsCli).Concat(ExpectedNeedsRuntime).ToArray();

        Assert.Equal(
            advertised.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            classified.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static void AssertSetMatches(string label, string[] expected, string[] actual) =>
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(name => name, StringComparer.Ordinal).ToArray());

    /// <summary>
    /// CLAUDE.md's CLI-free and CLI-optional classes, plus the events-file readers (which read a
    /// recorded file, never the engine) and <c>cancel_run</c> (which fires a token rather than
    /// invoking anything).
    /// </summary>
    private static readonly string[] ExpectedOffline =
    [
        "validate_suite", "normalize_suite", "get_schema", "search_docs", "explain_diagnostic",
        "explain_run", "diagnose_run", "get_run_events", "get_run_status", "list_runs",
        "get_step_timeline", "get_run_artifacts", "cancel_run",
    ];

    /// <summary>CLAUDE.md's pinned-CLI-backed class, less <c>run_suite</c> (which needs more).</summary>
    private static readonly string[] ExpectedNeedsCli =
    [
        "list_step_types", "describe_step_type", "plan_coverage", "scaffold_suite",
    ];

    /// <summary>The one tool that additionally needs a container runtime.</summary>
    private static readonly string[] ExpectedNeedsRuntime = ["run_suite"];

    /// <summary>The identifier-shaped tokens in the single bullet starting with <paramref name="marker"/>.</summary>
    private static string[] ToolsInBullet(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a '{marker}…' bullet in SKILL.md.");

        // Bounded by the next bullet OR the blank line that ends the list, whichever comes first —
        // the LAST bullet has no successor, and without the blank-line bound it would swallow the
        // paragraph after the list and inherit every tool that paragraph mentions in passing.
        var candidates = new[]
        {
            text.IndexOf("\n- ", start + marker.Length, StringComparison.Ordinal),
            text.IndexOf("\n\n", start + marker.Length, StringComparison.Ordinal),
        }.Where(index => index >= 0).ToArray();

        var end = candidates.Length == 0 ? -1 : candidates.Min();
        var bullet = end < 0 ? text[start..] : text[start..end];

        return [.. IdentifierShapedToken
            .Matches(bullet)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The identifier-shaped tokens between <paramref name="heading"/> and the next one.</summary>
    private static string[] ToolsInSection(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a '{heading}' heading in SKILL.md.");

        var end = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        var section = end < 0 ? text[start..] : text[start..end];

        return [.. IdentifierShapedToken
            .Matches(section)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)];
    }

    [Fact]
    public void SkillMd_StatesTheTaxonomyRuleAndTheReadOnlyRule()
    {
        // The AC names these two explicitly, and they are the two a model is most likely to violate
        // by being helpful. Wrap-tolerant, for the reason PromptTextAssertions records: these bodies
        // are hand-wrapped markdown and a phrase long enough to matter falls across a line break.
        var text = SkillText();

        // Written WITHOUT backticks: AssertPhrasePresent strips markdown noise from the rendered text
        // but not from the expected phrase, so a backtick here could never match.
        Prompts.PromptTextAssertions.AssertPhrasePresent(
            "Never weaken an assertion to make a Fail pass", text);
        Prompts.PromptTextAssertions.AssertPhrasePresent(
            "This server never writes, modifies or deletes a suite file", text);

        // And the four outcomes in the response-string vocabulary, never the engine's wire tokens —
        // the same rule DslGuideForAgentsTests and ExplainFailurePromptTests hold.
        foreach (var outcome in Enum.GetNames<Vouchfx.Mcp.Run.RunVerdict>())
        {
            Assert.Contains($"`{outcome}`", text, StringComparison.Ordinal);
        }

        foreach (var wireToken in new[] { "PASS", "FAIL", "ENV_ERROR", "INCONCLUSIVE" })
        {
            Assert.DoesNotContain(wireToken, text, StringComparison.Ordinal);
        }
    }
}
