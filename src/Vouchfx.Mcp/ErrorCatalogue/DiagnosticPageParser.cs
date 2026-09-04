namespace Vouchfx.Mcp.ErrorCatalogue;

/// <summary>
/// Parses one <c>docs/errors/&lt;CODE&gt;.md</c> catalogue page's FIXED heading structure (US-S1-05)
/// into a <see cref="DiagnosticPage"/>: an H1 carrying the code itself, then — in any order, but all
/// required — <c>## Title</c>, <c>## Explanation</c>, <c>## Common causes</c>, and <c>## Fixes</c>.
/// </summary>
/// <remarks>
/// Deterministic by construction, and strict on purpose: a page that deviates from this shape (a
/// missing heading, a repeated heading, content before the first heading, a "common cause"/"fix" line
/// that is not a <c>- </c> bullet) throws a <see cref="FormatException"/> rather than silently
/// producing a partial page. This parser is <c>explain_diagnostic</c>'s and the
/// <c>vouchfx-docs:///errors/{code}</c> resource's ONLY view of a page's structure — a page that
/// "mostly" parses would surface as an answer nobody actually wrote.
/// </remarks>
public static class DiagnosticPageParser
{
    private const string TitleHeading = "Title";
    private const string ExplanationHeading = "Explanation";
    private const string CommonCausesHeading = "Common causes";
    private const string FixesHeading = "Fixes";

    private static readonly string[] RequiredHeadingsInOrder =
        [TitleHeading, ExplanationHeading, CommonCausesHeading, FixesHeading];

    /// <summary>Parses <paramref name="rawText"/> into a <see cref="DiagnosticPage"/>.</summary>
    /// <exception cref="FormatException"><paramref name="rawText"/> does not match the fixed heading structure.</exception>
    public static DiagnosticPage Parse(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var (code, bodyStart) = ParseHeading1(lines);
        var sections = SplitIntoHeading2Sections(lines, bodyStart, code);

        return new DiagnosticPage(
            code,
            Title: ParseTitle(sections, code),
            Explanation: ParseExplanation(sections, code),
            CommonCauses: ParseBulletList(sections, CommonCausesHeading, code),
            Fixes: ParseBulletList(sections, FixesHeading, code));
    }

    /// <summary>Finds the page's single <c># CODE</c> heading and returns it plus where its body starts.</summary>
    private static (string Code, int BodyStart) ParseHeading1(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // No separate "## " check needed: a line starting "## " (two hashes) never matches the
            // "# " (hash, space) prefix below either — its second character is '#', not a space —
            // so StartsWith("# ", Ordinal) alone already rejects it. An earlier `|| line.StartsWith("## ")`
            // clause here was therefore unreachable dead code, not an extra guard.
            if (!line.StartsWith("# ", StringComparison.Ordinal))
            {
                throw new FormatException(
                    $"Diagnostic page must open with a '# <CODE>' heading (nothing before it); found '{line}'.");
            }

            return (line[2..].Trim(), i + 1);
        }

        throw new FormatException("Diagnostic page is empty (or blank-only).");
    }

    /// <summary>
    /// Groups every line after the H1 under its nearest preceding <c>## </c> heading, then checks
    /// every required heading in <see cref="RequiredHeadingsInOrder"/> is present exactly once.
    /// </summary>
    private static Dictionary<string, List<string>> SplitIntoHeading2Sections(string[] lines, int start, string code)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;

        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                current = trimmed[3..].Trim();
                if (!sections.TryAdd(current, []))
                {
                    throw new FormatException($"'{code}' repeats the '## {current}' heading.");
                }

                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                throw new FormatException(
                    $"'{code}' has a second top-level '# ' heading ('{trimmed}'); a page carries exactly one.");
            }

            if (current is not null)
            {
                sections[current].Add(line);
            }
            else if (trimmed.Length > 0)
            {
                throw new FormatException($"'{code}' has content before its first '## ' heading: '{trimmed}'.");
            }
        }

        var missing = RequiredHeadingsInOrder.Where(heading => !sections.ContainsKey(heading)).ToArray();
        if (missing.Length > 0)
        {
            throw new FormatException(
                $"'{code}' is missing required heading(s): {string.Join(", ", missing.Select(h => $"## {h}"))}.");
        }

        return sections;
    }

    /// <summary>The Title section is exactly one non-blank line — a heading, not a paragraph.</summary>
    private static string ParseTitle(Dictionary<string, List<string>> sections, string code)
    {
        var nonBlank = sections[TitleHeading].Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (nonBlank.Length != 1)
        {
            throw new FormatException(
                $"'{code}''s '## {TitleHeading}' section must be exactly one line; found {nonBlank.Length}.");
        }

        return nonBlank[0];
    }

    /// <summary>
    /// The Explanation section is one or more paragraphs, joined verbatim (leading/trailing blank
    /// lines trimmed, internal paragraph breaks preserved).
    /// </summary>
    private static string ParseExplanation(Dictionary<string, List<string>> sections, string code)
    {
        var text = string.Join('\n', sections[ExplanationHeading]).Trim();
        if (text.Length == 0)
        {
            throw new FormatException($"'{code}''s '## {ExplanationHeading}' section is empty.");
        }

        return text;
    }

    /// <summary>Common causes / Fixes are both <c>- </c> bullet lists — at least one item, no other prose.</summary>
    private static List<string> ParseBulletList(
        Dictionary<string, List<string>> sections, string heading, string code)
    {
        var items = new List<string>();

        foreach (var line in sections[heading])
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                throw new FormatException(
                    $"'{code}''s '## {heading}' section has a line that is not a '- ' bullet: '{trimmed}'.");
            }

            items.Add(trimmed[2..].Trim());
        }

        if (items.Count == 0)
        {
            throw new FormatException($"'{code}''s '## {heading}' section has no bullet items.");
        }

        return items;
    }
}
