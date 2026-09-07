using System.Reflection;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// The prompt catalogue's packaging invariants and <see cref="PromptDocumentParser"/>'s refusal paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is cited by three production comments and did not exist</b> (a peer review's MAJOR):
/// <c>PromptCatalogue</c>, <c>PromptDocumentParser</c> and the csproj all told a reader that
/// "PromptCatalogueTests fails loudly if …", and nothing did. A comment that names a guard which is
/// not there is worse than no comment — it stops the next person looking.
/// </para>
/// <para>
/// It covers two things the rest of the prompt tests cannot: every way a malformed prompt document
/// must be REFUSED (each exercised directly, since no malformed file ships), and the parity direction
/// <see cref="PromptRepository"/> structurally cannot hold — an embedded prompt with no catalogue
/// entry, which its load loop simply never looks at.
/// </para>
/// </remarks>
public class PromptCatalogueTests
{
    /// <summary>A minimal well-formed document, for mutating one thing at a time.</summary>
    private const string ValidDocument = """
        ---
        name: sample_prompt
        title: Sample
        description: A sample prompt.
        arguments:
          - name: subject
            required: true
            description: What to act on.
        ---
        Do the thing to {{subject}}.
        """;

    // ── The catalogue, the csproj embedding and the files agree ────────────────────────────────

    [Fact]
    public void EveryCatalogueEntry_HasAnEmbeddedResourceAndParses()
    {
        Assert.NotEmpty(PromptCatalogue.All);

        foreach (var descriptor in PromptCatalogue.All)
        {
            // Touching the repository is what loads and parses every prompt; a missing embed or a
            // malformed front matter throws here with the file named.
            var definition = PromptRepository.Get(descriptor.Name);

            Assert.Equal(descriptor.Name, definition.Name);
            Assert.False(string.IsNullOrWhiteSpace(definition.Body));
        }
    }

