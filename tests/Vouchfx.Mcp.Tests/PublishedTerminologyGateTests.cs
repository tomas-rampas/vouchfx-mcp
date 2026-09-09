using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// No page this repository PUBLISHES, and no string it SHIPS, carries internal planning vocabulary —
/// sprint numbers, story ids, or citations into maintainer-local documents a public reader cannot
/// open.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a test and not a CI step.</b> The fleet's cross-site sentinel
/// (<c>scripts/check_docs_drift.py</c> in the engine repository) crawls the PUBLISHED SITE, so it
/// reports a leak only after a deploy, and it uses <c>pattern.search</c> rather than
/// <c>finditer</c> — one hit per pattern per page, which understates a class of leak as a single
/// line. A sweep prompted by one such report found ten occurrences in three shapes, two of which no
/// sentinel regex matched at all. This moves the check to where the text is written, and to the same
/// place every other content-parity guard in this repository lives
/// (<c>ErrorCatalogueFilesystemParityTests</c>, <c>ResourceDocumentationParityTests</c>).
/// </para>
/// <para>
/// <b>Scope is derived, not listed.</b> The published set is <c>docs/**/*.md</c> plus
/// <c>README.md</c> — what the shared site generator renders. Because that claim is the whole
/// foundation of this test, <see cref="TheGeneratorsPublicationScope_IsStillWhatThisGateAssumes"/>
/// pins the generator's own SKIP configuration: if a future edit starts excluding part of
/// <c>docs/</c>, this gate would silently keep scanning files nobody publishes (harmless) or, worse,
/// stop covering files that are (not harmless), and the assumption fails loudly instead.
/// </para>
/// <para>
/// <b>Shipped strings are scanned too.</b> A tool description, a prompt body or an error message
/// reaches a model at runtime and no crawler of any kind can see it; six such strings carried the
/// same citations. Descriptions are read from the LIVE server rather than from source, so the test
/// sees exactly what a host sees.
/// </para>
/// <para>
/// <b>One shipped surface is deliberately OUT of scope: <c>vendored/</c>.</b> The class summary says
/// "no string it ships", and that is true of everything this repository WRITES — the two vendored
/// engine documents are served verbatim and do contain §-citations (ten today), but they are
/// byte-exact copies of the pinned engine commit held under a SHA-256 drift gate, their citations
/// point into the engine's own PUBLISHED documentation rather than a maintainer-local file, and
/// editing them to satisfy this gate would break the thing that makes them trustworthy. They are
/// excluded because they are not ours to reword, not because they were overlooked.
/// </para>
/// </remarks>
public class PublishedTerminologyGateTests
{
    /// <summary>
    /// The fleet sentinel's own eight patterns, transcribed from
    /// <c>vouchfx/scripts/check_docs_drift.py</c>'s <c>FORBIDDEN_PATTERNS</c> so the two surfaces stay
    /// comparable — if that list grows, this one should be re-synced from it.
    /// </summary>
    /// <remarks>
    /// Plus three this repository adds, each covering a real shape the sentinel structurally cannot
    /// see:
    /// <list type="bullet">
    /// <item><description>
    /// <c>§\s*\d</c> — a section citation into a maintainer-local spec. <c>\s*</c> rather than a
    /// literal space because the published HTML wraps, and "spec /\n§4.5" is the same leak split
    /// across two lines.
    /// </description></item>
    /// <item><description>
    /// <c>US-S\d+-\d+</c> — this repository's story ids, which do not match the engine's
    /// <c>S\d{2}-[A-Z]-\d{2}</c> shape at all.
    /// </description></item>
    /// <item><description>
    /// Bare, case-insensitive <c>sprint</c> — the sentinel requires a number after it, so "this
    /// sprint" and "the sprint's exit checklist" walked straight past. A temporal claim with no
    /// number is still a temporal claim that expires.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static readonly (string Name, Regex Pattern)[] ForbiddenPatterns =
    [
        // ── The engine sentinel's eight ────────────────────────────────────────────────────────
        ("engine-story-id", new Regex(@"S\d{2}-[A-Z]-\d{2}", RegexOptions.Compiled)),
        ("engine-story-id-alt", new Regex(@"\bS\d{2}-[A-Z]\d{2}\b", RegexOptions.Compiled)),
        ("sprint-numbered", new Regex(@"[Ss]print[- ]\d+", RegexOptions.Compiled)),
        ("mvp-folder", new Regex(@"03_MVP", RegexOptions.Compiled)),
        ("reviews-folder", new Regex(@"docs/reviews", RegexOptions.Compiled)),
        ("human-todo", new Regex(@"HUMAN_TODO", RegexOptions.Compiled)),
        ("engine-issue-ref", new Regex(@"vouchfx#\d+", RegexOptions.Compiled)),
        ("phase-of-issue", new Regex(@"Phase [A-Z0-9] of #\d+", RegexOptions.Compiled)),

        // ── The three it cannot see ────────────────────────────────────────────────────────────
        ("spec-section-citation", new Regex(@"§\s*\d", RegexOptions.Compiled)),
        ("repo-story-id", new Regex(@"US-S\d+-\d+", RegexOptions.Compiled)),
        ("sprint-bare", new Regex(@"sprint", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>
    /// Every deliberate exception, keyed by the MATCHED TEXT and the file — never by line number,
    /// which every edit above it invalidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each entry must still match something</b>
    /// (<see cref="EveryAllowedException_StillMatchesSomething"/>). An exception that has outlived
    /// the text it excused is the same rot as the leak itself: it accumulates silently, and the next
    /// reader treats a stale list as a considered one.
    /// </para>
    /// <para>
    /// <b>ONE entry, not the three a review anticipated</b> — reported rather than padded to match.
    /// The other two candidates need no exception because no pattern above matches them: <c>EDGE-002</c>
    /// (in <c>docs/validation/</c>) and the upstream-ask ids <c>U1</c>/<c>U4</c>/<c>U5</c> are neither
    /// section citations nor story ids in any of the eleven shapes. Adding them would have created
    /// exactly the dead entries the freshness check exists to prevent, on day one. Both remain fine to
    /// publish on their own merits — <c>EDGE-002</c> is explained where it appears, and the U-ids are
    /// defined in the pages that use them.
    /// </para>
    /// <para>
    /// Matching is by a CONTEXT WINDOW around the hit, not by the matched token: <c>§\s*\d</c> matches
    /// only "§1" out of "§17", so a token-equality allowlist could never have expressed this entry.
    /// </para>
    /// </remarks>
    private static readonly (string File, string Text, string Reason)[] Allowed =
    [
        (
            "README.md",
            "§17",
            "Cites §17 of the PUBLISHED engine blueprint, which a reader can open — not a "
            + "maintainer-local document."),
    ];

    /// <summary>Characters of context taken either side of a hit when testing the allowlist.</summary>
    private const int AllowContextChars = 12;

    private static IEnumerable<string> PublishedFiles()
    {
        var root = SourceGuardScan.RepoRoot.FullName;

        yield return "README.md";

        // The landing page: genuinely published (copied verbatim into the site output), and OUTSIDE
        // the docs/**/*.md derivation, so nothing else here would ever look at it.
        yield return "site/index.html";

        // SKILL.md: a SHIPPED artefact at the repository root that a Claude Code session reads before
        // it reads anything else. Not under docs/**, and not reachable through any live MCP leg
        // either — it is discovered from the filesystem, so this is the only place it gets swept.
        yield return "SKILL.md";

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
    }

    private static string ReadPublished(string relativePath) =>
        File.ReadAllText(Path.Combine(SourceGuardScan.RepoRoot.FullName, relativePath));

    /// <summary>
    /// Whether this specific hit is the one an allowlist entry excuses — right file, right place, and
    /// right MATCHED VALUE.
    /// </summary>
    /// <remarks>
    /// <b>The third condition is the one a gatekeeper review added, and it is load-bearing.</b> The
    /// window alone excuses everything NEAR the allowed text: a genuine leak landing within twelve
    /// characters of README's "§17" — "…§17; sprint 6 adds…" — would have been silently suppressed,
    /// because the window contains "§17" and nothing tied the entry to what actually matched. The
    /// containment test binds them: the entry's text must CONTAIN the matched value, so "§17" excuses
    /// the "§1" that <c>§\s*\d</c> reports out of it and refuses an adjacent "sprint" outright. Both
    /// conditions are needed — containment alone would excuse "§1" anywhere in the file, the window
    /// alone excuses anything nearby.
    /// </remarks>
    private static bool IsAllowed(string file, string text, Match match)
    {
        var start = Math.Max(0, match.Index - AllowContextChars);
        var end = Math.Min(text.Length, match.Index + match.Length + AllowContextChars);
        var window = text[start..end];

        return Allowed.Any(entry =>
            string.Equals(entry.File, file, StringComparison.Ordinal)
            && window.Contains(entry.Text, StringComparison.Ordinal)
            && entry.Text.Contains(match.Value, StringComparison.Ordinal));
    }

    // ── The scope assumption itself ────────────────────────────────────────────────────────────

    [Fact]
    public void TheGeneratorsPublicationScope_IsStillWhatThisGateAssumes()
    {
        var generator = ReadPublished("scripts/build_site.py");

        // build() auto-renders docs/**/*.md minus SKIP/SKIP_PREFIXES. An empty SKIP and a specs/-only
        // SKIP_PREFIXES is what makes "every docs/*.md is published" true — and therefore what makes
        // this test's file set the right one.
        Assert.Contains("SKIP: set[str] = set()", generator, StringComparison.Ordinal);
        Assert.Contains(
            "SKIP_PREFIXES: tuple[str, ...] = (\"specs/\",)", generator, StringComparison.Ordinal);
    }

    // ── The published surface ──────────────────────────────────────────────────────────────────

    [Fact]
    public void NoPublishedPage_CarriesInternalPlanningVocabulary()
    {
        var files = PublishedFiles().ToArray();

        // Anti-vacuity: an enumeration that matched nothing would pass this test perfectly. The floor
        // is proportionate to the real count (57 today) rather than a token 10 — a floor an order of
        // magnitude under the truth would let most of the surface stop being scanned and still pass.
        Assert.True(files.Length >= 40, $"Only {files.Length} published files found — scope is wrong.");

        var leaks = new List<string>();

        foreach (var file in files)
        {
            var text = ReadPublished(file);

            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                // finditer, not search — the fleet sentinel's one-hit-per-pattern reporting is what
                // made a ten-occurrence class look like a single line.
                foreach (Match match in pattern.Matches(text))
                {
                    if (!IsAllowed(file, text, match))
                    {
                        leaks.Add($"{file}: [{name}] {match.Value.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            leaks.Count == 0,
            $"{leaks.Count} internal-planning leak(s) on the published surface:\n  "
            + string.Join("\n  ", leaks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            + "\n\nDelete the planning framing and state the fact in present tense — a reader cannot "
            + "open a sprint plan, a story id, or a maintainer-local spec section.");
    }

    // ── The shipped strings ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToolDescription_CarriesInternalPlanningVocabulary()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);

        // Proportionate anti-vacuity floor, like every other leg here: eighteen tools are advertised
        // today, and Assert.NotEmpty would have let seventeen of them stop being scanned.
        Assert.True(tools.Count >= 18, $"Only {tools.Count} tools advertised — scope is wrong.");

        var leaks = new List<string>();

        foreach (var tool in tools)
        {
            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                foreach (Match match in pattern.Matches(tool.Description ?? string.Empty))
                {
                    leaks.Add($"tool {tool.Name}: [{name}] {match.Value.Trim()}");
                }
            }
        }

        Assert.True(
            leaks.Count == 0,
            $"{leaks.Count} internal-planning leak(s) in SHIPPED tool descriptions — text a model "
            + $"reads at runtime, which no site crawler can ever see:\n  {string.Join("\n  ", leaks)}");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Prompt and resource descriptions carry none either — the other two runtime surfaces a model
    /// reads and no crawler can see.
    /// </summary>
    /// <remarks>
    /// Added with the tool-description leg's own rationale applied consistently (a gatekeeper
    /// review's finding): a prompt's description is what a host shows a user choosing between
    /// procedures, and a resource's is what a model reads when deciding whether to spend a read. Both
    /// ship inside the assembly; neither appears on any page. Read from the LIVE server for the same
    /// reason the tool leg is — the test then sees exactly the bytes a host receives.
    /// </remarks>
    [Fact]
    public async Task NoPromptOrResourceDescription_CarriesInternalPlanningVocabulary()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var described = new List<(string Owner, string Text)>();

        foreach (var prompt in await harness.Client.ListPromptsAsync(cancellationToken: cts.Token))
        {
            described.Add(($"prompt {prompt.Name}", prompt.Description ?? string.Empty));
        }

        foreach (var resource in await harness.Client.ListResourcesAsync(cancellationToken: cts.Token))
        {
            described.Add(($"resource {resource.Uri}", resource.Description ?? string.Empty));
        }

        foreach (var template in await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token))
        {
            described.Add((
                $"template {template.ProtocolResourceTemplate.UriTemplate}",
                template.ProtocolResourceTemplate.Description ?? string.Empty));
        }

        // Anti-vacuity: four prompts, four resources and seven templates are advertised today.
        Assert.True(described.Count >= 15, $"Only {described.Count} described surfaces found.");

        var leaks = (from surface in described
                     from pattern in ForbiddenPatterns
                     from Match match in pattern.Pattern.Matches(surface.Text)
                     select $"{surface.Owner}: [{pattern.Name}] {match.Value.Trim()}").ToArray();

        Assert.True(
            leaks.Length == 0,
            $"{leaks.Length} internal-planning leak(s) in SHIPPED prompt/resource descriptions:\n  "
            + string.Join("\n  ", leaks));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The prompt BODIES and the embedded example suites carry none either.
    /// </summary>
    /// <remarks>
    /// <b>The largest model-facing text this server ships, and it was the last unswept surface</b> (a
    /// peer review's finding). The description legs above cover one line per prompt; a body is the
    /// ~130-line procedure a model actually executes, and it is where planning vocabulary would
    /// naturally accumulate — every one of these was written against a numbered story. The example
    /// suites are the same class: shipped bytes a model reads as a template to copy. Neither appears
    /// on any page, and neither is reachable through <c>prompts/list</c> or <c>resources/list</c>
    /// metadata, so nothing else here would see them.
    /// </remarks>
    [Fact]
    public void NoPromptBodyOrExampleSuite_CarriesInternalPlanningVocabulary()
    {
        var shipped = new List<(string Owner, string Text)>();

        foreach (var prompt in Vouchfx.Mcp.Prompts.PromptRepository.All)
        {
            shipped.Add(($"prompt body {prompt.Name}", prompt.Body));
        }

        foreach (var example in Vouchfx.Mcp.Examples.ExampleSuites.All)
        {
            shipped.Add((
                $"example {example.Name}",
                Vouchfx.Mcp.Examples.ExampleSuiteRepository.GetRawText(example.Name)));
        }

        // Anti-vacuity, proportionate: four prompts and three example suites ship today.
        Assert.True(shipped.Count >= 7, $"Only {shipped.Count} shipped documents found.");
        Assert.All(shipped, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Text)));

        var leaks = (from document in shipped
                     from pattern in ForbiddenPatterns
                     from Match match in pattern.Pattern.Matches(document.Text)
                     select $"{document.Owner}: [{pattern.Name}] {match.Value.Trim()}").ToArray();

        Assert.True(
            leaks.Length == 0,
            $"{leaks.Length} internal-planning leak(s) in SHIPPED prompt bodies / example suites — the "
            + $"text a model executes:\n  {string.Join("\n  ", leaks)}");
    }

    [Fact]
    public void NoCatalogueDescription_CarriesInternalPlanningVocabulary()
    {
        // Proportionate floor: the catalogue carries dozens of codes, and a reflection or filter
        // change that emptied it would otherwise make this leg pass over nothing.
        Assert.True(
            VfxCodeCatalogue.All.Count >= 30,
            $"Only {VfxCodeCatalogue.All.Count} catalogue entries — scope is wrong.");

        var leaks = new List<string>();

        foreach (var entry in VfxCodeCatalogue.All)
        {
            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                foreach (Match match in pattern.Matches(entry.Summary))
                {
                    leaks.Add($"{entry.Code}: [{name}] {match.Value.Trim()}");
                }
            }
        }

        Assert.True(
            leaks.Count == 0,
            $"{leaks.Count} internal-planning leak(s) in SHIPPED diagnostic descriptions:\n  "
            + string.Join("\n  ", leaks));
    }

    // ── The allowlist must not rot ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An allowlist entry excuses its OWN match and nothing else — a real leak sitting beside the
    /// allowed text is still reported.
    /// </summary>
    /// <remarks>
    /// The regression test for the suppression window a gatekeeper review found: excusing by
    /// PROXIMITY alone meant anything within twelve characters of README's "§17" inherited its
    /// exemption. Exercised against <see cref="IsAllowed"/> directly with a synthetic line rather than
    /// by writing to a tracked file — the property under test is the predicate's, and a test that
    /// edited README to prove it would be a test that can leave the repository dirty when it fails.
    /// </remarks>
    [Fact]
    public void AnAllowedException_DoesNotExcuseADifferentLeakBesideIt()
    {
        // The real README shape, with a genuine leak inserted right next to the allowed citation.
        const string line = "the blueprint (`SecretString`, §17): sprint 6 adds the rest.";

        var citation = new Regex(@"§\s*\d").Match(line);
        var sprint = new Regex(@"[Ss]print[- ]\d+").Match(line);

        Assert.True(citation.Success && sprint.Success, "Fixture no longer contains both shapes.");

        // Close enough that the window catches both — which is exactly the condition that used to
        // suppress the second one.
        Assert.True(
            Math.Abs(sprint.Index - citation.Index) <= AllowContextChars * 2,
            "Fixture no longer places the leak inside the allowlist window; it would not test anything.");

        Assert.True(IsAllowed("README.md", line, citation), "The §17 citation is the allowed one.");
        Assert.False(IsAllowed("README.md", line, sprint), "A sprint leak beside §17 must NOT be excused.");
    }

    [Fact]
    public void EveryAllowedException_StillMatchesSomething()
    {
        foreach (var (file, text, reason) in Allowed)
        {
            var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, file);

            Assert.True(File.Exists(path), $"Allowlist entry names '{file}', which does not exist.");
            Assert.True(
                File.ReadAllText(path).Contains(text, StringComparison.Ordinal),
                $"Allowlist entry '{text}' in '{file}' no longer matches anything — the text it "
                + $"excused is gone. Delete the entry. (Its reason was: {reason})");
        }
    }
}
