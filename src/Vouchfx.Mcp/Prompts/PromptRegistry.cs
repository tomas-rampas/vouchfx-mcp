using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Prompts;

/// <summary>
/// An <see cref="McpServerPrompt"/> whose protocol declaration AND rendered text both come from one
/// embedded <c>Prompts/*.md</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subclassed rather than built with <see cref="McpServerPrompt.Create(Delegate, McpServerPromptCreateOptions)"/>,
/// and that is the design.</b> The delegate overload infers the wire arguments from a C# method's
/// parameter list, which would mean every prompt's arguments existed twice — once in a signature and
/// once in the front matter a human edits — with nothing but a reviewer between them. Overriding
/// <see cref="ProtocolPrompt"/> builds the declaration FROM the parsed file, so `prompts/list` cannot
/// describe an argument the procedure does not use, or omit one it does. See
/// <see cref="PromptDefinition"/>'s header.
/// </para>
/// <para>
/// <b>The two failure modes are deliberately not treated alike.</b> A missing required argument is
/// ordinary caller input and becomes a clean <see cref="McpException"/> — the same protocol-error
/// pattern <c>DiagnosticResourceRegistry</c> uses for an unresolvable resource parameter. A malformed
/// TEMPLATE is a packaging fault in this repository's own embedded content and is deliberately NOT
/// caught: it cannot happen at run time (every prompt is parsed at startup by
/// <see cref="PromptRepository"/>), and swallowing it would turn a build-time bug into a silently
/// truncated procedure in a host's context window.
/// </para>
/// </remarks>
internal sealed class MarkdownPrompt : McpServerPrompt
{
    private readonly PromptDefinition _definition;

    public MarkdownPrompt(PromptDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _definition = definition;
        ProtocolPrompt = new Prompt
        {
            Name = definition.Name,
            Title = definition.Title,
            Description = definition.Description,
            Arguments =
            [
                .. definition.Arguments.Select(argument => new PromptArgument
                {
                    Name = argument.Name,
                    Description = DescribeWithDefault(argument),
                    Required = argument.Required,
                })
            ],
        };
    }

    /// <summary>
    /// The advertised description, with the argument's literal default appended when it has one.
    /// </summary>
    /// <remarks>
    /// <b>The description is the ONLY channel MCP gives this</b> (a code review's finding, and both
    /// reviewers converged on the same fix). <see cref="PromptArgument"/> has <c>Name</c>,
    /// <c>Title</c>, <c>Description</c> and <c>Required</c> — and no <c>default</c> field. Before
    /// this, <see cref="PromptArgumentDefinition.Default"/> reached the RENDER but never the WIRE, so
    /// a host could not show a user what omitting the argument would send, the docs documented a list
    /// <c>prompts/list</c> did not carry, and this type's own remark claimed otherwise.
    /// <para>
    /// Appended as a sentence rather than smuggled into a structured field that does not exist:
    /// the description is prose a host displays, and one more sentence of prose is what a user needs
    /// to see. The literal is the SAME flattened string the render substitutes, so what a host
    /// advertises and what the procedure says cannot differ.
    /// </para>
    /// </remarks>
    private static string DescribeWithDefault(PromptArgumentDefinition argument) =>
        argument.Default is null
            ? argument.Description
            : $"{argument.Description.TrimEnd()} Defaults to: {argument.Default}.";

