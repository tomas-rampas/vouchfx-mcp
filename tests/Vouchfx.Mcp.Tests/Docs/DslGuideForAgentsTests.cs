using System.Text;
using System.Text.RegularExpressions;
using Vouchfx.Mcp.Docs;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Docs;

/// <summary>
/// US-S5-05: <c>docs/dsl-guide-for-agents.md</c> is within its size budget, model-reader-shaped, and
/// — the load-bearing one — every YAML example in it validates against the vendored schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema check is why this file exists.</b> A guide is only worth shipping from a repository
/// rather than from prose if something stops its examples drifting from what the engine accepts. This
/// runs every fenced block through the SAME <see cref="SuiteValidator"/> pipeline
/// <c>validate_suite</c> uses, against the SAME embedded composed schema. An example that stops
/// validating fails the build — the identical guarantee <c>ExampleSuiteCatalogueTests</c> gives the
/// <c>examples/</c> directory.
/// </para>
/// <para>
/// <b>On parsing YAML in-process here, given US-S5-01's Scanner-spin lesson.</b> That hazard is real
/// and unchanged — a twelve-byte input can drive YamlDotNet's Scanner into an uninterruptible spin,
/// which is why <c>vouchfx://workspace/specs</c> parses in a child process. It does not apply to this
/// test. The bytes are a REPO-AUTHORED markdown file, fixed at build time, that no caller can supply
/// or influence; the distinction that matters is not "is this YAML" but "can an attacker choose the
/// bytes", and here they cannot. A hostile example would also have to be committed to this repository
/// first, at which point a hanging test is the least of the problems. In-process is correct here for
/// the same reason it is correct in <c>ExampleSuiteCatalogueTests</c>.
/// </para>
/// <para>
/// <b>Fragment-vs-document: every fenced block is a COMPLETE document, deliberately.</b> The
/// alternative — allowing fragments and wrapping them in a synthetic envelope before validation —
/// was rejected: the wrapper would be test-only scaffolding that the reader never sees, so a fragment
/// could validate here and still be uncopyable in practice. The guide's own opening line promises
/// "every YAML block below is a complete, schema-valid document — copy one and edit it", and
/// <see cref="EveryFencedYamlBlock_IsACompleteDocumentWithSteps"/> holds that promise. If a future
/// edit genuinely needs a fragment, mark its fence with a different language tag so it is excluded
/// explicitly rather than silently.
/// </para>
/// <para>
/// <b>What schema validation cannot reach.</b> Three of the guide's claims are about ENGINE RUNTIME
/// BEHAVIOUR, not document shape, so no assertion in this class can confirm them: that an absolute
/// URL in <c>path</c> is rejected as an SSRF guard, that a bare step-type family (without a
/// <c>.provider</c> suffix) is rejected, and that <c>${secret:...}</c> values are redacted from logs,
/// reports and the event stream. They are prose held true by review; spot-check them at the next
/// <c>ENGINE_PIN</c> bump, alongside the <c>*AgainstPinnedCliTests</c> claims that are host-conditional
/// for the same class of reason.
/// </para>
/// </remarks>
public class DslGuideForAgentsTests
{
    /// <summary>The AC's own budget: at most 20 KB.</summary>
    private const int MaxSizeBytes = 20 * 1024;

    /// <summary>A fenced block tagged as YAML — the form every example in the guide uses.</summary>
    private static readonly Regex YamlFence =
        new(@"^```yaml\r?\n(?<body>.*?)^```", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    private static string GuidePath =>
        Path.Combine(SourceGuardScan.RepoRoot.FullName, "docs", "dsl-guide-for-agents.md");

    private static string GuideText()
    {
        Assert.True(File.Exists(GuidePath), $"Expected the guide at '{GuidePath}'.");
        return File.ReadAllText(GuidePath);
    }

    public static TheoryData<int> ExampleIndices()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < YamlFence.Matches(GuideText()).Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static string ExampleAt(int index) =>
        YamlFence.Matches(GuideText())[index].Groups["body"].Value;

    // ── The embed is the file ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The embedded manifest resource is byte-for-byte the tracked file this class reads.
    /// </summary>
    /// <remarks>
    /// <b>Closes a real hole</b> (a peer review's finding). Every OTHER test in this class reads
    /// <c>docs/dsl-guide-for-agents.md</c> from the repository, and
    /// <see cref="RealDslGuideResourceMcpTests"/> asserts the WIRE equals
    /// <see cref="DslGuideDocument.RawMarkdown"/> — but nothing joined the two ends. Repointing the
    /// csproj's <c>LogicalName</c> at some other embedded file would leave both of those green while
    /// hosts received a different document entirely. This is the missing link in that chain:
    /// repo file → embed (here) → wire (there).
    /// </remarks>
    [Fact]
    public void TheEmbeddedGuide_IsByteForByteTheTrackedFile() =>
        Assert.Equal(GuideText(), DslGuideDocument.RawMarkdown);

    // ── Size ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGuideIsWithinItsSizeBudget()
    {
        var bytes = Encoding.UTF8.GetByteCount(GuideText());

        Assert.True(
            bytes <= MaxSizeBytes,
            $"docs/dsl-guide-for-agents.md is {bytes} bytes, over the {MaxSizeBytes}-byte budget. "
            + "It is written for a model reader: cut prose, not examples.");

        // Anti-vacuity in the other direction: a guide that had been emptied would pass a size cap.
        Assert.True(bytes > 2_000, $"The guide is implausibly short ({bytes} bytes).");
    }

    // ── Every example validates ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGuideContainsExamples()
    {
        // Anti-vacuity for the theory below: a regex that stopped matching would make it run zero
        // cases and report success.
        Assert.True(
            YamlFence.Matches(GuideText()).Count >= 4,
            "Expected at least four YAML examples; the fence pattern may have stopped matching.");
    }

    [Theory]
    [MemberData(nameof(ExampleIndices))]
    public void EveryFencedYamlBlock_ValidatesAgainstTheVendoredSchema(int index)
    {
        var yaml = ExampleAt(index);

        // ValidationLevel.Full runs the schema pass AND the semantic pass — deliberately stricter than
        // the AC's "zero schema errors", because an example that tripped a semantic rule of severity
        // error (VFX-D-1207, a secret literal) would be teaching the exact practice the guide warns
        // against two sections later.
        var analysis = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full, $"dsl-guide example {index}");

        Assert.True(
            analysis.Valid,
            $"Example {index} in docs/dsl-guide-for-agents.md is not valid: "
            + string.Join(
                " | ",
                analysis.Errors.Select(error => $"[{error.Code}] {error.Message}")
                    .Concat(analysis.SemanticDiagnostics.Select(d => $"[{d.Code}] {d.Message}"))));
        Assert.Empty(analysis.Errors);
    }

