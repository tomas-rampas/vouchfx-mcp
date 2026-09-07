using ModelContextProtocol;
using Vouchfx.Mcp.Examples;
using Vouchfx.Mcp.Resources;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Examples;

/// <summary>
/// US-S5-01 AC-004: the repo-local sample suites <c>vouchfx://examples/{name}</c> serves are
/// complete, schema-valid, comment-annotated, and free of literal secrets — enforced here rather
/// than asserted in prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema check is the load-bearing one.</b> Shipping examples from a repository is only
/// better than shipping them in documentation if something stops them drifting from what the engine
/// actually accepts, and that something is
/// <see cref="ExampleValidatesAgainstTheVendoredSchemaWithZeroErrors"/>: it runs each example through
/// the SAME <see cref="SuiteValidator"/> pipeline <c>validate_suite</c> uses, against the SAME
/// embedded composed schema, in this process. An example that stops validating fails the build.
/// </para>
/// <para>
/// <b>What this file cannot assert is that a suite PASSES against a live environment</b> — every
/// example names placeholder container images, and no test in this repository depends on Docker or
/// the real CLI (CLAUDE.md's testing conventions). See <c>ExampleSuites</c>' own header, which states
/// that boundary in code rather than leaving it to be discovered.
/// </para>
/// </remarks>
public class ExampleSuiteCatalogueTests
{
    public static TheoryData<string> ExampleNames()
    {
        var data = new TheoryData<string>();
        foreach (var example in ExampleSuites.All)
        {
            data.Add(example.Name);
        }

        return data;
    }