    /// <inheritdoc />
    public override Prompt ProtocolPrompt { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Empty: this SDK member carries host-supplied primitive metadata, and a prompt served from a
    /// static file has none. Returning an empty list rather than null keeps every consumer's
    /// enumeration total.
    /// </remarks>
    public override IReadOnlyList<object> Metadata => [];

    /// <inheritdoc />
    public override ValueTask<GetPromptResult> GetAsync(
        RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string rendered;
        try
        {
            rendered = _definition.Render(ReadArguments(request.Params?.Arguments));
        }
        catch (PromptArgumentException ex)
        {
            // Caller input. The message already names the prompt and the argument, and is composed
            // entirely from this repository's own strings — no caller text is echoed into it.
            throw new McpException(ex.Message);
        }

        return ValueTask.FromResult(new GetPromptResult
        {
            Description = _definition.Description,
            Messages =
            [
                new PromptMessage
                {
                    // USER, not System: a prompt is an instruction the host is choosing to send on the
                    // user's behalf, and the MCP prompt primitive's own examples use this role. A
                    // System role would also be silently dropped by hosts that reserve it for their
                    // own preamble.
                    Role = Role.User,
                    Content = new TextContentBlock { Text = rendered },
                }
            ],
        });
    }

    /// <summary>
    /// The longest an argument value may be before it is capped — the same bound
    /// <see cref="Vouchfx.Mcp.Resources.ResourceArgumentGuard.MaxArgumentChars"/> applies to the
    /// analogous URI-template surface.
    /// </summary>
    /// <remarks>
    /// <b>Capped rather than left unbounded</b> (a security review's finding), and the inconsistency it
    /// named is the argument for it: this class's ERROR path already refuses to echo caller text at
    /// all, while its success path echoed it into the rendered procedure without limit. One surface,
    /// two postures. A prompt argument is caller text on its way into a model's context window, and a
    /// megabyte of it is not a flow description.
    /// <para>
    /// <b>Capped, not refused</b> — unlike the resource guard, which rejects. A prompt is prose: an
    /// over-long <c>constraints</c> is a host being verbose, not an attack, and truncating it still
    /// yields a usable procedure whereas refusing yields none. The truncation is VISIBLE
    /// (<see cref="TruncationMarker"/>) for the reason every bound in this server is: a silently
    /// shortened instruction is worse than a visibly shortened one.
    /// </para>
    /// </remarks>
    internal const int MaxArgumentValueChars = Vouchfx.Mcp.Resources.ResourceArgumentGuard.MaxArgumentChars;

    /// <summary>Appended to a value this class truncated, so the shortening is never silent.</summary>
    internal const string TruncationMarker = "… (truncated)";

    /// <summary>
    /// Flattens the protocol's untyped argument map to the bounded strings the template substitutes.
    /// </summary>
    /// <remarks>
    /// <b>A non-string JSON value is rendered as its raw text rather than refused.</b> The protocol
    /// types <c>arguments</c> as a free JSON map, so a host may legitimately send a number or a
    /// boolean for something this server treats as text; refusing would make this the strictest party
    /// in an untyped exchange for no benefit. A null or an absent key is "not supplied", which the
    /// template already handles as the blank case. Every value goes through
    /// <see cref="Bound"/> — see <see cref="MaxArgumentValueChars"/>.
    /// </remarks>
    private static Dictionary<string, string?> ReadArguments(IDictionary<string, JsonElement>? arguments)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (arguments is null)
        {
            return values;
        }

        foreach (var (key, value) in arguments)
        {
            values[key] = Bound(Flatten(value));
        }

        return values;
    }

