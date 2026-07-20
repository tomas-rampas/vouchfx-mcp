using System.Text;

namespace Vouchfx.Mcp.Docs;

/// <summary>
/// Reproduces, exactly, the heading-anchor slug that <c>vouchfx.io</c> generates for a Markdown
/// heading — so a <see cref="DocSearchService"/> deep link's <c>#anchor</c> always lands on the
/// real section, never a near-miss.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exact algorithm:</b> <c>vouchfx.io</c> is built with MkDocs Material, using
/// Python-Markdown's built-in <c>toc</c> extension (<c>permalink: true</c> in <c>mkdocs.yml</c>) —
/// crucially, WITHOUT <c>pymdownx.slugs</c> or a custom <c>slugify:</c> override configured, so
/// every heading's anchor <c>id</c> comes from that extension's own DEFAULT slugify function:
/// </para>
/// <code>
/// def slugify(value, separator, unicode=False):
///     if not unicode:
///         value = unicodedata.normalize('NFKD', value)
///         value = value.encode('ascii', 'ignore').decode('ascii')
///     value = re.sub(r'[^\w\s-]', '', value).strip().lower()
///     return re.sub(rf'[{separator}\s]+', separator, value)
/// </code>
/// <para>
/// Verified against the LIVE site (not just read off the config) for three representative
/// headings, covering a dotted step-type name (backticks and a period, both non-word characters
/// that get stripped by the SAME step) and a heading containing a colon:
/// </para>
/// <list type="bullet">
/// <item><description><c>Common step fields</c> -&gt; <c>common-step-fields</c>
/// (<c>https://vouchfx.io/language-reference/#common-step-fields</c>).</description></item>
/// <item><description>the heading rendered from <c>### `http.rest`</c> -&gt; <c>httprest</c>
/// (<c>https://vouchfx.io/language-reference/#httprest</c>) — confirms inline code-span
/// backticks AND the <c>.</c> in a dotted step-type name are both stripped, not just
/// whitespace.</description></item>
/// <item><description><c>Engine-owned polling with verifyMode: RETRY</c> -&gt;
/// <c>engine-owned-polling-with-verifymode-retry</c>
/// (<c>https://vouchfx.io/recipes/#engine-owned-polling-with-verifymode-retry</c>) — confirms the
/// colon is stripped (not replaced with a hyphen) and the existing hyphen in "Engine-owned" is
/// preserved.</description></item>
/// </list>
/// <para>
/// <b>Why feeding the RAW Markdown heading line works, with no separate "strip inline
/// markdown" pass:</b> Python-Markdown's toc extension slugifies the heading's RENDERED text (so
/// backticks become <c>&lt;code&gt;</c> tags and vanish, asterisks become <c>&lt;strong&gt;</c>/
/// <c>&lt;em&gt;</c> and vanish, etc.) — but every one of those Markdown syntax characters
/// (backtick, asterisk, underscore-as-emphasis, colon, parentheses, …) is ALSO not a word
/// character, not whitespace, and not a hyphen, so the SAME <c>[^\w\s-]</c> removal this method
/// implements strips them regardless of whether they were "real" punctuation or Markdown syntax.
/// Feeding the raw heading source text (with its <c>#</c> markers already removed) therefore
/// produces the identical result to slugifying the rendered text, for every heading these two
/// vendored documents actually contain (none of them use underscore-emphasis, the one Markdown
/// construct that could ambiguously collide with a literal underscore).
/// </para>
/// </remarks>
public static class MarkdownSlugifier
{
    public static string Slugify(string headingText)
    {
        ArgumentNullException.ThrowIfNull(headingText);

        // Step 1: NFKD-normalise, then drop every character outside ASCII — mirrors Python's
        // `unicodedata.normalize('NFKD', value).encode('ascii', 'ignore').decode('ascii')`.
        var normalized = headingText.Normalize(NormalizationForm.FormKD);
        var asciiOnly = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (c < 128)
            {
                asciiOnly.Append(c);
            }
        }

        // Step 2: drop everything that is not a word character, whitespace, or hyphen — mirrors
        // Python's `re.sub(r'[^\w\s-]', '', value)`. At this point every remaining character is
        // ASCII, so char.IsLetterOrDigit here is equivalent to Python's \w over an ASCII string.
        var stripped = new StringBuilder(asciiOnly.Length);
        foreach (var c in asciiOnly.ToString())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-' || char.IsWhiteSpace(c))
            {
                stripped.Append(c);
            }
        }

        // Step 3: trim and lower-case — mirrors Python's `.strip().lower()`.
        var lowered = stripped.ToString().Trim().ToLowerInvariant();

        // Step 4: collapse every run of hyphens/whitespace into a single hyphen — mirrors Python's
        // `re.sub(rf'[{separator}\s]+', separator, value)` with the default separator '-'.
        var result = new StringBuilder(lowered.Length);
        var previousWasSeparator = false;
        foreach (var c in lowered)
        {
            if (c is '-' || char.IsWhiteSpace(c))
            {
                if (!previousWasSeparator)
                {
                    result.Append('-');
                    previousWasSeparator = true;
                }
            }
            else
            {
                result.Append(c);
                previousWasSeparator = false;
            }
        }

        return result.ToString();
    }
}
