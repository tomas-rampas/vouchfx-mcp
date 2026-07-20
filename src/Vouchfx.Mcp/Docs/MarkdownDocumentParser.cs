using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Docs;

/// <summary>
/// Splits raw Markdown text into one <see cref="DocSection"/> per heading — the seam
/// <see cref="DocSearchService"/> scores matches against.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a small, hand-rolled line scanner rather than a full Markdown parser (no new
/// NuGet dependency; REQ-005 asks for exactly this): it only needs to recognise ATX heading lines
/// (<c>#</c> through <c>######</c>, the only heading style either vendored document uses — neither
/// uses Setext-style <c>===</c>/<c>---</c> underlines) and otherwise treat every line as opaque
/// section body text.
/// </para>
/// <para>
/// <b>It DOES need to understand fenced code blocks</b> — this is not optional: both vendored
/// documents contain many YAML/bash comment lines that start with <c>#</c> inside triple-backtick
/// fences (e.g. <c>recipes.md</c>'s worked examples routinely open with <c>```yaml</c> and then a
/// <c># The stubbed payment gateway</c>-style comment). Without fence tracking, EVERY such comment
/// line is misparsed as a heading: it corrupts the section count, produces deep links to anchors
/// that do not exist on the published site, corrupts <see cref="DocSection.HeadingPath"/> ancestry
/// (a code comment becomes a "parent" of whatever real heading follows it), and truncates the real
/// section's body at the phantom heading. <see cref="Parse"/> therefore tracks fence state (opened
/// by a line whose trimmed content starts with 3+ backticks or 3+ tildes, closed by a
/// matching-or-longer fence of the SAME character with no trailing info string — CommonMark §4.5)
/// and never treats ANY line inside an open fence as a heading, regardless of what it starts with.
/// </para>
/// <para>
/// It still does not need to understand tables or any other Markdown construct beyond fences and
/// ATX headings — deep validation of THOSE isn't this parser's job, only correctly carving the
/// document into heading-delimited chunks is.
/// </para>
/// </remarks>
public static class MarkdownDocumentParser
{
    // Mirrors Python-Markdown's toc extension EXACTLY (markdown/extensions/toc.py, IDCOUNT_RE),
    // verified against the current upstream source rather than assumed: MkDocs Material renders
    // this repository's docs via that extension with NO pymdownx.slugs/custom slugify override
    // (see MarkdownSlugifier's remarks), so ITS OWN duplicate-id disambiguation is what
    // vouchfx.io actually applies when two headings on the SAME page produce the same base slug —
    // an UNDERSCORE-separated counter (id_1, id_2, ...), not a hyphen.
    private static readonly Regex IdCountPattern = new(@"^(.*)_([0-9]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <paramref name="markdownText"/> into one <see cref="DocSection"/> per heading line.
    /// Any text before the first heading (e.g. a document title's own lead-in prose, if the title
    /// itself were not a heading — not the case for either vendored document) is discarded: there
    /// is no heading for it to attach to.
    /// </summary>
    public static IReadOnlyList<DocSection> Parse(string docId, string markdownText)
    {
        ArgumentNullException.ThrowIfNull(docId);
        ArgumentNullException.ThrowIfNull(markdownText);

        var lines = markdownText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var sections = new List<DocSection>();
        var ancestry = new List<(int Level, string Text)>();
        var usedSlugs = new HashSet<string>(StringComparer.Ordinal);
        var currentLevel = 0;
        var currentHeading = string.Empty;
        var currentBody = new StringBuilder();

        char? fenceChar = null;
        var fenceLength = 0;

        void FlushCurrentSection()
        {
            if (currentLevel == 0)
            {
                return; // Nothing opened yet — no heading seen so far.
            }

            var headingPath = string.Join(" > ", ancestry.Select(a => a.Text));
            var slug = MakeSlugUnique(MarkdownSlugifier.Slugify(currentHeading), usedSlugs);
            sections.Add(new DocSection(docId, headingPath, currentHeading, currentLevel, slug, currentBody.ToString().Trim()));
            currentBody.Clear();
        }

        foreach (var rawLine in lines)
        {
            if (fenceChar is char openFenceChar)
            {
                // Inside a fence: this line can NEVER be a heading, no matter what it starts
                // with — only check whether it closes the fence.
                if (currentLevel > 0)
                {
                    currentBody.Append(rawLine).Append('\n');
                }

                if (IsClosingFence(rawLine, openFenceChar, fenceLength))
                {
                    fenceChar = null;
                    fenceLength = 0;
                }

                continue;
            }

            if (TryParseOpeningFence(rawLine, out var newFenceChar, out var newFenceLength))
            {
                fenceChar = newFenceChar;
                fenceLength = newFenceLength;

                if (currentLevel > 0)
                {
                    currentBody.Append(rawLine).Append('\n');
                }

                continue;
            }

            if (TryParseHeadingLine(rawLine, out var level, out var text))
            {
                FlushCurrentSection();

                // An ancestry entry at the same level or deeper than the new heading is no longer
                // an ancestor of it (a sibling, or a sibling of an ancestor) — drop it before
                // pushing the new heading.
                while (ancestry.Count > 0 && ancestry[^1].Level >= level)
                {
                    ancestry.RemoveAt(ancestry.Count - 1);
                }

                ancestry.Add((level, text));
                currentLevel = level;
                currentHeading = text;
            }
            else if (currentLevel > 0)
            {
                currentBody.Append(rawLine).Append('\n');
            }
        }

        FlushCurrentSection();
        return sections;
    }

    private static bool TryParseHeadingLine(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        var trimmedStart = line.TrimStart(' ', '\t');

        var hashCount = 0;
        while (hashCount < trimmedStart.Length && trimmedStart[hashCount] == '#')
        {
            hashCount++;
        }

        // ATX headings are 1-6 '#' characters, followed by at least one space, then the heading
        // text (CommonMark §4.2) — "#Word" with no space is a paragraph starting with '#', not a
        // heading.
        if (hashCount is < 1 or > 6 || hashCount >= trimmedStart.Length || trimmedStart[hashCount] != ' ')
        {
            return false;
        }

        var rawText = trimmedStart[(hashCount + 1)..].Trim();

        // CommonMark permits an optional closing sequence of '#' characters (e.g. "## Heading ##")
        // — neither vendored document uses this, but stripping it defensively costs nothing.
        rawText = rawText.TrimEnd('#').TrimEnd();

        if (rawText.Length == 0)
        {
            return false;
        }

        level = hashCount;
        text = rawText;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="line"/> opens a fenced code block: its content, after stripping
    /// leading indentation, starts with a run of 3 or more identical backtick or tilde characters
    /// (CommonMark §4.5). Any info string after the fence (e.g. <c>yaml</c> in ```` ```yaml ````) is
    /// irrelevant to fence detection — the run length and character are all that matter.
    /// </summary>
    private static bool TryParseOpeningFence(string line, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        var trimmed = line.TrimStart(' ', '\t');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var candidate = trimmed[0];
        if (candidate is not ('`' or '~'))
        {
            return false;
        }

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == candidate)
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        fenceChar = candidate;
        fenceLength = count;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="line"/> closes a fence that was opened with <paramref name="openChar"/>
    /// repeated <paramref name="minLength"/> times: per CommonMark §4.5, a closing fence must use the
    /// SAME character, be at least as long as the opening fence, and — unlike the opening fence —
    /// carry NO info string: after stripping indentation and trailing whitespace, the line must
    /// consist of nothing BUT the fence run.
    /// </summary>
    private static bool IsClosingFence(string line, char openChar, int minLength)
    {
        var trimmed = line.Trim(' ', '\t');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == openChar)
        {
            count++;
        }

        return count >= minLength && count == trimmed.Length;
    }

    /// <summary>
    /// Ensures <paramref name="baseSlug"/> is unique within <paramref name="usedSlugsOnThisPage"/>,
    /// disambiguating a repeat exactly the way <c>vouchfx.io</c>'s own renderer does — see this
    /// type's <see cref="IdCountPattern"/> remarks. Ported faithfully from Python-Markdown's own
    /// <c>toc</c> extension <c>unique()</c> function: an id already ending in <c>_&lt;N&gt;</c> has
    /// N incremented; any other repeat gets <c>_1</c> appended; the loop repeats (rather than
    /// stopping at the first candidate) so a THIRD occurrence correctly becomes <c>_2</c>, not
    /// another <c>_1</c>.
    /// </summary>
    private static string MakeSlugUnique(string baseSlug, HashSet<string> usedSlugsOnThisPage)
    {
        var candidate = baseSlug;
        while (usedSlugsOnThisPage.Contains(candidate) || candidate.Length == 0)
        {
            var match = IdCountPattern.Match(candidate);
            candidate = match.Success
                ? $"{match.Groups[1].Value}_{int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + 1}"
                : $"{candidate}_1";
        }

        usedSlugsOnThisPage.Add(candidate);
        return candidate;
    }
}
