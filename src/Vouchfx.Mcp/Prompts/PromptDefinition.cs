namespace Vouchfx.Mcp.Prompts;

// Vouchfx.Mcp.Prompts — the prompt model (Sprint 5 / US-S5-02; spec §7).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE MARKDOWN FILE IS THE SINGLE SOURCE OF TRUTH, INCLUDING FOR THE ARGUMENTS
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Spec §7 says to keep prompts in `Vouchfx.Mcp/Prompts/*.md` "with front-matter for arguments", and
// this model takes that literally: the name, the description and every argument's name/requiredness/
// description are PARSED FROM THE FILE, not declared a second time in C#.
//
// The obvious alternative — the SDK's McpServerPrompt.Create(delegate) overload, which infers the
// wire arguments from a C# method's parameters — was measured against that and rejected. It would put
// the argument list in two places (a method signature and the front matter a human reads), and this
// repository has just spent a story fixing exactly that class of drift in its documentation. Instead
// MarkdownPrompt subclasses McpServerPrompt directly and builds the protocol shape from the parsed
// file, so `prompts/list` cannot describe arguments the prompt text does not use, or omit ones it
// does. (Verified subclassable: McpServerPrompt is abstract with a protected parameterless
// constructor and public abstract GetAsync/ProtocolPrompt/Metadata members.)

/// <summary>One declared argument of a prompt, as its front matter declares it.</summary>
/// <param name="Name">The argument name a host sends in <c>prompts/get</c>.</param>
/// <param name="Description">What a host shows a user when collecting a value. Never blank.</param>
/// <param name="Required">
/// Whether omitting it is an error. A required argument that is missing or blank is refused with
/// <see cref="PromptArgumentException"/> rather than rendered around — a prompt whose subject is
/// absent is not a shorter prompt, it is a broken one.
/// </param>
/// <param name="Default">
/// The value substituted when an OPTIONAL argument is absent or blank, or <see langword="null"/> when
/// omitting it simply renders nothing.
/// </param>
/// <remarks>
/// <b>Defaults exist because spec §7.2 declares one</b> (<c>heal_run</c>'s
/// <c>allowedScopes</c>), and a default that lived only in the prompt's prose would be invisible to
/// <c>prompts/list</c> — a host could not show a user what it was about to send. Declaring it in the
/// front matter puts it on the wire and in the render from one place.
/// <para>
/// A LIST default is written as a YAML list and flattened to a comma-separated string, because the
/// template substitutes text. That flattening is the ONE representation: what a host reads in
/// <c>prompts/list</c> and what the procedure renders are the same string.
/// </para>
/// <para>
/// Meaningless on a REQUIRED argument — one is refused when missing, so a default could never apply —
/// and <see cref="PromptDocumentParser"/> refuses that combination rather than silently ignoring it.
/// </para>
/// </remarks>
public sealed record PromptArgumentDefinition(
    string Name, string Description, bool Required, string? Default = null);

/// <summary>Thrown when a <c>prompts/get</c> omits a required argument.</summary>
/// <remarks>
/// Distinct from <see cref="PromptTemplateException"/> on purpose: that one is a packaging fault in
/// this repository's own embedded content, this one is ordinary caller input arriving over the wire.
/// The two must never be reported the same way — <see cref="PromptRegistry"/> turns this into a clean
/// protocol error and lets the other one escape as the bug it is.
/// </remarks>
public sealed class PromptArgumentException : Exception
{
    /// <inheritdoc />
    public PromptArgumentException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public PromptArgumentException()
    {
    }

    /// <inheritdoc />
    public PromptArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One parsed prompt: its front-matter declaration plus its template body.</summary>
/// <param name="Name">The prompt name a host calls, e.g. <c>author_scenario</c>.</param>
/// <param name="Title">A short human-readable title for a host's prompt picker.</param>
/// <param name="Description">One or two sentences saying what invoking it will do.</param>
/// <param name="Arguments">Declared arguments, in front-matter order — which is the order a host displays.</param>
/// <param name="Body">The template, everything after the closing front-matter fence.</param>
public sealed record PromptDefinition(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<PromptArgumentDefinition> Arguments,
    string Body)
{
    /// <summary>
    /// Renders this prompt's procedure for <paramref name="arguments"/>.
    /// </summary>
    /// <exception cref="PromptArgumentException">A required argument is missing or blank.</exception>
    /// <exception cref="PromptTemplateException">The template is malformed — a packaging fault.</exception>
    /// <remarks>
    /// <b>Unknown arguments are ignored rather than refused.</b> A host that sends an extra key is not
    /// doing anything dangerous — the template only reads names it knows — and refusing would make
    /// this server the strictest party in a protocol whose own <c>prompts/get</c> arguments map is
    /// untyped. What IS refused is a missing required one, because that is the case where rendering
    /// would produce a confidently wrong procedure.
    /// </remarks>
    public string Render(IReadOnlyDictionary<string, string?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Copied rather than mutated: the caller's dictionary is theirs, and applying defaults into it
        // would make a second Render of the same dictionary behave differently from the first.
        var effective = new Dictionary<string, string?>(arguments, StringComparer.Ordinal);

        foreach (var declared in Arguments)
        {
            var present = effective.TryGetValue(declared.Name, out var value);

            if (declared.Required && (!present || string.IsNullOrWhiteSpace(value)))
            {
                throw new PromptArgumentException(
                    $"The '{Name}' prompt requires a non-empty '{declared.Name}' argument.");
            }

            // ── ABSENT ≠ EXPLICITLY EMPTY, and conflating them was a SAFETY INVERSION ────────────
            //
            // A default applies only when the caller said NOTHING about the argument. A caller who
            // sent an explicit empty value said something, and it was the most restrictive thing they
            // could say.
            //
            // The defect this replaces (a code review's finding): `supplied` treated blank as absent,
            // so `allowedScopes: []` — exactly what a UI with every scope checkbox cleared sends —
            // fell through to the FULL default. The most restrictive request produced the most
            // permissive text, silently. That is the worst possible direction for a permission
            // argument to fail in.
            //
            // JSON `null` counts as ABSENT rather than empty: a host that sends null for an optional
            // argument is declining to specify it, which is what a default is for. An empty ARRAY or
            // an empty STRING is a value, and is passed through untouched — the template's own
            // blank-is-falsey rule then selects the prompt's `{{^name}}` branch, which is where the
            // "you may apply nothing" instruction lives. See heal_run.md.
            if (declared.Default is not null && (!present || value is null))
            {
                effective[declared.Name] = declared.Default;
            }
        }

        // The declared set is handed to the renderer so an undeclared placeholder THROWS rather than
        // rendering as nothing — see PromptTemplate.SubstituteValues. Every shipped prompt is parsed
        // at startup, so this can only fire for a template this repository itself got wrong.
        return PromptTemplate.Render(
            Body, effective, Arguments.Select(argument => argument.Name).ToHashSet(StringComparer.Ordinal));
    }
}
