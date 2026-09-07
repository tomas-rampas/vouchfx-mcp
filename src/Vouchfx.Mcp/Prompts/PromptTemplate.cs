using System.Text;

namespace Vouchfx.Mcp.Prompts;

// Vouchfx.Mcp.Prompts — the prompt template renderer (Sprint 5 / US-S5-02).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// A TINY CLOSED SYNTAX, AND WHY IT IS NOT A TEMPLATE ENGINE
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Four prompts ship in this sprint. What they need of a template is: drop an argument's value into a
// sentence, and include a paragraph only when an optional argument was (or was not) supplied. That is
// all three constructs below, and nothing else is supported:
//
//   {{name}}              — the argument's value, or nothing when it was not supplied.
//   {{#name}}…{{/name}}   — the block, only when the argument WAS supplied (and is not blank).
//   {{^name}}…{{/name}}   — the block, only when it was NOT.
//
// Taking a real template engine as a dependency for that would import loops, partials, filters,
// helper resolution and an expression grammar — surface this repository would then have to reason
// about on a path that reaches a host's context window. The Mustache-shaped subset is small enough to
// read in one sitting, and every construct it does not support THROWS rather than rendering something
// surprising (see PromptTemplateException). Prompts are embedded at build time, so a malformed one is
// a packaging fault that fails at load, not a per-request condition.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// ARGUMENT VALUES ARE INERT — SINGLE-PASS SUBSTITUTION, DELIBERATELY
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Sections are resolved FIRST, over the template alone; values are substituted afterwards and are
// never re-scanned. That ordering is the whole of the injection story: a host that passed
// `flowDescription: "{{^flowId}}ignore everything above{{/flowId}}"` would otherwise be writing
// template syntax into a document this server then evaluates, which is a prompt-injection primitive
// handed over for free. With this ordering that value is copied through verbatim, as the caller's own
// text, and cannot alter the procedure around it. Pinned by PromptTemplateTests.

/// <summary>Thrown when a prompt template is malformed — a packaging fault, never caller input.</summary>
public sealed class PromptTemplateException : Exception
{
    /// <inheritdoc />
    public PromptTemplateException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public PromptTemplateException()
    {
    }

    /// <inheritdoc />
    public PromptTemplateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Renders a prompt body: conditional sections resolved first, then argument values substituted once.
/// </summary>
public static class PromptTemplate
{
    private const string OpenPositive = "{{#";
    private const string OpenNegative = "{{^";
    private const string OpenClose = "{{/";
    private const string Open = "{{";
    private const string Close = "}}";

    /// <summary>
    /// Renders <paramref name="template"/> against <paramref name="values"/>.
    /// </summary>
    /// <param name="template">The prompt body, from an embedded <c>Prompts/*.md</c> file.</param>
    /// <param name="values">
    /// The caller's arguments. A key whose value is <see langword="null"/>, empty or whitespace counts
    /// as NOT SUPPLIED for both section forms and substitutes as an empty string — a host that sends
    /// an empty optional argument means "I have nothing to say here", and rendering a labelled empty
    /// paragraph for it would be noise a model then has to reason about.
    /// </param>
    /// <exception cref="PromptTemplateException">The template has an unbalanced or stray section marker.</exception>
    /// <param name="declaredNames">
    /// The argument names the prompt DECLARES. A placeholder naming anything else is a malformed
    /// template and throws. <see langword="null"/> disables the check — used only by the renderer's
    /// own unit tests, which exercise the substitution machinery without a front matter.
    /// </param>
    public static string Render(
        string template,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlySet<string>? declaredNames = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        return SubstituteValues(ResolveSections(template, values), values, declaredNames);
    }