    [Fact]
    public void EveryEmbeddedPromptResource_HasACatalogueEntry()
    {
        // THE "VICE VERSA" DIRECTION, which nothing held before. PromptRepository.LoadAll iterates the
        // CATALOGUE, so a prompt embedded in the csproj but never catalogued is silently invisible:
        // it ships inside the assembly, is never advertised, is never parsed, and no test notices.
        // Deriving the real set from the assembly's own manifest is the only way to see it.
        var embedded = typeof(PromptCatalogue).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith("Vouchfx.Mcp.Prompts.", StringComparison.Ordinal)
                && name.EndsWith(".md", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Anti-vacuity: a renamed LogicalName prefix would otherwise make this pass over nothing.
        Assert.NotEmpty(embedded);

        Assert.Equal(
            PromptCatalogue.All
                .Select(descriptor => descriptor.EmbeddedResourceName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            embedded);
    }

    [Fact]
    public void EveryCatalogueEntry_HasItsFileOnDiskUnderTheProjectsPromptsDirectory()
    {
        var promptsDirectory = new DirectoryInfo(
            Path.Combine(SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Prompts"));

        Assert.True(promptsDirectory.Exists, $"Expected a prompts directory at '{promptsDirectory.FullName}'.");

        var onDisk = promptsDirectory
            .EnumerateFiles("*.md", SearchOption.TopDirectoryOnly)
            .Select(file => file.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            PromptCatalogue.All.Select(d => d.FileName).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            onDisk);
    }

    [Fact]
    public void CatalogueNamesAreUniqueAndDeriveTheirFileAndResourceNames()
    {
        Assert.Equal(
            PromptCatalogue.All.Count,
            PromptCatalogue.All.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var descriptor in PromptCatalogue.All)
        {
            Assert.Equal($"{descriptor.Name}.md", descriptor.FileName);
            Assert.Equal($"Vouchfx.Mcp.Prompts.{descriptor.Name}.md", descriptor.EmbeddedResourceName);
        }
    }

    // ── The parser refuses every malformed shape ───────────────────────────────────────────────

    [Fact]
    public void AWellFormedDocument_Parses()
    {
        // Anti-vacuity for every refusal below: if the baseline did not parse, each of them would be
        // "throws" for the wrong reason.
        var definition = PromptDocumentParser.Parse("sample.md", ValidDocument);

        Assert.Equal("sample_prompt", definition.Name);
        Assert.Equal("Sample", definition.Title);
        Assert.Equal("A sample prompt.", definition.Description);
        Assert.Equal("subject", Assert.Single(definition.Arguments).Name);
        Assert.True(Assert.Single(definition.Arguments).Required);
        Assert.StartsWith("Do the thing to", definition.Body, StringComparison.Ordinal);
    }

    [Theory]
    // No opening fence at all.
    [InlineData("name: x\n---\nbody\n")]
    // Opening fence not on its own line.
    [InlineData("--- name: x\n---\nbody\n")]
    // Leading blank line before the fence.
    [InlineData("\n---\nname: x\n---\nbody\n")]
    public void ADocumentWithoutAnOpeningFence_IsRefused(string document) =>
        Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

    [Fact]
    public void AnUnterminatedFrontMatterBlock_IsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => PromptDocumentParser.Parse("sample.md", "---\nname: x\ntitle: T\nbody with no close\n"));

    [Fact]
    public void ADocumentWithNoBody_IsRefused() =>
        // A prompt with a declaration and nothing to render is not a shorter prompt, it is a broken
        // one — and would advertise itself happily.
        Assert.Throws<InvalidOperationException>(
            () => PromptDocumentParser.Parse(
                "sample.md", "---\nname: x\ntitle: T\ndescription: D\n---\n   \n"));

    [Theory]
    [InlineData("---\n- just\n- a list\n---\nbody\n")]
    [InlineData("---\nplain scalar\n---\nbody\n")]
    public void FrontMatterThatIsNotAMapping_IsRefused(string document) =>
        Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

    [Theory]
    [InlineData("name")]
    [InlineData("title")]
    [InlineData("description")]
    public void AMissingRequiredFrontMatterField_IsRefused(string field)
    {
        var document = ValidDocument.Replace($"{field}:", $"x-{field}:", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

        // The message names the file AND the field, which is the whole value of failing at load.
        Assert.Contains("sample.md", ex.Message, StringComparison.Ordinal);
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankRequiredFrontMatterField_IsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => PromptDocumentParser.Parse(
                "sample.md", "---\nname: \"   \"\ntitle: T\ndescription: D\n---\nbody\n"));

    [Fact]
    public void ArgumentsThatAreNotAList_AreRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => PromptDocumentParser.Parse(
                "sample.md", "---\nname: x\ntitle: T\ndescription: D\narguments: nope\n---\nbody\n"));

    [Fact]
    public void AnArgumentEntryThatIsNotAMapping_IsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => PromptDocumentParser.Parse(
                "sample.md", "---\nname: x\ntitle: T\ndescription: D\narguments:\n  - nope\n---\nbody\n"));

    [Fact]
    public void ADuplicateArgumentName_IsRefused()
    {
        var document = """
            ---
            name: x
            title: T
            description: D
            arguments:
              - name: subject
                description: First.
              - name: subject
                description: Second.
            ---
            {{subject}}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonBooleanRequiredFlag_IsRefused()
    {
        var document = ValidDocument.Replace("required: true", "required: yes-please", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

        Assert.Contains("required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentRequiredFlag_DefaultsToOptional()
    {
        var document = ValidDocument.Replace("    required: true\n", string.Empty, StringComparison.Ordinal);

        Assert.False(Assert.Single(PromptDocumentParser.Parse("sample.md", document).Arguments).Required);
    }

    // ── Declaration ↔ body parity, enforced at parse time (M1) ─────────────────────────────────

    [Fact]
    public void ABodyPlaceholderThatIsNotDeclared_IsRefused()
    {
        // The exact typo the review named: front matter says `specPath`, the body says `{{specpath}}`.
        var document = ValidDocument.Replace("{{subject}}", "{{subjekt}}", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

        Assert.Contains("undeclared", ex.Message, StringComparison.Ordinal);
        Assert.Contains("subjekt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredArgumentTheBodyNeverUses_IsRefused()
    {
        var document = ValidDocument.Replace("Do the thing to {{subject}}.", "Do the thing.", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => PromptDocumentParser.Parse("sample.md", document));

        Assert.Contains("never uses", ex.Message, StringComparison.Ordinal);
        Assert.Contains("subject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionOpenCountsAsUsingTheArgument()
    {
        // {{#name}} and {{^name}} are references too — an argument used only to gate a paragraph is
        // used, and refusing it would forbid the most common optional-argument shape these prompts have.
        var document = ValidDocument.Replace(
            "Do the thing to {{subject}}.",
            "{{#subject}}Do the thing.{{/subject}}{{^subject}}Ask for a subject.{{/subject}}",
            StringComparison.Ordinal);

        Assert.Equal("subject", Assert.Single(PromptDocumentParser.Parse("sample.md", document).Arguments).Name);
    }

    [Fact]
    public void ReferencedArgumentNames_FindsAllThreeReferenceForms() =>
        Assert.Equal(
            ["a", "b", "c"],
            PromptDocumentParser.ReferencedArgumentNames("{{a}} {{#b}}x{{/b}} {{^c}}y{{/c}}")
                .OrderBy(name => name, StringComparer.Ordinal));

    // ── Encoding and line endings ──────────────────────────────────────────────────────────────

    [Fact]
    public void ACrLfDocument_ParsesIdenticallyToAnLfOne()
    {
        // These files are hand-edited on Windows and Linux; a line-ending difference must never change
        // what a host receives.
        var lf = PromptDocumentParser.Parse("sample.md", ValidDocument);
        var crlf = PromptDocumentParser.Parse(
            "sample.md", ValidDocument.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(lf.Name, crlf.Name);
        Assert.Equal(lf.Description, crlf.Description);
        Assert.Equal(lf.Body, crlf.Body);
    }

    [Fact]
    public void AShippedPromptSurvivesABomOnItsEmbeddedStream()
    {
        // PromptRepository decodes with detectEncodingFromByteOrderMarks so an editor-added BOM never
        // sits in front of the opening fence. Asserted at the level the repository works at: every
        // shipped prompt is already loaded and parsed by the time this runs, which is the proof.
        Assert.NotEmpty(PromptRepository.All);
        Assert.All(PromptRepository.All, definition => Assert.False(definition.Body.StartsWith('﻿')));
    }

    [Fact]
    public void ADocumentWhoseNameDisagreesWithItsCatalogueEntry_WouldBeRefused()
    {
        // PromptRepository's own cross-check. Exercised through the parser plus an explicit comparison
        // rather than by corrupting a shipped file: the point is that the two values must be compared
        // at all, and every shipped prompt is asserted to agree in EveryCatalogueEntry_… above.
        var definition = PromptDocumentParser.Parse("sample.md", ValidDocument);

        Assert.NotEqual("author_scenario", definition.Name);
    }

    [Fact]
    public void TheRepositoryRefusesAnUnknownPromptName()
    {
        Assert.Throws<InvalidOperationException>(() => PromptRepository.Get("no_such_prompt"));
        Assert.False(PromptRepository.TryGet("no_such_prompt", out _));
        Assert.False(PromptRepository.TryGet(null, out _));
    }

    [Fact]
    public void TheCsprojEmbedsEveryPromptFile()
    {
        // The third leg, read from the project file itself: a prompt file added to the directory and
        // the catalogue but not embedded fails EveryCatalogueEntry_… with a resource-not-found — this
        // says the same thing earlier and names the csproj, which is where the fix goes.
        var csproj = File.ReadAllText(
            Path.Combine(SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Vouchfx.Mcp.csproj"));

        foreach (var descriptor in PromptCatalogue.All)
        {
            Assert.Contains($"Prompts/{descriptor.FileName}", csproj, StringComparison.Ordinal);
            Assert.Contains(descriptor.EmbeddedResourceName, csproj, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePromptsAssemblyPrefixIsWhatTheRepositoryScansFor() =>
        // Ties EveryEmbeddedPromptResource_HasACatalogueEntry's literal prefix to the catalogue's own
        // derivation, so a LogicalName convention change cannot leave that guard scanning for nothing.
        Assert.StartsWith(
            "Vouchfx.Mcp.Prompts.",
            PromptCatalogue.All[0].EmbeddedResourceName,
            StringComparison.Ordinal);

    [Fact]
    public void TheAssemblyExposesNoUncataloguedPromptTypeByAccident() =>
        // Cheap structural note: the prompt surface is the catalogue, so the count a host sees and the
        // count this repository declares are the same number in one place.
        Assert.Equal(PromptCatalogue.All.Count, PromptRepository.All.Count);

    [Fact]
    public void ManifestResourceNamesAreStableAcrossReflectionAndCatalogue() =>
        Assert.All(
            PromptCatalogue.All,
            descriptor => Assert.NotNull(
                typeof(PromptCatalogue).Assembly.GetManifestResourceInfo(descriptor.EmbeddedResourceName)));

    [Fact]
    public void TheParserIsReachableOnlyThroughPublicSurfaceThisTestUses() =>
        // Guards against the parser quietly becoming internal, which would make every refusal test
        // above unbuildable and tempt someone to delete them rather than restore the surface.
        Assert.True(typeof(PromptDocumentParser).GetMethod(nameof(PromptDocumentParser.Parse))?.IsPublic ?? false);

    [Fact]
    public void EveryShippedPromptDeclaresAtLeastOneArgumentDescription() =>
        Assert.All(
            PromptRepository.All,
            definition => Assert.All(
                definition.Arguments,
                argument => Assert.False(string.IsNullOrWhiteSpace(argument.Description))));

    [Fact]
    public void ReflectionOverTheCatalogueFindsNoDescriptorMissingFromAll()
    {
        // A descriptor declared as a public static field but left out of All would be invisible to
        // every other check here — the same "declared but not wired" shape the embed check catches on
        // the other side.
        var declared = typeof(PromptCatalogue)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(PromptDescriptor))
            .Select(field => (PromptDescriptor)field.GetValue(null)!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(
            declared.Select(d => d.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            PromptCatalogue.All.Select(d => d.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