    [Fact]
    public void TheCatalogue_IsNonEmptyAndHasUniqueNames()
    {
        Assert.NotEmpty(ExampleSuites.All);
        Assert.Equal(
            ExampleSuites.All.Count,
            ExampleSuites.All.Select(example => example.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void EveryCatalogueEntry_IsEmbeddedAndReadable(string name)
    {
        // Anti-vacuity for everything below: if the csproj's EmbeddedResource item list and
        // ExampleSuites.All ever fall out of step, ExampleSuiteRepository's static initialiser throws
        // here with a message naming the file that is missing.
        var yaml = ExampleSuiteRepository.GetRawText(name);

        Assert.False(string.IsNullOrWhiteSpace(yaml));
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void ExampleValidatesAgainstTheVendoredSchemaWithZeroErrors(string name)
    {
        var yaml = ExampleSuiteRepository.GetRawText(name);

        // ValidationLevel.Full runs the schema pass AND the semantic pass, which is deliberately
        // stricter than the AC asks for: an example that trips a semantic rule of severity error
        // (VFX-D-1207, a literal secret) would be teaching the exact practice the file's own comments
        // warn against, so it must fail here too. `Valid` folds both in — see SuiteValidator's
        // verdict reconciliation.
        var analysis = SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full, $"examples/{name}.e2e.yaml");

        Assert.True(
            analysis.Valid,
            $"examples/{name}.e2e.yaml is not valid: "
            + string.Join(
                " | ",
                analysis.Errors.Select(error => $"[{error.Code}] {error.Message}")
                    .Concat(analysis.SemanticDiagnostics.Select(d => $"[{d.Code}] {d.Message}"))));
        Assert.Empty(analysis.Errors);
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void ExampleIsComplete_DeclaringStepsAndAtLeastOneStepType(string name)
    {
        var analysis = SuiteValidator.AnalyseYaml(
            ExampleSuiteRepository.GetRawText(name), ValidationLevel.Schema, $"examples/{name}.e2e.yaml");

        var summary = Assert.IsType<SuiteSummary>(analysis.Summary);

        // "Complete" in the AC's sense: a whole document, not a fragment. A suite with no steps
        // parses and validates but teaches nothing.
        Assert.True(summary.Steps > 0, $"examples/{name}.e2e.yaml declares no steps.");
        Assert.NotEmpty(summary.StepTypes);
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void ExampleIsCommentAnnotated(string name)
    {
        var yaml = ExampleSuiteRepository.GetRawText(name);
        var commentLines = yaml
            .Split('\n')
            .Count(line => line.TrimStart().StartsWith('#'));

        // The annotation IS the deliverable (AC-004: "annotated with comments explaining every
        // step") — an uncommented valid suite is already available from vouchfx-docs:///recipes. The
        // threshold is deliberately a floor rather than a ratio: it catches an example added without
        // annotation, and does not pretend to measure explanatory quality, which no assertion can.
        Assert.True(
            commentLines >= 10,
            $"examples/{name}.e2e.yaml has only {commentLines} comment lines; these files exist to "
            + "explain, not merely to validate.");
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void ExampleContainsNoLiteralSecret(string name)
    {
        var yaml = ExampleSuiteRepository.GetRawText(name);

        // Belt and braces alongside the VFX-D-1207 semantic rule the validity check already runs: a
        // suite whose only credential shape is `${secret:...}` cannot teach a host to write a literal
        // one. Checked textually so it holds even for a value the semantic rule's heuristics would
        // not classify.
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var marker in new[] { "password:", "Authorization:", "apiKey:", "token:" })
            {
                if (!trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Assert.True(
                    trimmed.Contains("${secret:", StringComparison.Ordinal),
                    $"examples/{name}.e2e.yaml line '{trimmed}' looks like a credential that is not a "
                    + "${secret:...} reference.");
            }
        }
    }

    [Fact]
    public void TheCatalogue_TheCsprojEmbeddingAndTheExamplesDirectory_AreTheSameSet()
    {
        // Fail-closed in BOTH directions, mirroring ErrorCatalogueFilesystemParityTests' own shape: a
        // file added to examples/ but not catalogued is never served (and would look like a bug to
        // whoever added it), and a catalogue entry with no file already fails
        // EveryCatalogueEntry_IsEmbeddedAndReadable above. This is the half that catches the former.
        var examplesDirectory = new DirectoryInfo(Path.Combine(SourceGuardScan.RepoRoot.FullName, "examples"));
        Assert.True(examplesDirectory.Exists, $"Expected an examples/ directory at '{examplesDirectory.FullName}'.");

        var onDisk = examplesDirectory
            .EnumerateFiles("*" + WorkspaceSpecIndexerFileSuffix, SearchOption.TopDirectoryOnly)
            .Select(file => file.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var catalogued = ExampleSuites.All
            .Select(example => example.FileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(catalogued, onDisk);
    }

    [Fact]
    public void AnUnknownExampleName_IsRefusedAndNamesTheAvailableOnes()
    {
        var ex = Assert.Throws<McpException>(() => ExampleResourceRegistry.Resolve("no-such-example"));

        foreach (var example in ExampleSuites.All)
        {
            Assert.Contains(example.Name, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ANetworkShapedExampleName_IsRefusedByTheSharedArgumentGuard()
    {
        var ex = Assert.Throws<McpException>(() => ExampleResourceRegistry.Resolve(@"\\attacker\share"));

        Assert.Contains("network/UNC", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void ResolvingByName_ReturnsTheEmbeddedTextVerbatim(string name) =>
        // Verbatim matters more here than for any other resource: YamlDotNet drops comments on a
        // round trip (SuiteNormalizer's measured finding), so a parse-and-re-emit anywhere on this
        // path would silently discard the annotation these files exist for.
        Assert.Equal(ExampleSuiteRepository.GetRawText(name), ExampleResourceRegistry.Resolve(name));

    /// <summary>
    /// The suffix the examples' own file names carry. Read from the production constant rather than
    /// re-typed, so a change to what counts as a suite file cannot leave this parity check looking at
    /// a different set from the one the server indexes.
    /// </summary>
    private const string WorkspaceSpecIndexerFileSuffix = Vouchfx.Mcp.Specs.WorkspaceSpecIndexer.SpecFileSuffix;
}