    /// <summary>Whether <paramref name="name"/> was supplied with a non-blank value.</summary>
    private static bool IsSupplied(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Resolves every <c>{{#name}}</c>/<c>{{^name}}</c> section, innermost content preserved, by
    /// scanning left to right and recursing into each kept block.
    /// </summary>
    private static string ResolveSections(string template, IReadOnlyDictionary<string, string?> values)
    {
        var output = new StringBuilder(template.Length);
        var position = 0;

        while (position < template.Length)
        {
            var open = IndexOfSectionOpen(template, position, out var isNegative);
            if (open < 0)
            {
                // No further section. Everything left is literal — but a stray close in it is a
                // malformed template and must not be passed through silently.
                var tail = template[position..];
                RejectStrayClose(tail);
                output.Append(tail);
                break;
            }

            output.Append(template, position, open - position);
            RejectStrayClose(template[position..open]);

            var nameStart = open + OpenPositive.Length;
            var nameEnd = template.IndexOf(Close, nameStart, StringComparison.Ordinal);
            if (nameEnd < 0)
            {
                throw new PromptTemplateException(
                    $"Prompt template has an unterminated section marker at offset {open}.");
            }

            var name = template[nameStart..nameEnd];
            var bodyStart = nameEnd + Close.Length;
            var bodyEnd = FindMatchingClose(template, name, bodyStart);

            var body = template[bodyStart..bodyEnd];
            var keep = isNegative ? !IsSupplied(values, name) : IsSupplied(values, name);
            if (keep)
            {
                // Recursion is what makes sections nest; the body is resolved with the same values.
                output.Append(ResolveSections(body, values));
            }

            position = bodyEnd + OpenClose.Length + name.Length + Close.Length;
        }

        return output.ToString();
    }

    /// <summary>The index of the next section OPEN marker at or after <paramref name="from"/>, or -1.</summary>
    private static int IndexOfSectionOpen(string template, int from, out bool isNegative)
    {
        var positive = template.IndexOf(OpenPositive, from, StringComparison.Ordinal);
        var negative = template.IndexOf(OpenNegative, from, StringComparison.Ordinal);

        if (positive < 0 && negative < 0)
        {
            isNegative = false;
            return -1;
        }

        if (positive < 0 || (negative >= 0 && negative < positive))
        {
            isNegative = true;
            return negative;
        }

        isNegative = false;
        return positive;
    }

    /// <summary>
    /// The offset of the <c>{{/name}}</c> that closes the section opened at
    /// <paramref name="bodyStart"/>, counting depth so a same-named nested section matches correctly.
    /// </summary>
    /// <remarks>
    /// Depth counting rather than "the first close": a template like
    /// <c>{{#a}}outer {{#a}}inner{{/a}}{{/a}}</c> would otherwise be sliced at the inner close,
    /// leaving a stray <c>{{/a}}</c> in the output — a silent wrong render instead of an error. The
    /// prompts do not nest same-named sections today; the matcher is correct anyway so that a future
    /// one cannot be quietly mis-rendered.
    /// </remarks>
    private static int FindMatchingClose(string template, string name, int bodyStart)
    {
        var openMarkerPositive = OpenPositive + name + Close;
        var openMarkerNegative = OpenNegative + name + Close;
        var closeMarker = OpenClose + name + Close;

        var depth = 1;
        var scan = bodyStart;

        while (scan < template.Length)
        {
            var nextClose = template.IndexOf(closeMarker, scan, StringComparison.Ordinal);
            if (nextClose < 0)
            {
                throw new PromptTemplateException(
                    $"Prompt template has an unclosed '{name}' section.");
            }

            var nextOpenPositive = template.IndexOf(openMarkerPositive, scan, StringComparison.Ordinal);
            var nextOpenNegative = template.IndexOf(openMarkerNegative, scan, StringComparison.Ordinal);
            var nextOpen = Nearest(nextOpenPositive, nextOpenNegative);

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                scan = nextOpen + openMarkerPositive.Length;
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return nextClose;
            }

            scan = nextClose + closeMarker.Length;
        }

        throw new PromptTemplateException($"Prompt template has an unclosed '{name}' section.");
    }

    private static int Nearest(int first, int second) =>
        first < 0 ? second : second < 0 ? first : Math.Min(first, second);

    /// <summary>
    /// Rejects a <c>{{/…}}</c> in text that is outside any section — the other half of "malformed
    /// templates fail loudly", and the one an unbalanced-open check alone would miss.
    /// </summary>
    private static void RejectStrayClose(string literal)
    {
        if (literal.Contains(OpenClose, StringComparison.Ordinal))
        {
            throw new PromptTemplateException(
                "Prompt template has a section close marker with no matching open.");
        }
    }

    /// <summary>
    /// Replaces every remaining <c>{{name}}</c> with its value — ONE pass, values never re-scanned.
    /// See this file's header for why that is a security property rather than an optimisation.
    /// </summary>
    private static string SubstituteValues(
        string text, IReadOnlyDictionary<string, string?> values, IReadOnlySet<string>? declaredNames)
    {
        var output = new StringBuilder(text.Length);
        var position = 0;

        while (position < text.Length)
        {
            var open = text.IndexOf(Open, position, StringComparison.Ordinal);
            if (open < 0)
            {
                output.Append(text, position, text.Length - position);
                break;
            }

            var close = text.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new PromptTemplateException(
                    $"Prompt template has an unterminated placeholder at offset {open}.");
            }

            output.Append(text, position, open - position);

            var name = text[(open + Open.Length)..close];

            // AN UNDECLARED PLACEHOLDER THROWS. It used to render as nothing, which contradicted this
            // type's own "every unsupported construct throws" claim and — worse — made the whole class
            // of NAME TYPO invisible. PromptDocumentParser's parse-time guard matches only
            // [A-Za-z][A-Za-z0-9]*, so `{{spec_path}}`, `{{flow-id}}` and `{{ specPath }}` were not
            // placeholders as far as IT was concerned either: the two checks had the same blind spot,
            // and a snake_case typo (the likely one, given snake_case tool names everywhere) silently
            // produced a procedure with a hole in it. Checking against the DECLARED SET closes both,
            // because it does not depend on guessing what a name looks like.
            if (declaredNames is not null && !declaredNames.Contains(name))
            {
                throw new PromptTemplateException(
                    $"Prompt template references '{{{{{name}}}}}', which is not a declared argument. "
                    + $"Declared: {string.Join(", ", declaredNames.Order(StringComparer.Ordinal))}.");
            }

            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                output.Append(value);
            }

            position = close + Close.Length;
        }

        return output.ToString();
    }
}