    /// <summary>
    /// Flattens one protocol argument value to the text the template substitutes.
    /// </summary>
    /// <remarks>
    /// <b>A JSON ARRAY becomes a comma-separated list, not its raw JSON.</b> Spec §7.2 types
    /// <c>heal_run</c>'s <c>allowedScopes</c> as a list, so a conforming host sends
    /// <c>["environment","timeouts"]</c> — and rendering that verbatim would put JSON punctuation into
    /// a prose instruction, reading as <c>apply only ["environment","timeouts"]-scoped proposals</c>.
    /// Joining with <c>", "</c> produces exactly the same string the front matter's own list default
    /// flattens to (see <c>PromptDocumentParser.ReadDefault</c>), so the default and a
    /// host-supplied override render identically shaped text — which is what lets one set of
    /// assertions cover both.
    /// <para>
    /// A nested array or object inside the list keeps its raw JSON: it is not something any prompt
    /// argument is typed as, and inventing a flattening for it would be guessing at a shape no host
    /// has a reason to send.
    /// </para>
    /// </remarks>
    private static string? Flatten(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(
                ", ",
                value.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())),
            _ => value.GetRawText(),
        };

    /// <summary>
    /// Folds line breaks and tabs to single spaces, strips every other control character from a
    /// caller-supplied value, and caps it at <see cref="MaxArgumentValueChars"/>, marking the
    /// truncation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control-character strip was missing, and this is the sprint's one caller-text echo that
    /// lacked it</b> (a peer review's finding). A <c>flowDescription</c> containing an ESC or a BEL
    /// reached the rendered procedure verbatim — text a host may print to a terminal. Every other
    /// surface in this server that echoes caller text sanitises it; this one capped and nothing else.
    /// </para>
    /// <para>
    /// <b>Stripped, NOT <c>TextSanitiser.SanitiseForDisplay</c>d</b> — and that choice is the same one
    /// <c>WorkspaceSpecIndexer.CapAndSanitiseWirePath</c> made, for the same reason. That helper
    /// literal-escapes every character outside <c>0x20</c>–<c>0x7E</c>, so a French or Japanese
    /// <c>flowDescription</c> would arrive in the model's context as a wall of <c>\uXXXX</c>. That is
    /// the m2 defect in reverse: it would break legitimate non-ASCII input to guard against a hazard
    /// that only control characters pose. Removing the control characters keeps every printable
    /// character exact.
    /// </para>
    /// <para>
    /// Removed rather than escaped, again matching that helper: an escaped sequence would put text the
    /// caller did not write into their own quoted description.
    /// </para>
    /// <para>
    /// <b>Except line breaks and tabs, which are FOLDED to a space rather than removed</b> (a peer
    /// review's finding, and the correction to remarks that reasoned carefully about non-ASCII while
    /// saying nothing about the control characters a host actually sends). These arguments carry
    /// human PROSE — a <c>constraints</c> value is realistically "max 8 steps\nHTTP and Postgres
    /// only", because that is what a multi-line text box or a heredoc produces. Deleting the newline
    /// welds the last word of one line to the first of the next ("max 8 stepsHTTP and Postgres
    /// only"), which is worse than the hazard being guarded: it silently corrupts legitimate input
    /// into a word that was never written, and the corrupted text then goes into a model's context as
    /// the caller's own stated requirement. <c>\n</c>, <c>\r</c> and <c>\t</c> are the only control
    /// characters that mean "a gap between words" to a writer, so they are the only ones that become
    /// one. A run of them (a <c>\r\n</c>, a blank line, an indent) collapses to a SINGLE space, and a
    /// run at either end contributes nothing — the value is prose being quoted inline, not a document
    /// whose layout is being preserved.
    /// </para>
    /// <para>
    /// Order still matters: fold first, then cap. The fold can only shorten (a run of whitespace
    /// controls becomes one space) or keep length equal, never lengthen, so capping afterwards is
    /// still what actually bounds the result.
    /// </para>
    /// </remarks>
    internal static string? Bound(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var folded = value.Any(char.IsControl) ? Fold(value) : value;

        return folded.Length <= MaxArgumentValueChars
            ? folded
            : folded[..MaxArgumentValueChars] + TruncationMarker;
    }

    /// <summary>
    /// Collapses every run of <c>\n</c>/<c>\r</c>/<c>\t</c> to one space, drops every other control
    /// character, and leaves all printable characters — of any script — exactly as written.
    /// </summary>
    private static string Fold(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value)
        {
            if (c is '\n' or '\r' or '\t')
            {
                // Remembered rather than appended, so a run becomes one space and a trailing run
                // becomes nothing at all.
                pendingSpace = true;
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (pendingSpace)
            {
                // Suppressed at the very start: a leading newline is not a gap between two words.
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Assembles every MCP prompt this server advertises — the prompt analogue of
/// <see cref="Vouchfx.Mcp.Tools.ToolRegistry"/> and <see cref="Vouchfx.Mcp.Resources.ResourceRegistry"/>,
/// and the single list <c>VouchfxMcpServerRegistration.AddVouchfxMcpServer</c> assigns to its
/// <c>PromptCollection</c>.
/// </summary>
public static class PromptRegistry
{
    /// <summary>Creates every catalogued prompt, in <see cref="PromptCatalogue.All"/>'s order.</summary>
    /// <remarks>
    /// <b>This method does NOT run during DI configuration, and an earlier version of this comment
    /// said it did — wrongly, in both of its clauses</b> (a peer review measured it against the IL).
    /// It runs inside the <c>services.Configure&lt;McpServerOptions&gt;</c> callback that
    /// <c>AddVouchfxMcpServer</c> registers, and that callback is invoked when the hosted MCP service
    /// resolves its options — i.e. inside <c>host.RunAsync()</c>, long after <c>Program.cs</c>'s
    /// registration try/catch has returned. So a malformed prompt reaching here would surface as an
    /// unhandled <see cref="TypeInitializationException"/> from inside the transport, naming no file.
    /// <para>
    /// What actually makes it a clean startup failure is the explicit <c>_ = PromptRepository.All;</c>
    /// preflight in <c>Program.cs</c>, beside the pin and diagnostic-catalogue loads. If that
    /// preflight is ever removed, this is not a safety net — it is the thing that was mistaken for one.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<McpServerPrompt> CreateAll() =>
        [.. PromptRepository.All.Select(definition => new MarkdownPrompt(definition))];
}
