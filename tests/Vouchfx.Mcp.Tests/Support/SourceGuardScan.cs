namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The scanning primitives every SOURCE-LEVEL guard test in this repo needs: locate the repo root,
/// enumerate <c>src/</c>'s real source files, render one repo-relative, and strip comments and
/// string literals so a pattern only ever matches EXECUTABLE code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extracted when the third guard arrived.</b> <see cref="ReadOnlySourceGuardTests"/> and
/// <see cref="SecretHygieneSourceGuardTests"/> each grew their own copy of the repo-root walk and
/// the relative-path rendering, with comments in each saying "mirrors the other exactly" — an
/// arrangement that survives two copies and does not survive three. US-S3-05 added
/// <see cref="RunLockSourceGuardTests"/>, so the primitives moved here rather than being copied a
/// third time. All three guards now delegate;
/// <c>ReadOnlySourceGuardTests.TheMutationShapes_SeeThroughCommentsAndStringLiterals</c> and
/// <c>SecretHygieneSourceGuardTests.ProcessSpawnSitesInSrc_ExactlyMatchTheGuardedSet</c> (an
/// exact-equality assertion over the derived file set, which any change to the enumeration would
/// break) are what prove the move was behaviour-preserving.
/// </para>
/// <para>
/// <b>Nothing here decides anything.</b> WHICH shapes are forbidden, and WHERE they may live,
/// belongs to each guard — that is the part a reviewer must read per invariant. This type only
/// answers "what text should a pattern be run against, and over which files".
/// </para>
/// </remarks>
internal static class SourceGuardScan
{
    /// <summary>The repository root, walked up from the test assembly's output directory.</summary>
    public static DirectoryInfo RepoRoot
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

    /// <summary>Every <c>.cs</c> file under <c>src/</c> that is not build output.</summary>
    public static IEnumerable<string> SourceFilesInSrc() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path));

    /// <summary><paramref name="fullPath"/> rendered relative to <see cref="RepoRoot"/> with forward slashes.</summary>
    public static string ToRepoRelativeForwardSlashPath(string fullPath) =>
        Path.GetRelativePath(RepoRoot.FullName, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary><paramref name="fullPath"/>'s content with comments and string literals blanked.</summary>
    public static string ExecutableSourceOf(string fullPath) =>
        StripCommentsAndStringLiterals(File.ReadAllText(fullPath));

    /// <summary>
    /// <paramref name="fullPath"/>'s content with COMMENTS blanked but string literals — and
    /// crucially their interpolation holes — left intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists, and when to prefer it.</b> <see cref="ExecutableSourceOf"/> blanks a
    /// string literal's entire body, which is right for guards asking "is this API CALLED here" —
    /// a method name inside a message is not a call. It is exactly WRONG for guards asking "does
    /// forbidden material reach a message", because an interpolated string is a literal whose holes
    /// are executable code: <c>$"…{ex.Message}"</c> has its <c>ex.Message</c> blanked along with the
    /// prose around it, so a content guard run over that text passes while the very shape it exists
    /// to forbid sits in the file. A security review found precisely that hole in
    /// <see cref="StructuredLogHygieneSourceGuardTests"/>, whose house style is interpolated
    /// messages.
    /// </para>
    /// <para>
    /// Comments are still blanked, so a shape merely DESCRIBED in a doc comment ("never log
    /// <c>ex.Message</c>") does not trip a content guard — which would otherwise make the rule
    /// impossible to document beside the code it governs.
    /// </para>
    /// <para>
    /// The trade is that ordinary prose inside a literal is now visible to a pattern. That is a
    /// deliberate false-positive risk, carried because the alternative is a false negative on a
    /// security rule; a guard using this should pair it with a fail-closed allow-list of known-benign
    /// matches rather than by weakening the pattern.
    /// </para>
    /// </remarks>
    public static string SourceWithCommentsStrippedOnly(string fullPath) =>
        StripCommentsOnly(File.ReadAllText(fullPath));

    private static bool IsBuildOutputPath(string fullPath)
    {
        var relative = Path.GetRelativePath(RepoRoot.FullName, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal)
            || relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces every comment and every string/char literal body with spaces, keeping newlines so
    /// line-oriented patterns and multi-line constructor calls behave unchanged.
    /// </summary>
    /// <remarks>
    /// Not a C# lexer, and does not need to be: it recognises <c>//</c>, <c>/* */</c>, <c>"…"</c>
    /// (with backslash escapes), <c>@"…"</c> (where <c>""</c> is an escaped quote), <c>"""…"""</c>
    /// raw literals, and <c>'…'</c>. Anything it mis-lexes degrades toward blanking MORE text, which
    /// can only cost a pattern a match — never invent one — and
    /// <c>ReadOnlySourceGuardTests.TheMutationShapes_SeeThroughCommentsAndStringLiterals</c> pins the
    /// cases that matter.
    /// </remarks>
    /// <summary>
    /// Blanks <c>//</c> and <c>/* */</c> comments, leaving every string literal — and therefore every
    /// interpolation hole — intact. See <see cref="SourceWithCommentsStrippedOnly"/> for why.
    /// </summary>
    /// <remarks>
    /// String literals are still SCANNED rather than ignored, because a quote character has to be
    /// tracked to know that a <c>//</c> inside one ("https://…") is not a comment. They are simply
    /// copied through instead of blanked.
    /// </remarks>
    public static string StripCommentsOnly(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var index = 0;

        void CopyThrough(int end)
        {
            end = Math.Clamp(end, index, source.Length);
            for (; index < end; index++)
            {
                output.Append(source[index]);
            }
        }

        void BlankThrough(int end)
        {
            end = Math.Clamp(end, index, source.Length);
            for (; index < end; index++)
            {
                output.Append(source[index] == '\n' ? '\n' : ' ');
            }
        }

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
                CopyThrough(PastTerminator("\"\"\"", index + 3));
            }
            else if (c == '@' && next == '"')
            {
                CopyThrough(index + 2);

                while (index < source.Length)
                {
                    if (source[index] == '"')
                    {
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            CopyThrough(index + 2);
                            continue;
                        }

                        CopyThrough(index + 1);
                        break;
                    }

                    CopyThrough(index + 1);
                }
            }
            else if (c is '"' or '\'')
            {
                var end = index + 1;
                while (end < source.Length && source[end] != c && source[end] != '\n')
                {
                    end += source[end] == '\\' ? 2 : 1;
                }

                CopyThrough(Math.Min(end + 1, source.Length));
            }
            else
            {
                output.Append(c);
                index++;
            }
        }

        return output.ToString();
    }

    public static string StripCommentsAndStringLiterals(string source)
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
}