    [Theory]
    [MemberData(nameof(ExampleIndices))]
    public void EveryFencedYamlBlock_IsACompleteDocumentWithSteps(int index)
    {
        var analysis = SuiteValidator.AnalyseYaml(
            ExampleAt(index), ValidationLevel.Schema, $"dsl-guide example {index}");

        var summary = Assert.IsType<SuiteSummary>(analysis.Summary);

        // The guide promises copyable documents, not fragments — see this class's remarks.
        Assert.True(summary.Steps > 0, $"Example {index} declares no steps.");
        Assert.NotEmpty(summary.StepTypes);
    }

    [Theory]
    [MemberData(nameof(ExampleIndices))]
    public void NoExampleContainsALiteralCredential(int index)
    {
        // Belt and braces alongside VFX-D-1207: the guide's whole secrets section is undermined if one
        // of its own examples shows the practice it forbids.
        var yaml = ExampleAt(index);

        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var marker in new[] { "password:", "Authorization:", "apiKey:", "token:" })
            {
                if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(
                        trimmed.Contains("${secret:", StringComparison.Ordinal),
                        $"Example {index} line '{trimmed}' looks like a credential that is not a "
                        + "${secret:...} reference.");
                }
            }
        }
    }

    // ── The AC's required topics ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("capture")]                 // state threading
    [InlineData("{placeholder}")]           // …and its other half
    [InlineData("verifyMode: RETRY")]       // RETRY semantics
    [InlineData("timeout")]                 // …with an explicit timeout
    [InlineData("${secret:")]               // secrets as references
    [InlineData("Do / don't")]              // the do/don't list
    public void TheGuideCoversEveryRequiredTopic(string topic) =>
        Assert.Contains(topic, GuideText(), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TheGuideTeachesTheFourOutcomes_InResponseStringVocabulary()
    {
        var text = GuideText();

        foreach (var outcome in Enum.GetNames<Vouchfx.Mcp.Run.RunVerdict>())
        {
            Assert.Contains($"`{outcome}`", text, StringComparison.Ordinal);
        }

        // And never the engine's wire spellings — the guide's reader is a host-facing author, and
        // sprint-00-overview.md §5 reserves those for the raw event relay.
        //
        // ALL FOUR, matching ExplainFailurePromptTests' own list. An earlier version checked only the
        // two that differ by more than casing, which left PASS/FAIL — the two an author is most likely
        // to type out of habit from another tool — unguarded for no saving at all.
        foreach (var wireToken in new[] { "PASS", "FAIL", "ENV_ERROR", "INCONCLUSIVE" })
        {
            Assert.DoesNotContain(wireToken, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheGuideNamesNoRetiredIdentifier()
    {
        var text = GuideText();

        foreach (var banned in Prompts.PromptTextAssertions.RetiredIdentifiers)
        {
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryToolTheGuideNames_IsOneThisServerAdvertises()
    {
        // The guide points a reader at tools; naming one that does not exist would send them to a 404
        // at the exact moment they are learning what exists.
        var text = GuideText();

        foreach (var tool in new[]
        {
            "validate_suite", "describe_step_type", "list_step_types", "scaffold_suite", "explain_diagnostic",
        })
        {
            Assert.Contains(tool, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryStepTypeTheExamplesUse_IsInTheVendoredCatalogue()
    {
        // Derived from the schema's own vocabulary rather than a re-typed list, so a pin bump that
        // retired a step type fails here instead of leaving the guide teaching a dead one.
        var known = StepTypeCatalogue.All.Select(info => info.Type).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(known);

        foreach (Match match in YamlFence.Matches(GuideText()))
        {
            var analysis = SuiteValidator.AnalyseYaml(
                match.Groups["body"].Value, ValidationLevel.Schema, "dsl-guide example");

            foreach (var stepType in analysis.Summary!.StepTypes)
            {
                Assert.True(
                    known.Contains(stepType),
                    $"The guide uses step type '{stepType}', which the vendored catalogue does not list.");
            }
        }
    }
}
