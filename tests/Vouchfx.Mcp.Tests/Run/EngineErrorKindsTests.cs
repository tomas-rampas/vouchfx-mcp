using System.Reflection;
using System.Text.RegularExpressions;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// Covers <see cref="EngineErrorKinds"/> — the one shared home for the engine's
/// <c>OrchestrationErrorKind</c> names — and, more usefully, guards that its two consumers actually
/// draw from it rather than re-spelling the strings.
/// </summary>
/// <remarks>
/// <b>Why a guard and not just a constant.</b> Hoisting the vocabulary is only worth anything if
/// adding an engine kind stays a ONE-PLACE edit. The compiler enforces half of that already —
/// naming a constant that does not exist will not build — but nothing stops a future edit from
/// writing <c>"ImagePull"</c> inline and quietly reintroducing the divergence that made this file
/// necessary. That is what the source guard below catches.
/// </remarks>
public class EngineErrorKindsTests
{
    /// <summary>The files that recognise engine error kinds today, relative to the repo root.</summary>
    /// <remarks>
    /// <b>This list is ASSERTED against a repo-wide scan, not merely iterated.</b> An earlier version
    /// only looped over these two paths and claimed a third consumer "will fail the membership
    /// assertion below" — it would not have: a new file referencing <c>EngineErrorKinds</c> was
    /// simply never looked at. The guard below now derives the real set from
    /// <see cref="SourceGuardScan.SourceFilesInSrc"/> and requires EXACT equality with this list,
    /// which is the fail-closed shape the environment-variable guard already uses: a third consumer
    /// fails the test until someone adds it here deliberately.
    /// </remarks>
    private static readonly string[] ConsumerFiles =
    [
        "src/Vouchfx.Mcp/Diagnosis/VerdictReasonClassifier.cs",
        "src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs",
    ];

    [Fact]
    public void EveryDeclaredConstant_IsInTheAllSet()
    {
        var declared = typeof(EngineErrorKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.All(declared, kind => Assert.Contains(kind, (IReadOnlySet<string>)EngineErrorKinds.All));
        Assert.Equal(declared.Count, EngineErrorKinds.All.Count);
    }

    /// <summary>
    /// Neither consumer spells an error kind as a bare string literal — the only way the compiler's
    /// own enforcement could be bypassed.
    /// </summary>
    /// <remarks>
    /// Reads the RAW source with comments stripped and literals KEPT: the literals ARE what this
    /// guard looks for, so <c>SourceGuardScan.ExecutableSourceOf</c> (which blanks them) is the wrong
    /// tool here, and prose in a comment naming a kind must not be mistaken for code that does.
    /// </remarks>
    [Fact]
    public void NoFileInSrc_SpellsAnErrorKindAsABareStringLiteral()
    {
        var offenders = new List<string>();

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            var relative = SourceGuardScan.ToRepoRelativeForwardSlashPath(file);

            // The declaration itself is where the literals legitimately live.
            if (relative.EndsWith("Run/EngineErrorKinds.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var source = StripCommentsKeepingLiterals(File.ReadAllText(file));

            offenders.AddRange(
                EngineErrorKinds.All
                    .Where(kind => source.Contains($"\"{kind}\"", StringComparison.Ordinal))
                    .Select(kind => $"{relative}: \"{kind}\""));
        }

        Assert.True(
            offenders.Count == 0,
            $"These sites spell an engine error kind as a literal: [{string.Join(", ", offenders)}]. Use the "
            + "EngineErrorKinds constant instead — the vocabulary is shared so that adding an engine kind "
            + "stays a one-place edit, and a literal is the only way to bypass the compiler's own check.");
    }

    /// <summary>
    /// The set of files referencing <see cref="EngineErrorKinds"/> is EXACTLY the documented
    /// consumers — fail-closed in both directions.
    /// </summary>
    /// <remarks>
    /// Both directions matter. A file that stopped referencing the vocabulary would make the literal
    /// guard above pass vacuously over it; a NEW file that started referencing it is a third
    /// consumer, and this repo's whole reason for hoisting the vocabulary was that a second one had
    /// grown up unnoticed. Either way the test fails until someone updates
    /// <see cref="ConsumerFiles"/> deliberately.
    /// </remarks>
    [Fact]
    public void TheSetOfFilesReferencingTheVocabulary_IsExactlyTheDocumentedConsumers()
    {
        var referencing = new List<string>();

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            var relative = SourceGuardScan.ToRepoRelativeForwardSlashPath(file);
            if (relative.EndsWith("Run/EngineErrorKinds.cs", StringComparison.Ordinal))
            {
                continue;
            }

            if (Regex.IsMatch(SourceGuardScan.ExecutableSourceOf(file), @"\bEngineErrorKinds\s*\."))
            {
                referencing.Add(relative);
            }
        }

        Assert.Equal(
            ConsumerFiles.OrderBy(p => p, StringComparer.Ordinal),
            referencing.OrderBy(p => p, StringComparer.Ordinal));

        // ...and every kind each one names is a real member of the shared set — the "subset of the
        // vocabulary" property, explicit rather than implied by the compiler.
        foreach (var relative in referencing)
        {
            foreach (var name in ReferencedKinds(relative))
            {
                var value = typeof(EngineErrorKinds).GetField(name, BindingFlags.Public | BindingFlags.Static)
                    ?.GetRawConstantValue() as string;

                Assert.NotNull(value);
                Assert.Contains(value, (IReadOnlySet<string>)EngineErrorKinds.All);
            }
        }
    }

    /// <summary>
    /// The two consumers recognise deliberately DIFFERENT subsets, and this records which — so a
    /// future reader sees the divergence as a decision rather than an accident, and a change to
    /// either subset is a visible edit.
    /// </summary>
    [Fact]
    public void TheTwoConsumersSubsets_AreTheDocumentedOnes()
    {
        var classifier = ReferencedKinds("src/Vouchfx.Mcp/Diagnosis/VerdictReasonClassifier.cs");
        var remediation = ReferencedKinds("src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs");

        Assert.Equal(
            ["HealthGate", "ImagePull", "Seed", "Unhealthy", "WaitFor"],
            classifier.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(
            ["Discovery", "HealthGate", "ImagePull"],
            remediation.OrderBy(k => k, StringComparer.Ordinal));

        // The overlap and the divergence, both stated: two kinds are shared, and each side knows
        // things the other does not.
        Assert.Equal(["HealthGate", "ImagePull"], classifier.Intersect(remediation, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal));
        Assert.NotEmpty(classifier.Except(remediation, StringComparer.Ordinal));
        Assert.NotEmpty(remediation.Except(classifier, StringComparer.Ordinal));
    }

    private static List<string> ReferencedKinds(string relative)
    {
        var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

        return Regex.Matches(SourceGuardScan.ExecutableSourceOf(path), @"EngineErrorKinds\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Where(name => !string.Equals(name, nameof(EngineErrorKinds.All), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string StripCommentsKeepingLiterals(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//[^\n]*", string.Empty);
    }
}
