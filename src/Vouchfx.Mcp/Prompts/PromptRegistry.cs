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
                    Description = argument.Description,
                    Required = argument.Required,
                })
            ],
        };
    }

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
            values[key] = Bound(value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => value.GetString(),
                _ => value.GetRawText(),
            });
        }

        return values;
    }

    /// <summary>Caps a value at <see cref="MaxArgumentValueChars"/>, marking the truncation.</summary>
    internal static string? Bound(string? value) =>
        value is null || value.Length <= MaxArgumentValueChars
            ? value
            : value[..MaxArgumentValueChars] + TruncationMarker;
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
