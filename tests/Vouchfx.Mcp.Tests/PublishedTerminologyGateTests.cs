using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
/// <b>What sits outside that derivation is stated as a rule, not counted.</b> This paragraph used
/// to open "Four pages sit outside…" — a count is a completeness claim that nothing checks, and
/// this one was already false when it was written: the packaging project's shipped metadata sat
/// unswept while the sentence read as a full account of what lies outside the derivation. The
/// engine's <c>AsciiRuntimeOutputCensusTests</c> makes the argument in as many words — an
/// enumeration in a comment decays the moment a surface is added, a structural statement cannot be
/// incomplete. <b>The rule <c>PublishedFiles</c> applies:</b> sweep every file this repository
/// WRITES whose prose a reader outside it sees. Three shapes clear that bar without being
/// <c>docs/**/*.md</c>, and each is DERIVED rather than named — <c>site/*.html</c>, enumerated off
/// disk because <c>scripts/build_site.py</c> copies <c>site/</c> wholesale (so the 404 page ships
/// with the same prose status as the landing page, and the next page added is swept without an edit
/// here); <c>SKILL.md</c>, which a Claude Code session reads off the filesystem and which no site
/// tooling touches at all; and the packed NuGet readme, taken from the packaging project's own
/// <c>&lt;PackageReadmeFile&gt;</c> and pinned by
/// <see cref="ThePackedNuGetReadme_IsStillTheOneNuGetRenders"/> for the same reason the generator's
/// SKIP configuration is. That last is the only entry under <c>src/</c> and it is a PAGE rather
/// than source: the literal census below reads <c>.cs</c> only and would never have seen it, and no
/// site tooling reaches it because it is not part of the site at all.
/// </para>
/// <para>
/// <b>The nupkg ships more prose than that readme, and the same rule takes it in.</b>
/// <c>&lt;Description&gt;</c>, <c>&lt;PackageTags&gt;</c> and <c>&lt;PackageReleaseNotes&gt;</c> go
/// into the <c>.nuspec</c> and render on nuget.org BESIDE the readme this gate goes to the trouble
/// of deriving. <see cref="NoPackagedProjectMetadata_CarriesInternalPlanningVocabulary"/> sweeps
/// them through the same <see cref="XDocument"/> parse that already yields the readme's path, and
/// deliberately NOT as file text: that project's XML comments carry the forbidden vocabulary, so a
/// text sweep of it reds on day one. Comments are trivia to a parser and content to a regex — the
/// same distinction the literal census at the end of these remarks is built on.
/// </para>
/// <para>
/// <b>Surfaces deliberately left OUT get their reason recorded, never silence.</b> <c>CLAUDE.md</c>
/// is maintainer-facing rather than published and carries this vocabulary throughout by design.
/// <c>scripts/build_site.py</c> IS published in effect — its <c>DOCS</c> descriptions, its page and
/// portal templates, its <c>meta_description_prefix</c> and its <c>llms_summary</c> all become site
/// copy — but the only way to reach just its published strings from a C# test is to strip Python
/// comments with a regex, which is the technique this file refutes further down;
/// <see cref="TheSiteGeneratorsOwnSource_IsOutOfScopeForTheStatedReason"/> holds that exclusion's
/// premise to account instead. <c>vendored/</c> is the last, for the reason at the end of these
/// remarks.
/// </para>
/// <para>
/// <b>Shipped strings are scanned too.</b> A tool description, a prompt body or an error message
/// reaches a model at runtime and no crawler of any kind can see it; six such strings carried the
/// same citations. Descriptions are read from the LIVE server rather than from source, so the test
/// sees exactly what a host sees.
/// </para>
/// <para>
/// <b>And so is every OTHER shipped string, by source census.</b> The five legs above read the
/// surfaces this repository knows how to enumerate — pages, tool metadata, prompt and resource
/// metadata, prompt bodies and example suites, catalogue summaries. Nothing enumerates an arbitrary
/// error message, and that is where the class originally hid: a citation in an exception message
/// reaches a model at run time while appearing on no page at all.
/// <see cref="NoShippedStringLiteral_CarriesInternalPlanningVocabulary"/> closes that by parsing
/// <c>src/**/*.cs</c> with Roslyn and reading every LITERAL token. MEASURED rather than assumed:
/// reintroducing <c>(spec §4.5)</c> into <c>ListRunsOrchestrator</c>'s <c>'limit'</c> message left
/// every one of those five legs green, and only this census reported it.
/// </para>
/// <para>
/// <b><c>tests/</c> is OUT of that census, and it has to be.</b> Test code names the story it was
/// written against, asserts on the very strings this gate forbids, and this file itself carries
/// <c>spec §</c> and <c>sprint</c> in its own fixtures — a census including <c>tests/</c> would fail
/// on its own source. None of it ships: the test assembly is not packable and no model ever reads
/// it. The engine repository's <c>AsciiRuntimeOutputCensusTests</c> excludes test projects from its
/// own source census for the same reason.
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

        // Every hand-written page under site/ — index.html and 404.html today — genuinely
        // published (copied verbatim into the site output) and OUTSIDE the docs/**/*.md derivation,
        // so nothing else here would ever look at them. ENUMERATED OFF DISK rather than listed,
        // because the reason they are in scope is a property of the DIRECTORY and not of those two
        // names: scripts/build_site.py copies site/ WHOLESALE, so every file in it is published.
        // Listing them made the next page added a silent gap; deriving them sweeps it the day it
        // lands. No other derivation reaches them — the docs/**/*.md sweep does not see .html and
        // the literal census reads .cs.
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(root, "site"), "*.html", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        // SKILL.md: a SHIPPED artefact at the repository root that a Claude Code session reads before
        // it reads anything else. Not under docs/**, and not reachable through any live MCP leg
        // either — it is discovered from the filesystem, so this is the only place it gets swept.
        yield return "SKILL.md";

        // The PACKED NUGET README — the one entry here that lives under src/, which is otherwise all
        // C# and is swept by the LITERAL census rather than by this page sweep. It is a page, not
        // source: nuget.org renders it as the body of the package listing, so it is arguably the
        // most public surface this repository ships, and no site crawler reaches it because it is
        // not part of the docs site at all. DERIVED from the packaging project's own
        // <PackageReadmeFile> rather than named here — see ThePackedNuGetReadme_IsStillTheOneNuGetRenders.
        yield return PackedNuGetReadmePath();

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
    }

    private static string ReadPublished(string relativePath) =>
        File.ReadAllText(Path.Combine(SourceGuardScan.RepoRoot.FullName, relativePath));

    /// <summary>The project that packs this repository as the <c>Vouchfx.Mcp</c> dotnet tool.</summary>
    private const string PackagingProject = "src/Vouchfx.Mcp/Vouchfx.Mcp.csproj";

    /// <summary>The packaging project, parsed — the one parse every question below is answered from.</summary>
    /// <remarks>
    /// <para>
    /// <b>Parsed as XML, not matched as text, and that is the difference between a live derivation
    /// and a decorative one.</b> A regex reads a COMMENTED-OUT declaration as a live one — MEASURED,
    /// on the predecessor of <see cref="DeclaredPackageReadme"/>: comment the packaging block out and
    /// the text matches still fire from inside the <c>&lt;!-- --&gt;</c>, so both of
    /// <see cref="ThePackedNuGetReadme_IsStillTheOneNuGetRenders"/>'s assertions pass while the
    /// package ships no readme and <c>PublishedFiles</c> goes on sweeping a page nobody can read —
    /// verbatim the dead scope this derivation exists to prevent. <see cref="XDocument"/> drops
    /// comments for free, and it is attribute-ORDER-blind as well: <c>&lt;None Pack="true"
    /// Include="…" /&gt;</c> is valid MSBuild that the ordered regex reddened, also measured. No new
    /// dependency — <c>System.Xml.Linq</c> is in the framework. The same parse is what
    /// <see cref="NoPackagedProjectMetadata_CarriesInternalPlanningVocabulary"/> sweeps, for that
    /// reason at larger scale: this file's XML comments carry the vocabulary that gate forbids.
    /// </para>
    /// <para>
    /// <b>Threaded, not repeated.</b> The predecessor re-parsed the file for every question asked of
    /// it, which cost <see cref="ThePackedNuGetReadme_IsStillTheOneNuGetRenders"/> three parses of
    /// the same bytes. Cheap either way; the reason to fix it is that three parses read three
    /// possible states of one file, and a derivation answering "declared" and "packed" from
    /// different reads is one that cannot report a contradiction between them.
    /// </para>
    /// </remarks>
    private static XDocument PackagingProjectXml() =>
        XDocument.Parse(ReadPublished(PackagingProject));

    /// <summary>
    /// The packaging project's <c>&lt;PackageReadmeFile&gt;</c> element, or <c>null</c> if it
    /// declares none.
    /// </summary>
    private static XElement? DeclaredPackageReadmeElement(XDocument project) =>
        project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReadmeFile")
            .FirstOrDefault(element => !string.IsNullOrEmpty(element.Value.Trim()));

    /// <summary>That element's value, trimmed, or <c>null</c> when the project declares none.</summary>
    private static string? DeclaredPackageReadme(XDocument project) =>
        DeclaredPackageReadmeElement(project)?.Value.Trim();

    /// <summary>
    /// The packaging project's <c>&lt;None … Pack="true"&gt;</c> item for <paramref name="file"/>,
    /// or <c>null</c> when nothing packs it.
    /// </summary>
    private static XElement? PackItemFor(XDocument project, string file) =>
        project.Descendants()
            .Where(element => element.Name.LocalName == "None")
            .FirstOrDefault(element =>
                string.Equals(
                    (element.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/'),
                    file.Replace('\\', '/'),
                    StringComparison.Ordinal)
                && string.Equals(element.Attribute("Pack")?.Value, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>Condition</c> governing <paramref name="element"/> — its own, or the nearest
    /// ancestor's — or <c>null</c> when nothing on that path is conditional.
    /// </summary>
    /// <remarks>
    /// <b>Live XML that MSBuild may never evaluate is not a fact, and reading it as one is the same
    /// class of error as reading a commented-out block as a live one.</b>
    /// <c>&lt;None Include="PACKAGE_README.md" Pack="true" Condition="'$(X)'=='y'" /&gt;</c> parses
    /// exactly like an unconditional item, so the parse alone concludes "packed" for a package that
    /// may ship no readme at all — the same dead scope, arrived at from the other direction.
    /// ANCESTORS are walked and not just the element itself: a <c>Condition</c> on the enclosing
    /// <c>&lt;ItemGroup&gt;</c> or <c>&lt;PropertyGroup&gt;</c> is the commoner MSBuild shape and
    /// suppresses the child just as completely. Evaluating the condition is out of scope — a test
    /// has no MSBuild property state to evaluate it against — so the derivation reports that it
    /// CANNOT decide rather than guessing, which is the honest failure and the loud one.
    /// </remarks>
    private static string? ConditionGoverning(XElement element) =>
        element.AncestorsAndSelf()
            .Select(node => node.Attribute("Condition")?.Value)
            .FirstOrDefault(condition => !string.IsNullOrWhiteSpace(condition));

    /// <summary>
    /// The packed NuGet readme, repo-relative — <b>derived from the packaging project, never written
    /// down here</b>.
    /// </summary>
    /// <remarks>
    /// The same discipline as <see cref="TheGeneratorsPublicationScope_IsStillWhatThisGateAssumes"/>,
    /// but the benefit is narrower than "a rename cannot break it" and worth stating exactly, because
    /// the overstatement is what would let the next reader drop it. A rename that MOVES the file also
    /// removes the old path from disk, and a hardcoded path then throws
    /// <see cref="FileNotFoundException"/> — loud, not silent, which is precisely what
    /// <c>LandingPageToolParityTests</c> relies on where it hardcodes this same path. What the
    /// derivation buys is the case disk cannot report: the csproj repointed at a NEW readme while the
    /// old file stays behind, where a hardcoded path keeps passing over a page the package no longer
    /// ships. On top of that it is the ergonomic win — one edit to the csproj moves the gate with it,
    /// instead of a second edit here that a rename has no way to demand. The file sits beside its
    /// <c>.csproj</c> because <c>&lt;None Include&gt;</c> paths are project-relative.
    /// </remarks>
    private static string PackedNuGetReadmePath()
    {
        var declared = DeclaredPackageReadme(PackagingProjectXml());

        Assert.True(
            declared is not null,
            $"'{PackagingProject}' no longer declares a <PackageReadmeFile>. This gate derives the "
            + "packed readme from that element; decide whether the package still ships a readme and "
            + "update this derivation, rather than leaving the sweep pointed at nothing.");

        var projectDirectory = PackagingProject[..PackagingProject.LastIndexOf('/')];

        return $"{projectDirectory}/{declared}";
    }

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

    /// <summary>
    /// The file this gate sweeps as the NuGet package page is still the one NuGet would render — it
    /// is declared, it is packed, and it exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared AND packed are two different facts, and both are asserted.</b>
    /// <c>&lt;PackageReadmeFile&gt;</c> only NAMES a file; it is the <c>&lt;None … Pack="true"&gt;</c>
    /// item that puts it in the nupkg. Either one alone is a broken package — and for this gate's
    /// purposes, a readme that is scanned but not shipped is a file nobody reads, which is the same
    /// dead scope the generator pin above exists to prevent.
    /// </para>
    /// <para>
    /// <b>A third fact: both must be UNCONDITIONAL</b> (<see cref="ConditionGoverning"/>). A
    /// <c>Condition</c> on either — or on the group containing either — leaves live XML that MSBuild
    /// may never evaluate, and a parse that reads it as a fact concludes "declared and packed" for a
    /// package that ships neither. That is the commented-out-block failure in a new costume, so it
    /// fails the same way: loudly, saying the derivation cannot evaluate it, rather than guessing a
    /// configuration.
    /// </para>
    /// <para>
    /// This is the pin for a page that reaches a WIDER audience than the docs site: nuget.org is
    /// where someone evaluating the package reads first, and it is not part of the site build at
    /// all. Nothing in THIS repository would catch a leak there — it has no publication gate of any
    /// kind (<c>scripts/</c> holds <c>build_site.py</c> and <c>sync-vendored.ps1</c>, and
    /// <c>pages.yml</c> runs only the first; <c>check_site.py</c> is the ENGINE's MkDocs gate, on
    /// the engine's own tree) — and the fleet's cross-site sentinel crawls the deployed site, which
    /// this page is not on. That total absence is the argument FOR the pin, not a qualification of
    /// it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePackedNuGetReadme_IsStillTheOneNuGetRenders()
    {
        var project = PackagingProjectXml();
        var declaration = DeclaredPackageReadmeElement(project);

        Assert.True(declaration is not null, $"'{PackagingProject}' declares no <PackageReadmeFile>.");

        var file = declaration!.Value.Trim();

        AssertUnconditional(declaration, $"the <PackageReadmeFile> naming '{file}'");

        var packItem = PackItemFor(project, file);

        Assert.True(
            packItem is not null,
            $"'{PackagingProject}' names '{file}' as its <PackageReadmeFile>, but no "
            + $"<None Include=\"{file}\" Pack=\"true\" /> item puts it in the nupkg. NuGet fails the "
            + "pack in that state, so this gate would be sweeping a page nobody can read.");

        AssertUnconditional(packItem!, $"the <None Include=\"{file}\" Pack=\"true\" /> item");

        var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, PackedNuGetReadmePath());

        Assert.True(
            File.Exists(path),
            $"'{PackagingProject}' declares and packs '{file}', but '{path}' does not exist — "
            + "`dotnet pack` would fail, and this gate is sweeping a file that is not there.");
    }

    /// <summary>
    /// Fails when <paramref name="element"/>, or any ancestor of it, carries a <c>Condition</c> —
    /// the state in which this gate's XML derivation can no longer report a fact.
    /// </summary>
    private static void AssertUnconditional(XElement element, string description)
    {
        var condition = ConditionGoverning(element);

        if (condition is not null)
        {
            Assert.Fail(
                $"In '{PackagingProject}', {description} is governed by Condition=\"{condition}\". "
                + "This gate reads the project as XML and cannot evaluate an MSBuild condition, so "
                + "it can no longer tell whether the package actually ships that readme — and a page "
                + "that is scanned but not shipped is the dead scope this pin exists to prevent. "
                + "Make the declaration unconditional, or decide which configuration this gate "
                + "should assume and encode that decision here.");
        }
    }

    // ── The shipped package metadata ───────────────────────────────────────────────────

    /// <summary>
    /// The prose the nupkg ships BESIDE its readme carries no internal planning vocabulary either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sharper half of a surface this gate covered by halves.</b> <c>&lt;Description&gt;</c>,
    /// <c>&lt;PackageTags&gt;</c> and <c>&lt;PackageReleaseNotes&gt;</c> go into the <c>.nuspec</c>
    /// and render on nuget.org on the SAME PAGE as <c>PACKAGE_README.md</c> — which the sweep above
    /// derives its path from, out of this same file, two lines from the <c>&lt;Description&gt;</c>.
    /// </para>
    /// <para>
    /// <b>Through the parse, never as file text, and that is not a stylistic preference.</b> The
    /// packaging project's XML comments carry the forbidden vocabulary — a maintainer's notes about
    /// why each block exists, exactly the material this gate wants them free to write — so a
    /// whole-file sweep of it reds immediately and would be allowlisted or deleted within the hour.
    /// <see cref="XDocument"/> drops comments for free, which is why
    /// <see cref="PackagingProjectXml"/> exists at all. The first assertion below keeps that
    /// justification honest by failing if those comments ever STOP carrying it: a reason nothing
    /// checks reads as considered long after it has stopped being true, which is the rot
    /// <see cref="EveryAllowedException_StillMatchesSomething"/> guards against for the allowlist.
    /// </para>
    /// <para>
    /// <b>Every leaf element value, not the three that render.</b> Naming three would be the
    /// enumeration this file argues against everywhere else — a <c>&lt;Title&gt;</c> or a
    /// <c>&lt;Copyright&gt;</c> added later would ship unswept, which is precisely how the
    /// <c>PublishedFiles</c> list came to be wrong. The wider read costs nothing: everything else in
    /// this project is a token like <c>net8.0</c> or a path. The three that reach nuget.org are
    /// asserted PRESENT instead, which is the anti-vacuity floor in its most specific available
    /// form — a project that had lost its <c>&lt;Description&gt;</c> would otherwise be swept over
    /// in silence. Attributes are outside the read (<c>Include=</c>, <c>Condition=</c>,
    /// <c>PackagePath=</c>); none of them is prose NuGet renders.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPackagedProjectMetadata_CarriesInternalPlanningVocabulary()
    {
        var text = ReadPublished(PackagingProject);
        var inWholeFile = ForbiddenPatterns.Sum(entry => entry.Pattern.Matches(text).Count);

        Assert.True(
            inWholeFile > 0,
            $"No forbidden vocabulary anywhere in '{PackagingProject}'. The stated reason for "
            + "reading that file through XDocument rather than sweeping its text — its own XML "
            + "comments would red a text sweep — has expired. Re-read it: either the simpler "
            + "whole-file sweep is now available, or this justification needs replacing with the "
            + "real one.");

        var values = PackagingProjectXml()
            .Descendants()
            .Where(element => !element.HasElements)
            .Select(element => (Name: element.Name.LocalName, Value: element.Value.Trim()))
            .Where(entry => entry.Value.Length > 0)
            .ToArray();

        string[] rendered = ["Description", "PackageTags", "PackageReleaseNotes"];

        foreach (var required in rendered)
        {
            Assert.True(
                values.Any(entry => string.Equals(entry.Name, required, StringComparison.Ordinal)),
                $"'{PackagingProject}' no longer declares a non-empty <{required}>. nuget.org "
                + "renders it beside the package readme, so this sweep would be passing over the "
                + "surface it exists for — decide whether the package still ships it rather than "
                + "leaving the floor pointed at nothing.");
        }

        var leaks = (from entry in values
                     from pattern in ForbiddenPatterns
                     from Match match in pattern.Pattern.Matches(entry.Value)
                     select $"<{entry.Name}>: [{pattern.Name}] {match.Value.Trim()}").ToArray();

        Assert.True(
            leaks.Length == 0,
            $"{leaks.Length} internal-planning leak(s) in SHIPPED package metadata — <Description>, "
            + "<PackageTags> and <PackageReleaseNotes> go into the .nuspec and render on nuget.org "
            + $"beside the package readme:\n  {string.Join("\n  ", leaks)}\n\nTHE XML COMMENTS IN "
            + "THAT FILE ARE NOT AFFECTED and must not be changed to satisfy this gate — this sweep "
            + "runs over parsed element values, so nothing listed above is one.");
    }

    // ── The published surface deliberately left out ──────────────────────────────────────

    /// <summary>
    /// <c>scripts/build_site.py</c> is deliberately OUT of the page sweep, and the reason is CHECKED
    /// rather than asserted in prose: its forbidden vocabulary sits only in lexical positions the
    /// generator's own output can never carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The decision.</b> The generator is published in effect — site tooling writes the portal
    /// from its <c>PORTAL</c> template, every page from its <c>PAGE</c> template, each page's meta
    /// description from <c>meta_description_prefix</c>, and <c>llms.txt</c> from
    /// <c>llms_summary</c> plus the <c>DOCS</c> descriptions — and the leak path is one copy-paste
    /// wide, since a story-id comment sits directly above a <c>DOCS</c> tuple whose fourth element
    /// is published copy. <c>LandingPageToolParityTests</c> sweeps this same file for the tool count
    /// for exactly that reason, having MEASURED the failure: six "eleven tools" strings survived
    /// inside the generator after the site itself was swept, so the pages it built went on
    /// advertising the old count.
    /// </para>
    /// <para>
    /// <b>It is out because the only way in is the technique this file refutes.</b> A whole-file
    /// sweep reds on the generator's own commentary. Reaching just its published strings means
    /// stripping Python comments with a regex — which <see cref="PackagingProjectXml"/>'s remarks
    /// record as MEASURED-wrong one file over (a regex reads a commented-out declaration as a live
    /// one), and which
    /// <see cref="TheLiteralCensus_SeesInterpolationAndRawStrings_ButNeitherCommentsNorIdentifiers"/>
    /// argues against at length for C#. There is no Python parser available to a C# test assembly,
    /// and running the generator would put Python on this suite's critical path, which nothing else
    /// here requires. Doing the thing this file has just refuted, in this file, is worse than a
    /// stated gap. <b>The generator's own shape is the second argument.</b> Its two vocabulary sites
    /// are in DIFFERENT lexical categories: a <c>#</c> comment inside <c>DOCS</c>, and the module
    /// DOCSTRING — a string literal that is published nowhere. A rule simple enough to express as a
    /// regex cannot separate that docstring from the published templates, so the literal-sweeping
    /// alternative would not merely repeat the refuted technique; it would repeat it for a case it
    /// gets wrong on the first run.
    /// </para>
    /// <para>
    /// <b>What this test buys is not coverage.</b> The residual gap is real and named above. This is
    /// the freshness check on the EXCLUSION, in the shape
    /// <see cref="EveryAllowedException_StillMatchesSomething"/> uses for the allowlist: the
    /// exclusion's premise is that the vocabulary there lives only in non-published positions, and
    /// this fails the moment that stops being true — which is the moment to re-decide, not one a
    /// green suite should hide. Its own line-shape reading is the same approximation it refuses to
    /// use as a sweep, and that is defensible ONLY because the failure directions invert: as a sweep
    /// an approximation lets a leak through silently; here it can only UNDER-report, which leaves
    /// the file exactly as excluded as it already was. The single shape it would under-report — a
    /// <c>#</c>-initial line inside a triple-quoted template, which it would read as a comment — is
    /// asserted empty below rather than assumed empty.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSiteGeneratorsOwnSource_IsOutOfScopeForTheStatedReason()
    {
        const string generator = "scripts/build_site.py";

        Assert.DoesNotContain(generator, PublishedFiles());

        var lines = ReadPublished(generator).Split('\n');

        // The module docstring: a string literal Python publishes nowhere. Bounded by shape rather
        // than by line number, which every edit above it invalidates - the first line opening with
        // a triple quote, closed by the first later line that is nothing else.
        var docstringStart = Array.FindIndex(
            lines, line => line.StartsWith("\"\"\"", StringComparison.Ordinal));

        Assert.True(docstringStart >= 0, $"'{generator}' has no module docstring; re-read this test.");

        var docstringEnd = Array.FindIndex(
            lines, docstringStart + 1, line => line.TrimEnd() == "\"\"\"");

        Assert.True(docstringEnd > docstringStart, $"'{generator}' has an unclosed module docstring.");

        // Every NAME = <triple quote> ... <triple quote> template block after it. These ARE
        // published, so a hash-initial line inside one is the single shape the comment reading
        // below would misclassify.
        var templateLines = new HashSet<int>();

        for (var i = docstringEnd + 1; i < lines.Length; i++)
        {
            if (!lines[i].Contains(" = \"\"\"", StringComparison.Ordinal))
            {
                continue;
            }

            var close = Array.FindIndex(lines, i + 1, line => line.TrimEnd() == "\"\"\"");

            Assert.True(close > i, $"'{generator}':{i + 1} opens a template block that never closes.");

            for (var line = i; line <= close; line++)
            {
                templateLines.Add(line);
            }

            i = close;
        }

        Assert.True(
            templateLines.Count > 0,
            $"'{generator}' declares no template blocks any more; re-read this test.");

        var misreadable = templateLines
            .Where(line => lines[line].TrimStart().StartsWith('#'))
            .Order()
            .Select(line => $"{generator}:{line + 1}")
            .ToArray();

        Assert.True(
            misreadable.Length == 0,
            "A line inside a PUBLISHED template block begins with '#', which the reading below "
            + "would misclassify as a comment and excuse:\n  "
            + string.Join("\n  ", misreadable)
            + "\n\nThat is the one shape this exclusion's freshness check cannot see, so it must "
            + "not exist: move the line, or sweep the templates directly.");

        var published = new List<string>();
        var unpublished = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var isUnpublished = (i >= docstringStart && i <= docstringEnd)
                || lines[i].TrimStart().StartsWith('#');

            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                foreach (Match match in pattern.Matches(lines[i]))
                {
                    if (isUnpublished)
                    {
                        unpublished++;
                    }
                    else
                    {
                        published.Add($"{generator}:{i + 1}: [{name}] {match.Value.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            unpublished > 0,
            $"'{generator}' no longer carries internal planning vocabulary anywhere. The stated "
            + "reason for excluding it — that its own comments and docstring would red a whole-file "
            + "sweep — has expired, and the simple sweep is now available. Add it to "
            + "PublishedFiles() and delete this test.");

        Assert.True(
            published.Count == 0,
            $"{published.Count} internal-planning hit(s) in '{generator}' outside a comment and "
            + "outside the module docstring — that is, in text which can reach the built site:\n  "
            + string.Join("\n  ", published)
            + "\n\nThis file is EXCLUDED from the page sweep on the premise that its vocabulary "
            + "never leaves those two positions. That premise has just failed, so the exclusion is "
            + "no longer safe: delete the leak, or re-decide the exclusion.");
    }

    // ── The published surface ──────────────────────────────────────────────────────────────────

    [Fact]
    public void NoPublishedPage_CarriesInternalPlanningVocabulary()
    {
        var files = PublishedFiles().ToArray();

        // Anti-vacuity: a derivation that matched nothing would pass this test perfectly. The floor
        // is proportionate to the real count (61 today — 56 docs/**/*.md, README.md, SKILL.md, the
        // packed NuGet readme and the two site/*.html pages) rather than a token 10 — a floor an
        // order of magnitude under the truth would let most of the surface stop being scanned and
        // still pass.
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

    // ── The shipped string literals (source census) ────────────────────────────────────────────

    /// <summary>
    /// Every token kind that carries author-written character data. Comments and XML documentation
    /// are TRIVIA in Roslyn's model and therefore absent from this list by construction rather than
    /// by filtering, and an identifier is a token of a different kind entirely — which is the whole
    /// reason this leg is a parse and not a line regex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transcribed from the engine's <c>AsciiRuntimeOutputCensusTests.s_literalKinds</c>, in its
    /// order, and byte-for-byte the same eight</b> — the same relationship <c>ForbiddenPatterns</c>
    /// documents with the sentinel's pattern list, and recorded for the same reason: if the engine's
    /// list grows a kind, this one should be re-synced from it rather than rediscovered. The two
    /// gates read different things out of the token (this one <c>ValueText</c>, that one
    /// <c>Text</c> — see <see cref="ScanLiteralsForForbiddenVocabulary"/>), but WHICH tokens carry
    /// author-written character data is one question with one answer, and two lexer inventories that
    /// drift apart is one gate silently narrower than the other.
    /// </para>
    /// <para>
    /// <b>What each kind is actually worth here — MEASURED under <c>src/</c>, not assumed.</b> Two
    /// are load-bearing: <c>StringLiteralToken</c>, and <c>InterpolatedStringTextToken</c> without
    /// which the regression this leg exists to catch walks past — <c>ListRunsOrchestrator</c>'s
    /// <c>'limit'</c> message is <c>$"…between 1 and {MaxLimit}…"</c>, whose text segments are not
    /// string literals at all. <c>MultiLineRawStringLiteralToken</c> is exercised: six raw bodies
    /// ship from <c>Diagnosis/</c>. <c>CharacterLiteralToken</c> matches plenty of tokens and can
    /// never report a HIT — a char's <c>ValueText</c> is one character and the shortest forbidden
    /// pattern (<c>§\s*\d</c>) needs two — so it only inflates the literal count the anti-vacuity
    /// floor is read against. The last four, <c>SingleLineRawStringLiteralToken</c> and the three
    /// UTF-8 forms, match no token in this tree at all today. All five are kept for the parity
    /// above and for forward coverage, and
    /// <see cref="TheLiteralCensus_SeesInterpolationAndRawStrings_ButNeitherCommentsNorIdentifiers"/>
    /// exercises those four against a fixture, so "forward coverage" is a demonstrated property
    /// rather than a hope about a lexer nobody has run.
    /// </para>
    /// <para>
    /// <b>The boundary this per-token read leaves open, stated rather than discovered later:</b> a
    /// pattern SPLIT ACROSS two tokens is invisible — <c>"spec " + "§4.5"</c> is caught (the second
    /// segment carries the whole match) but <c>"spec §" + "4.5"</c> and <c>$"spec §{Section}"</c> are
    /// not, because no single token contains the shape. Joining adjacent tokens would fabricate text
    /// no reader ever sees and would report matches straddling an interpolation hole. Accepted rather
    /// than closed: the residual hole needs an author who splits a citation mid-token, and a gate
    /// that reports on text nobody wrote is worse than one with a stated edge. The other stated edge
    /// is <c>DisabledTextTrivia</c>: text inside a FALSE <c>#if</c> branch is trivia, so a citation
    /// parked there would be invisible to this walk. <c>src/</c> contains no <c>#if</c> or
    /// <c>#elif</c> at all, so there is nothing conditional to miss, and the day one appears is the
    /// day to decide whether disabled text is shipped text.
    /// </para>
    /// </remarks>
    private static readonly SyntaxKind[] LiteralKinds =
    [
        SyntaxKind.StringLiteralToken,
        SyntaxKind.Utf8StringLiteralToken,
        SyntaxKind.SingleLineRawStringLiteralToken,
        SyntaxKind.MultiLineRawStringLiteralToken,
        SyntaxKind.Utf8SingleLineRawStringLiteralToken,
        SyntaxKind.Utf8MultiLineRawStringLiteralToken,
        SyntaxKind.InterpolatedStringTextToken,
        SyntaxKind.CharacterLiteralToken,
    ];

    /// <summary>Characters of context quoted either side of a hit in the failure report.</summary>
    private const int ReportContextChars = 24;

    /// <summary>
    /// Every forbidden-vocabulary hit in <paramref name="source"/>'s literal tokens, and how many
    /// literal tokens were read to find them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>ValueText</c>, not <c>Text</c>.</b> The decoded value is what ships — the bytes a model
    /// receives — whereas the source spelling carries the delimiters and the escape sequences.
    /// <b>Decoding CLOSES a hole rather than opening one, and the direction matters because getting
    /// it backwards is an invitation to "fix" this back to <c>Text</c>.</b> A citation written as
    /// <c>"\u00a74.5"</c> emits "§4.5" at run time and is caught here; a source-spelling read
    /// cannot see it, and the engine's <c>AsciiRuntimeOutputCensusTests</c> says so in as many words
    /// where it accepts that hole for its own (different) purpose. A citation typed as a raw <c>§</c>
    /// is present in BOTH readings — it is the case neither approach misses, and mistaking it for
    /// the escaped one is how the cost and the benefit get written down back to front.
    /// </para>
    /// <para>
    /// <b>The genuine cost is the mirror image</b>, and it is the engine's own reason for choosing
    /// <c>Text</c>: <c>ValueText</c> reddens a literal that ESCAPES a forbidden shape deliberately —
    /// the engine has constants naming control characters as <c>'\u0080'</c> precisely because that
    /// file's rule is to write them as escapes, and a gate that fails correct code teaches the next
    /// author to suppress it. There is no such literal in this tree: with <c>ValueText</c> in place
    /// the census reports zero hits across all 170 files, so nothing is being reddened for escaping a
    /// citation on purpose. Should one ever appear, it belongs in <see cref="Allowed"/> with a
    /// reason — not in a retreat to <c>Text</c>, which would silently give back the escaped-citation
    /// case above.
    /// </para>
    /// <para>
    /// <b>The allowlist is <see cref="IsAllowed"/>, unchanged, and reused rather than forked.</b> Its
    /// <c>(File, Text, Reason)</c> shape carries over: the file key is the repo-relative path with
    /// forward slashes (so a future entry would read <c>src/Vouchfx.Mcp/…</c> and
    /// <see cref="EveryAllowedException_StillMatchesSomething"/> resolves it against the repo root
    /// exactly as it does <c>README.md</c>), and the context window is taken within the LITERAL
    /// rather than within the file — strictly tighter, because a literal is the unit a reader judges
    /// an exception in.
    /// </para>
    /// <para>
    /// <b>The one constraint a <c>src/**.cs</c> entry would carry, stated now rather than found
    /// later:</b> <see cref="IsAllowed"/> matches against the DECODED <c>ValueText</c>, while
    /// <see cref="EveryAllowedException_StillMatchesSomething"/> reads the file's raw bytes. For a
    /// page those are the same text; for a <c>.cs</c> file they diverge on any escape or verbatim
    /// <c>""</c> — an entry whose <c>Text</c> is what the literal SAYS would excuse the hit correctly
    /// and then fail the freshness check, which is looking for that text in the SOURCE SPELLING. Such
    /// an entry must therefore be written in a form that appears both ways, or the freshness check
    /// taught to run the census over <c>.cs</c> entries. No entry exists for <c>src/</c> today and
    /// none is needed — the tree is clean — so this is a note on the next one, not a defect in this
    /// one.
    /// </para>
    /// </remarks>
    private static (List<(int Line, string Pattern, string Match, string Context)> Hits, int Literals)
        ScanLiteralsForForbiddenVocabulary(string source, string file)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview), path: file);

        // A file that fails to parse is silently UNDER-read: Roslyn recovers and hands back a
        // partial tree, so this walk reads fewer literals than the file holds and the caller's
        // global literal floor cannot localise the loss to one file. src/ must compile for this
        // assembly to build, so it cannot fire on the real tree today - which is the argument for
        // one line here rather than against it, a check that only matters once something else has
        // broken being exactly the one nobody adds afterwards.
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        if (errors.Length > 0)
        {
            Assert.Fail(
                $"'{file}' did not parse: {errors.Length} error(s), first {errors[0]}. A partial "
                + "tree reads fewer literal tokens than the file holds, so this census would "
                + "under-report without saying so.");
        }

        var hits = new List<(int Line, string Pattern, string Match, string Context)>();
        var literals = 0;

        foreach (var token in tree.GetRoot().DescendantTokens())
        {
            if (Array.IndexOf(LiteralKinds, token.Kind()) < 0)
            {
                continue;
            }

            literals++;
            var value = token.ValueText;

            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                // Matches, not Match: one hit per pattern per literal is how the fleet sentinel
                // understated a ten-occurrence class as a single line.
                foreach (Match match in pattern.Matches(value))
                {
                    if (IsAllowed(file, value, match))
                    {
                        continue;
                    }

                    var line = tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;
                    hits.Add((line, name, match.Value.Trim(), LiteralExcerpt(value, match)));
                }
            }
        }

        return (hits, literals);
    }

    /// <summary>A hit quoted with a little context, newlines made visible so one multi-line raw
    /// string cannot reformat the report around itself.</summary>
    private static string LiteralExcerpt(string value, Match match)
    {
        var start = Math.Max(0, match.Index - ReportContextChars);
        var end = Math.Min(value.Length, match.Index + match.Length + ReportContextChars);

        var slice = value[start..end]
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return (start > 0 ? "..." : string.Empty) + slice + (end < value.Length ? "..." : string.Empty);
    }

    /// <summary>
    /// No string this server SHIPS inside its own assembly — an error message, a description, a
    /// hint — carries internal planning vocabulary.
    /// </summary>
    /// <remarks>
    /// See the class remarks for why <c>tests/</c> is out of scope and for the measurement that made
    /// this leg necessary: the five surface-enumerating legs stayed green while
    /// <c>ListRunsOrchestrator</c> carried a reintroduced <c>(spec §4.5)</c> in the message a user
    /// sees for an out-of-range <c>limit</c>.
    /// </remarks>
    [Fact]
    public void NoShippedStringLiteral_CarriesInternalPlanningVocabulary()
    {
        var files = SourceGuardScan.SourceFilesInSrc().ToArray();

        // Anti-vacuity, proportionate to the truth (170 files today) rather than a token 10: a
        // census that reads nothing reports no leaks and passes for free, which is the one way this
        // leg could stop guarding anything without saying so.
        Assert.True(
            files.Length >= 120,
            $"Only {files.Length} .cs files found under src/ — scope is wrong. This is not a partial "
            + "read: src/ holds a single project and SourceFilesInSrc() throws outright if src/ is "
            + "gone, so a count this low means the tree moved out from under src/ or the "
            + "build-output filter has started excluding real sources.");

        var leaks = new List<string>();
        var literals = 0;

        foreach (var path in files)
        {
            var file = SourceGuardScan.ToRepoRelativeForwardSlashPath(path);
            var (hits, scanned) = ScanLiteralsForForbiddenVocabulary(File.ReadAllText(path), file);

            literals += scanned;
            leaks.AddRange(hits.Select(hit =>
                $"{file}:{hit.Line}: [{hit.Pattern}] {hit.Match}\n      {hit.Context}"));
        }

        // The second half of the anti-vacuity floor, and NOT redundant with the file count: a
        // truncated kind list, or a parse that silently produced an empty tree, would read every
        // file and still see no literals. 3,075 today, measured.
        Assert.True(
            literals >= 2_000,
            $"Only {literals} literal tokens read across {files.Length} files — the parse or the "
            + "kind list is broken, and a census that sees no literals reports no leaks.");

        Assert.True(
            leaks.Count == 0,
            $"{leaks.Count} internal-planning leak(s) in SHIPPED string literals under src/ — text "
            + "an exception, a hint or a description puts in front of a model at run time, which no "
            + "site crawler can ever reach:\n  "
            + string.Join("\n  ", leaks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            + "\n\nDrop the citation and keep the fact: a caller cannot open a maintainer-local spec "
            + "section, a sprint plan or a story id, and a message that points at one tells them "
            + "nothing they can act on. State what is wrong and what to do instead.\n"
            + "COMMENTS AND XML DOCUMENTATION ARE NOT AFFECTED and must not be changed to satisfy "
            + "this gate — they are Roslyn trivia, never tokens, so nothing listed above is one.");
    }

    /// <summary>
    /// C# source, parsed rather than pattern-matched, for the census's own proof: a fixture in which
    /// every forbidden shape appears once as something that SHIPS and once as something that does
    /// not.
    /// </summary>
    private const string LiteralCensusFixture = """""
        namespace Fixture;

        // TRIVIA-ONLY: this line cites spec §4.5 and sprint 9, and story US-S9-99 with it.
        /// <summary>TRIVIA-ONLY XML doc, also citing spec §4.5 and S09-A-01.</summary>
        public static class Sample
        {
            public static string SanitiseForDisplay(string value)
            {
                // The IDENTIFIER that a bare case-insensitive `sprint` regex matches out of
                // "i-sPrint-ableAscii". It is not a literal, so a token walk never sees it.
                var isPrintableAscii = value.Length > 0;
                return isPrintableAscii ? value : "";
            }

            public static string Interpolated(int maxLimit) =>
                $"'limit' must be between 1 and {maxLimit} (spec §4.5)";

            public static string Concatenated() =>
                "the run was refused "
                + "(spec §4.6)";

            public static string Raw() =>
                """
                a raw multi-line body citing spec §4.7
                """;

            // The four kinds no src/ file uses today, exercised here so the list carries them as a
            // demonstrated property rather than an untested hope about the lexer.
            public static string SingleLineRaw() => """a single-line raw body citing spec §4.8""";

            public static ReadOnlySpan<byte> Utf8() => "a UTF-8 literal citing spec §4.9"u8;

            public static ReadOnlySpan<byte> Utf8SingleLineRaw() =>
                """a UTF-8 single-line raw body citing spec §5.1"""u8;

            public static ReadOnlySpan<byte> Utf8MultiLineRaw() =>
                """
                a UTF-8 multi-line raw body citing spec §5.2
                """u8;
        }
        """"";

    /// <summary>
    /// The census sees an interpolated segment, a concatenated segment and a raw string; it does not
    /// see a comment, an XML doc, or an identifier that merely CONTAINS a forbidden word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the argument for Roslyn over a line regex, written as an assertion instead of as a
    /// claim in a comment.</b> MEASURED on this tree: the eleven patterns hit 851 lines under
    /// <c>src/</c>; strip comments and exactly two survive, <c>TextSanitiser.cs</c>'s
    /// <c>var isPrintableAscii = …</c> and the <c>if (isPrintableAscii)</c> on the line below it —
    /// an identifier, matched through "i-sPrint-ableAscii". The match comes ONLY from the bare
    /// case-insensitive <c>sprint</c> pattern: <c>[Ss]print[- ]\d+</c> requires a lower-case <c>p</c>
    /// and a number, so reproducing this with the numbered pattern finds nothing and would read as
    /// though the claim were wrong. A regex approach would need an allowlist entry for those two
    /// lines on day one, and that entry would then excuse a genuine leak on the same line. A token
    /// walk excludes them by construction, and the final assertion below pins the real file rather
    /// than only the fixture.
    /// </para>
    /// <para>
    /// <b>And the nearer alternative is in this repository, so it is the one to beat:</b>
    /// <c>SourceGuardScan.StripCommentsAndStringLiterals</c> already blanks comments, which is
    /// exactly the 849-line problem above. It loses anyway, and not on taste — it blanks string
    /// LITERALS too, so the very text this gate is looking for is the text it erases. Even inverted
    /// to keep literals it would still lose: its own remarks concede it is "not a C# lexer", it
    /// treats ANY <c>"""</c> as a terminator regardless of the opening delimiter's quote count, and
    /// it has no notion of interpolation holes — so a <c>$"""…"""</c> body (this repository ships
    /// one) is mis-lexed, and the regression this leg exists to catch lives in an interpolated
    /// string. Roslyn is not the heavier option here; it is the only one that draws the distinction
    /// the gate is built on.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLiteralCensus_SeesInterpolationAndRawStrings_ButNeitherCommentsNorIdentifiers()
    {
        var (hits, literals) = ScanLiteralsForForbiddenVocabulary(LiteralCensusFixture, "fixture.cs");

        Assert.True(literals > 0, "The fixture parsed to no literal tokens at all.");

        // Every shipping shape in the fixture is reported, and each carries a DIFFERENT section
        // number so a single over-broad match cannot satisfy more than its own. The first three are
        // the kinds src/ actually uses (interpolated, plain, multi-line raw); the last four are the
        // kinds it does not, pinned here so LiteralKinds' long tail is exercised somewhere.
        string[] sections = ["§4.5", "§4.6", "§4.7", "§4.8", "§4.9", "§5.1", "§5.2"];

        Assert.All(sections, section => Assert.Contains(
            hits,
            hit => hit.Pattern == "spec-section-citation"
                && hit.Context.Contains(section, StringComparison.Ordinal)));

        // Trivia is invisible: the comment and the XML doc cite §4.5 and a story id, and neither is
        // a token. Keyed on a marker word that appears ONLY in those two lines.
        Assert.DoesNotContain(hits, hit => hit.Context.Contains("TRIVIA-ONLY", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, hit => hit.Pattern == "repo-story-id");
        Assert.DoesNotContain(hits, hit => hit.Pattern == "engine-story-id");

        // The identifier is invisible for the same structural reason. The fixture's other "sprint"
        // occurrences (the TRIVIA-ONLY comment's "sprint 9", and the two in the comment above
        // isPrintableAscii) are trivia as well, so a bare sprint-bare assertion would not say WHICH
        // of them had become visible — hence the second, specific one.
        Assert.DoesNotContain(hits, hit => hit.Pattern == "sprint-bare");
        Assert.DoesNotContain(
            hits, hit => hit.Context.Contains("isPrintableAscii", StringComparison.Ordinal));

        // The real file the regex approach would have tripped over, asserted directly.
        var sanitiser = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "TextSanitiser.cs");

        Assert.True(File.Exists(sanitiser), $"'{sanitiser}' has moved; re-point this assertion at it.");

        var source = File.ReadAllText(sanitiser);
        Assert.Contains("isPrintableAscii", source, StringComparison.Ordinal);

        var (sanitiserHits, _) = ScanLiteralsForForbiddenVocabulary(source, "src/Vouchfx.Mcp/TextSanitiser.cs");
        Assert.True(
            sanitiserHits.Count == 0,
            $"TextSanitiser.cs reported {sanitiserHits.Count} hit(s). Its 'isPrintableAscii' is an "
            + "IDENTIFIER, and the census must not see it — if it does, the scan has stopped "
            + "distinguishing tokens from text and the whole leg is now an allowlist problem.");
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
