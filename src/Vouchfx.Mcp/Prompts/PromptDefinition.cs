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
public sealed record PromptArgumentDefinition(string Name, string Description, bool Required);

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

        foreach (var declared in Arguments)
        {
            if (declared.Required
                && (!arguments.TryGetValue(declared.Name, out var value) || string.IsNullOrWhiteSpace(value)))
            {
                throw new PromptArgumentException(
                    $"The '{Name}' prompt requires a non-empty '{declared.Name}' argument.");
            }
        }

        return PromptTemplate.Render(Body, arguments);
    }
}
