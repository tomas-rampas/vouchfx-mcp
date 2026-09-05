using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for the read-only invariant (CLAUDE.md; plan §2.7 invariant 5) —
/// re-asserted, not merely unregressed by omission, as US-S2-04 requires of the new
/// <c>normalize_suite</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrors <see cref="SecretHygieneSourceGuardTests"/> exactly</b>, because that is the shape this
/// repository already uses to hold an invariant that is a STATIC property of the source rather than
/// something observable by driving the compiled server: a whitespace-tolerant regex set naming the
/// forbidden code shapes, a fail-closed exact-equality check that derives the real set of offending
/// files from <c>src/</c> instead of trusting a hand-maintained list, and an end-to-end companion
/// (<c>RealNormalizeSuiteMcpTests</c>) proving the observable outcome. Neither half substitutes for
/// the other: the behavioural test proves one call did not touch one file; this class proves there is
/// no code path that could.
/// </para>
/// <para>
/// <b>What the invariant actually says, and why the guarded set is not empty.</b> The rule is that
/// this server never writes, modifies, or deletes a SUITE file, or anything else the caller named. It
/// is not "no <c>System.IO</c> mutation appears anywhere in <c>src/</c>": a small, enumerated set of
/// types legitimately manage artefacts they created themselves and own outright — see
/// <see cref="GuardedFilesystemMutationSiteRelativePaths"/> for each one and why it is admitted. A
/// site outside that set, anywhere in <c>src/</c>, fails
/// <see cref="FilesystemMutationSitesInSrc_ExactlyMatchTheGuardedSet"/> until it is deliberately
/// added.
/// </para>
/// <para>
/// <b>What that list is and is not.</b> It records which FILES are permitted to contain a mutation
/// shape — it is file-scoped, and says nothing about which call, on which path, under what
/// conditions. A second, unrelated write added inside <c>RunSuiteOrchestrator</c> would not fail this
/// test. So it is a fail-closed boundary on where such code may live, not an audit of what that code
/// does; reviewing a change to either named file still means reading it.
/// </para>
/// <para>
/// <b>The normalize/validate path carries no mutation at all</b>, and
/// <see cref="TheNormalizeAndValidatePipeline_ContainsNoFilesystemMutationApiWhatsoever"/> holds it
/// to the stronger standard the two orchestrators above cannot meet. <c>normalize_suite</c> is the
/// tool whose whole contract is "here is what your file COULD look like — you write it, not me", so
/// the code behind it must not be able to write anything, temp file or otherwise. The file list for
/// that check covers the tool boundary, the input resolver, the normalizer, the validator and the
/// worker CLIENT — and <c>Program.cs</c>, which is the worker itself: the parse and the canonical
/// render actually happen in the child process that file's <c>--validate-worker</c> mode runs, so
/// omitting it would leave the half of the pipeline that touches the suite unchecked.
/// </para>
/// </remarks>
public class ReadOnlySourceGuardTests
{
    /// <summary>
    /// The only files in <c>src/</c> allowed to mutate the filesystem, each for artefacts it created
    /// itself under a directory this server owns. Single source of truth for the fail-closed check
    /// below.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// <c>RunSuiteOrchestrator</c> sweeps its own stale <c>vouchfx-mcp-events-*.jsonl</c> files out
    /// of the OS temp directory.
    /// </description></item>
    /// <item><description>
    /// <c>ScaffoldSuiteOrchestrator</c> writes and then deletes the intent JSON it hands
    /// <c>vouchfx scaffold --intent</c>.
    /// </description></item>
    /// <item><description>
    /// <c>FileRunRegistry</c> (US-S3-01) creates a directory per run under the workspace's
    /// <c>outputDir</c> and publishes that run's metadata document into it — the ONE new mutation
    /// site this sprint adds, and admitted deliberately rather than by omission. Persisting run
    /// metadata is the story's whole point, and the read-only invariant is about never writing,
    /// modifying, or deleting a SUITE file or anything else the CALLER named: every path written
    /// there is composed from the workspace root US-S3-08 resolved plus a run id this server minted
    /// and shape-checked (<c>RunRegistryCore.IsWellFormedRunId</c>), so no caller-supplied string
    /// reaches a write. It appears here only when a workspace is configured; with none, the registry
    /// is <c>InMemoryRunRegistry</c>, which contains no mutation API at all — which is why that file
    /// is absent from this list rather than exempted in it.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static readonly string[] GuardedFilesystemMutationSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Run/FileRunRegistry.cs",
        "src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs",
        "src/Vouchfx.Mcp/Scaffold/ScaffoldSuiteOrchestrator.cs",
    ];

    /// <summary>
    /// Every file behind <c>normalize_suite</c>'s own pipeline — from the tool boundary, through the
    /// worker client, across the process boundary into the worker itself (<c>Program.cs</c>), to the
    /// normalizer. These are held to "no filesystem mutation API at all", with no temp-artefact
    /// exemption.
    /// </summary>
    /// <remarks>
    /// <c>Program.cs</c> is on this list because it IS the other half of the pipeline: its
    /// <c>--validate-worker</c> mode is where the suite is read, parsed and canonicalised, in a child
    /// process. Checking only the parent would leave the code that actually touches the file
    /// unguarded.
    /// </remarks>
    private static readonly string[] NormalizeAndValidatePipelineRelativePaths =
    [
        "src/Vouchfx.Mcp/Program.cs",
        "src/Vouchfx.Mcp/Tools/NormalizeSuiteTool.cs",
        "src/Vouchfx.Mcp/Tools/ValidateSuiteTool.cs",
        "src/Vouchfx.Mcp/Tools/ValidateSuiteInput.cs",
        "src/Vouchfx.Mcp/Normalization/SuiteNormalizer.cs",
        "src/Vouchfx.Mcp/Normalization/CanonicalKeyOrder.cs",
        "src/Vouchfx.Mcp/Normalization/SuiteNormalization.cs",
        "src/Vouchfx.Mcp/Validation/ValidationWorkerClient.cs",
        "src/Vouchfx.Mcp/Validation/SuiteValidator.cs",
    ];

    /// <summary>
    /// The filesystem-MUTATION shapes, as whitespace-tolerant regexes (<c>\s</c> matches line breaks
    /// in .NET by default, so reformatting cannot smuggle one past). Read-only APIs are deliberately
    /// excluded by construction rather than by an allow-list of files:
    /// <list type="bullet">
    /// <item><description><c>File.OpenRead</c> / <c>File.OpenText</c> / <c>File.ReadAll*</c> /
    /// <c>File.Exists</c> / <c>File.GetLastWriteTimeUtc</c> are reads, and none of them match any
    /// pattern below.</description></item>
    /// <item><description><c>Directory.EnumerateFiles</c>/<c>GetFiles</c> are reads; only
    /// <c>Create*</c>/<c>Delete</c>/<c>Move</c> match.</description></item>
    /// <item><description>The <c>FileInfo</c>/<c>DirectoryInfo</c> INSTANCE mutators are matched by
    /// receiver name rather than by method name alone, because <c>.Delete(</c> or <c>.Create(</c> on
    /// their own would match half the codebase. The receiver has to be an inline
    /// <c>new FileInfo(...)</c>/<c>new DirectoryInfo(...)</c> or a name ending in
    /// <c>File</c>/<c>Directory</c>/<c>Info</c>. <c>Path</c> is deliberately NOT a receiver suffix:
    /// <c>somePath.Replace(…)</c> is a string operation, and including it would make this pattern
    /// fire on ordinary code. A mutator reached through a differently-named local is the hole that
    /// leaves — narrowed, not closed.</description></item>
    /// </list>
    /// <b>Every pattern is matched against source with its COMMENTS REMOVED</b>
    /// (see <see cref="StripCommentsAndStringLiterals"/>), and that is what makes the
    /// <c>new FileStream(...)</c> lookahead trustworthy. It matches only when the statement does not
    /// name <c>FileAccess.Read</c> — the read-only overload <c>EventsFileReader</c> uses — and the
    /// <c>\b</c> after <c>Read</c> is load-bearing, because <c>FileAccess.ReadWrite</c> is a WRITE and
    /// must still match. On raw source that lookahead was disarmable by any <c>// FileAccess.Read</c>
    /// appearing before the next <c>;</c>, and the same blindness ran the other way: a
    /// <c>// File.WriteAllText(</c> in a comment would have been reported as a mutation site. String
    /// LITERALS are blanked for the second reason — a path or a message mentioning one of these API
    /// names is not a call to it.
    /// <para>
    /// <c>MemoryMappedFile</c> is matched on the bare type name with no read-only exemption. Its
    /// read-only mode exists, but nothing in <c>src/</c> uses the type at all, so the strict form
    /// costs nothing today; a legitimate read-only use should be admitted by naming its file, not by
    /// loosening the pattern.
    /// </para>
    /// </summary>
    private static readonly (string Description, Regex Pattern)[] FilesystemMutationShapes =
    [
        ("File.WriteAll*/AppendAll*/AppendText", new Regex(@"File\s*\.\s*(WriteAll|AppendAll|AppendText)\w*\s*\(", RegexOptions.Compiled)),
        ("File.Create*/CreateText", new Regex(@"File\s*\.\s*Create\w*\s*\(", RegexOptions.Compiled)),
        ("File.Delete", new Regex(@"File\s*\.\s*Delete\s*\(", RegexOptions.Compiled)),
        ("File.Move/Copy/Replace", new Regex(@"File\s*\.\s*(Move|Copy|Replace)\s*\(", RegexOptions.Compiled)),
        ("File.Open/OpenWrite (a writable open)", new Regex(@"File\s*\.\s*Open(?!Read\b|Text\b)\w*\s*\(", RegexOptions.Compiled)),
        ("File.SetAttributes/SetLastWriteTime and friends", new Regex(@"File\s*\.\s*Set\w+\s*\(", RegexOptions.Compiled)),
        ("Directory.CreateDirectory/Delete/Move", new Regex(@"Directory\s*\.\s*(Create|Delete|Move)\w*\s*\(", RegexOptions.Compiled)),
        ("a writable FileStream", new Regex(@"new\s+FileStream\s*\((?![^;]*FileAccess\s*\.\s*Read\b)", RegexOptions.Compiled)),
        ("new StreamWriter(...)", new Regex(@"new\s+StreamWriter\s*\(", RegexOptions.Compiled)),
        ("Path.GetTempFileName() (which CREATES a file)", new Regex(@"Path\s*\.\s*GetTempFileName\s*\(", RegexOptions.Compiled)),
        (
            "a FileInfo/DirectoryInfo instance mutator (.Delete/.MoveTo/.CopyTo/.Replace/.Create*)",
            new Regex(
                @"(new\s+(File|Directory)Info\s*\([^;]*\)|\b\w*(File|Directory|Info))\s*(\.\s*\w+\s*)*\.\s*(Delete|MoveTo|CopyTo|Replace|Create\w*|Encrypt|Decrypt)\s*\(",
                RegexOptions.Compiled)),
        ("RandomAccess.Write/SetLength", new Regex(@"RandomAccess\s*\.\s*(Write|SetLength|Flush)\w*\s*\(", RegexOptions.Compiled)),
        ("MemoryMappedFile (which can map a file writable)", new Regex(@"\bMemoryMappedFile\b", RegexOptions.Compiled)),
    ];

    [Fact]
    public void FilesystemMutationSitesInSrc_ExactlyMatchTheGuardedSet()
    {
        var actualSites = SourceFilesInSrc()
            .Where(path => FilesystemMutationShapes.Any(shape => shape.Pattern.IsMatch(ExecutableSourceOf(path))))
            .Select(ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var expectedSites = GuardedFilesystemMutationSiteRelativePaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // EXACT equality, both ways — a new mutation site fails here (fail-closed), and a stale entry
        // for a file that no longer mutates anything fails here too, so the list cannot quietly rot.
        Assert.Equal(expectedSites, actualSites);
    }

    [Theory]
    [MemberData(nameof(NormalizeAndValidatePipelineSites))]
    public void TheNormalizeAndValidatePipeline_ContainsNoFilesystemMutationApiWhatsoever(string relativePath)
    {
        var fullPath = Path.Combine(RepoRoot.FullName, relativePath);

        // Anti-vacuity: a renamed or deleted file would otherwise make this check pass over nothing.
        Assert.True(File.Exists(fullPath), $"Expected '{relativePath}' to exist — update this guard's file list if it moved.");

        var text = ExecutableSourceOf(fullPath);
        foreach (var (description, pattern) in FilesystemMutationShapes)
        {
            Assert.False(
                pattern.IsMatch(text),
                $"'{relativePath}' appears to contain {description} (matched by /{pattern}/). "
                + "normalize_suite's contract is that the SERVER never writes — the host decides "
                + "whether and where to write the canonical YAML. Not even a temp file belongs on "
                + "this path.");
        }
    }

    /// <summary>Adapts <see cref="NormalizeAndValidatePipelineRelativePaths"/> for <see cref="MemberDataAttribute"/>.</summary>
    public static TheoryData<string> NormalizeAndValidatePipelineSites()
    {
        var data = new TheoryData<string>();
        foreach (var path in NormalizeAndValidatePipelineRelativePaths)
        {
            data.Add(path);
        }

        return data;
    }

    /// <summary>
    /// Sanity check for <see cref="StripCommentsAndStringLiterals"/>, because every result above is
    /// only as good as it is. Each case is a way the raw-text version of this guard was wrong.
    /// </summary>
    [Theory]
    // A mutation named only in a comment is not a mutation site (a false POSITIVE on raw text).
    [InlineData("// File.WriteAllText(path, text);", false)]
    [InlineData("/* File.Delete(path); */", false)]
    // …but the same call outside one still is.
    [InlineData("File.WriteAllText(path, text); // writes", true)]
    // A mutation named only in a string literal is not one either.
    [InlineData("Log(\"call File.Delete( to remove it\");", false)]
    // The FileStream lookahead must not be disarmable by a comment (a false NEGATIVE on raw text).
    [InlineData("new FileStream(path, FileMode.Create); // FileAccess.Read elsewhere", true)]
    // …and the genuine read-only construction still passes, including split across lines.
    [InlineData("new FileStream(\n    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);", false)]
    // ReadWrite is a write, and the \b after Read is what keeps that true.
    [InlineData("new FileStream(path, FileMode.Open, FileAccess.ReadWrite);", true)]
    // A '//' inside a string must not swallow the rest of the line.
    [InlineData("var url = \"https://example.test\"; File.Delete(path);", true)]
    public void TheMutationShapes_SeeThroughCommentsAndStringLiterals(string source, bool expectedMatch)
    {
        var executable = StripCommentsAndStringLiterals(source);

        Assert.Equal(
            expectedMatch,
            FilesystemMutationShapes.Any(shape => shape.Pattern.IsMatch(executable)));
    }

    private static string ExecutableSourceOf(string fullPath) =>
        StripCommentsAndStringLiterals(File.ReadAllText(fullPath));

    /// <summary>
    /// Replaces every comment and every string/char literal body with spaces, keeping newlines so
    /// line-oriented patterns and multi-line constructor calls behave unchanged.
    /// </summary>
    /// <remarks>
    /// Not a C# lexer, and does not need to be: it recognises <c>//</c>, <c>/* */</c>, <c>"…"</c>
    /// (with backslash escapes), <c>@"…"</c> (where <c>""</c> is an escaped quote), <c>"""…"""</c>
    /// raw literals, and <c>'…'</c>. Anything it mis-lexes degrades toward blanking MORE text, which
    /// can only cost a pattern a match — never invent one — and
    /// <see cref="TheMutationShapes_SeeThroughCommentsAndStringLiterals"/> pins the cases that
    /// matter.
    /// </remarks>
    private static string StripCommentsAndStringLiterals(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var index = 0;

        // Blanks source[index..end) — newlines kept so line-oriented reading and multi-line
        // constructor calls are unaffected — and leaves index at end.
        void BlankThrough(int end)
        {
            end = Math.Clamp(end, index, source.Length);
            for (; index < end; index++)
            {
                output.Append(source[index] == '\n' ? '\n' : ' ');
            }
        }

        // The index just past `terminator`, or the end of the source if it never closes.
        int PastTerminator(string terminator, int from)
        {
            var at = source.IndexOf(terminator, from, StringComparison.Ordinal);
            return at < 0 ? source.Length : at + terminator.Length;
        }

        while (index < source.Length)
        {
            var c = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (c == '/' && next == '/')
            {
                var end = source.IndexOf('\n', index);
                BlankThrough(end < 0 ? source.Length : end);
            }
            else if (c == '/' && next == '*')
            {
                BlankThrough(PastTerminator("*/", index + 2));
            }
            else if (c == '"' && next == '"' && index + 2 < source.Length && source[index + 2] == '"')
            {
                // Raw string literal. Blanked opening delimiter and all: nothing downstream cares
                // about balanced quotes, and consuming the closing delimiter is what stops the next
                // iteration from reading it as a fresh opener.
                BlankThrough(PastTerminator("\"\"\"", index + 3));
            }
            else if (c == '@' && next == '"')
            {
                index += 2;
                output.Append("  ");

                while (index < source.Length)
                {
                    if (source[index] == '"')
                    {
                        // "" is an escaped quote inside a verbatim literal, not the end of it.
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            BlankThrough(index + 2);
                            continue;
                        }

                        BlankThrough(index + 1);
                        break;
                    }

                    BlankThrough(index + 1);
                }
            }
            else if (c is '"' or '\'')
            {
                var end = index + 1;
                while (end < source.Length && source[end] != c && source[end] != '\n')
                {
                    end += source[end] == '\\' ? 2 : 1;
                }

                BlankThrough(Math.Min(end + 1, source.Length));
            }
            else
            {
                output.Append(c);
                index++;
            }
        }

        return output.ToString();
    }

    private static IEnumerable<string> SourceFilesInSrc() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path));

    /// <summary>Mirrors <see cref="SecretHygieneSourceGuardTests"/>' identically-named helper exactly.</summary>
    private static bool IsBuildOutputPath(string fullPath)
    {
        var relative = Path.GetRelativePath(RepoRoot.FullName, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal)
            || relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string ToRepoRelativeForwardSlashPath(string fullPath) =>
        Path.GetRelativePath(RepoRoot.FullName, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Mirrors <see cref="SecretHygieneSourceGuardTests"/>' <c>RepoRoot</c> exactly.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");
            var repoRoot = testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

            return repoRoot;
        }
    }
}
