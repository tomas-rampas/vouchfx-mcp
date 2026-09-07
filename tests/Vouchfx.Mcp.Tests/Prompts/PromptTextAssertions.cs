namespace Vouchfx.Mcp.Tests.Prompts;

/// <summary>
/// The text assertions every prompt's render tests share: wrap-tolerant phrase presence, and
/// evasion-tolerant identifier absence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extracted when the second prompt arrived</b> (US-S5-03), rather than reached through
/// <see cref="AuthorScenarioPromptTests"/>'s own members. Four prompts ship in this sprint and every
/// one is held to the same banned-identifier list and the same wrapping rules; a helper living on one
/// prompt's test class would make the other three depend on that class's internals for no reason.
/// </para>
/// <para>
/// Both assertions are deliberately NOT plain substring checks, and each is that way because a plain
/// one measurably failed:
/// <list type="bullet">
/// <item><description>
/// <see cref="AssertPhrasePresent"/> collapses whitespace, because the prompt bodies are hand-wrapped
/// markdown and a phrase long enough to matter falls across a line break. Three assertions failed on
/// first run for exactly that, with the sentences fully present and correct — the wrong sensitivity:
/// brittle to a cosmetic re-wrap, blind to a genuine reword.
/// </description></item>
/// <item><description>
/// <see cref="AssertIdentifierAbsent"/> checks the raw text, a formatting-stripped copy, AND a
/// whitespace-stripped one, because markdown offers several ways to write an identifier that a
/// literal search misses — <c>write\_spec</c>, <c>**write**_spec</c>, and a <c>write_</c>/<c>spec</c>
/// line break all read as one word to a human and to a model.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
internal static class PromptTextAssertions
{
    /// <summary>
    /// Every identifier this sprint retires — banned from EVERY prompt by the sprint-level exit
    /// checklist, plus <c>get_topology</c> from US-S5-01's own scoping.
    /// </summary>
    /// <remarks>
    /// Re-typed from the checklist rather than derived from anything in <c>src/</c>: the point is that
    /// these names must not exist anywhere, so there is no production constant to read them from, and
    /// inventing one would be inventing the thing being banned.
    /// </remarks>
    public static TheoryData<string> BannedIdentifiers() =>
    [
        "write_spec",
        "compile_spec",
        "suggest_scenarios",
        "validate_spec",
        "run_scenario",
        "get_verdict",
        "list_providers",
        "get_topology",
    ];

    /// <summary>
    /// Asserts a multi-word PHRASE appears in the rendered text, immune to markdown line wrapping and
    /// emphasis.
    /// </summary>
    /// <param name="phrase">The phrase, written as a reader would read it.</param>
    /// <param name="rendered">The rendered prompt.</param>
    /// <param name="ignoreCase">
    /// <see langword="false"/> for a rule the AC requires WORD FOR WORD — the taxonomy prohibition is
    /// the case that matters.
    /// </param>
    public static void AssertPhrasePresent(string phrase, string rendered, bool ignoreCase = true)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        Assert.True(
            CollapseWhitespace(StripFormattingNoise(rendered)).Contains(CollapseWhitespace(phrase), comparison),
            $"Expected the rendered prompt to contain the phrase: \"{phrase}\".");
    }

    /// <summary>
    /// Asserts <paramref name="banned"/> appears in none of the three readings of the rendered text.
    /// </summary>
    public static void AssertIdentifierAbsent(string banned, string rendered)
    {
        Assert.DoesNotContain(banned, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(banned, StripFormattingNoise(rendered), StringComparison.OrdinalIgnoreCase);

        // Whitespace-stripped is the strictest form and cannot produce a false negative; it can in
        // principle produce a false POSITIVE by joining unrelated words across a break, which is the
        // safe direction for a ban and has not fired on any real prompt text.
        Assert.DoesNotContain(
            banned,
            new string([.. StripFormattingNoise(rendered).Where(c => !char.IsWhiteSpace(c))]),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the markdown characters that can split an identifier without changing how a reader
    /// sees it — backticks, emphasis asterisks, backslash escapes, and zero-width characters.
    /// </summary>
    /// <remarks>
    /// <b>Underscores are NOT removed</b>, because every banned identifier contains one: stripping
    /// them would make <c>write_spec</c> unfindable and turn the ban into a no-op. What is removed is
    /// what could hide one.
    /// </remarks>
    public static string StripFormattingNoise(string text) =>
        new([.. text.Where(c => c is not ('`' or '*' or '\\' or '​' or '‌' or '‍' or '﻿'))]);

    /// <summary>Collapses every run of whitespace to a single space.</summary>
    public static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
