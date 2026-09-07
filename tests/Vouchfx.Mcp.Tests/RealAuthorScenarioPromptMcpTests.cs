using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-02's wire-facing checks: <c>prompts/list</c> advertises <c>author_scenario</c> with its four
/// arguments, and <c>prompts/get</c> returns rendered text — over the real MCP protocol.
/// </summary>
/// <remarks>
/// <para>
/// The prompt's CONTENT is asserted exhaustively against the renderer in
/// <c>Prompts/AuthorScenarioPromptTests</c>; these tests confirm the different thing a wire test can:
/// that the prompt is wired into the server at all, that its declared arguments survive the protocol,
/// and that a host's <c>prompts/get</c> round trip produces a message rather than an error.
/// </para>
/// <para>
/// <b>No process is spawned by anything in this class</b> (or by any other test this story adds) — the
/// harness is the same in-memory paired-stream server every <c>Real*McpTests</c> class uses, and
/// prompt rendering is pure string work over embedded content.
/// </para>
/// </remarks>
public class RealAuthorScenarioPromptMcpTests
{
    [Fact]
    public async Task PromptsList_AdvertisesExactlyTheCataloguedPrompts()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);

        // Fail-closed set equality, the shape this repository uses for every advertised surface: a
        // fifth prompt added anywhere fails here until it is deliberately catalogued. US-S5-03 and
        // US-S5-04 will each extend PromptCatalogue.All, and this assertion is what makes them notice.
        Assert.Equal(
            PromptCatalogue.All.Select(prompt => prompt.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            prompts.Select(prompt => prompt.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.Contains(prompts, prompt => prompt.Name == "author_scenario");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsList_CarriesTheFourArgumentsWithTheirRequirednessAndDescriptions()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);
        var authored = Assert.Single(prompts, prompt => prompt.Name == "author_scenario");
        var arguments = authored.ProtocolPrompt.Arguments;

        Assert.NotNull(arguments);
        Assert.Equal(
            ["flowDescription", "flowId", "specPath", "constraints"],
            arguments!.Select(argument => argument.Name));

        // THE POINT OF THE MARKDOWN-OWNED DECLARATION: what a host sees on the wire is built from the
        // .md front matter, so there is no C# copy of these four names that could drift from it.
        var definition = PromptRepository.Get("author_scenario");
        foreach (var declared in definition.Arguments)
        {
            var advertised = Assert.Single(arguments!, argument => argument.Name == declared.Name);
            Assert.Equal(declared.Required, advertised.Required ?? false);
            Assert.Equal(declared.Description, advertised.Description);
        }

        Assert.False(string.IsNullOrWhiteSpace(authored.ProtocolPrompt.Description));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsGet_ReturnsTheRenderedProcedureAsAUserMessage()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "author_scenario",
            new Dictionary<string, object?> { ["flowDescription"] = "provision a customer" },
            cancellationToken: cts.Token);

        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);

        var text = Assert.IsType<TextContentBlock>(message.Content).Text;

        // The same content the unit tests pin, observed through the protocol — so a serialisation or
        // wiring fault cannot leave those tests green while a host receives nothing usable.
        Assert.Contains("provision a customer", text, StringComparison.Ordinal);
        Assert.Contains("validate_suite", text, StringComparison.Ordinal);
        Assert.DoesNotContain("write_spec", text, StringComparison.Ordinal);

        // The taxonomy rule, over the wire. Whitespace-collapsed for the reason
        // AuthorScenarioPromptTests.AssertContainsPhrase records: the body is hand-wrapped markdown,
        // and a cosmetic re-wrap must not be able to fail an assertion about wording.
        Assert.Contains(
            "do not change the assertion to make it pass",
            string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Replace("*", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsGet_WithEveryOptionalArgument_RendersThemAll()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "author_scenario",
            new Dictionary<string, object?>
            {
                ["flowDescription"] = "provision a customer",
                ["flowId"] = "checkout-v2",
                ["specPath"] = "e2e/provision-customer.e2e.yaml",
                ["constraints"] = "max 8 steps",
            },
            cancellationToken: cts.Token);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;

        Assert.Contains("checkout-v2", text, StringComparison.Ordinal);
        Assert.Contains("e2e/provision-customer.e2e.yaml", text, StringComparison.Ordinal);
        Assert.Contains("max 8 steps", text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsGet_MissingTheRequiredArgument_IsAProtocolErrorWithoutCrashingTheServer()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.GetPromptAsync(
                "author_scenario", new Dictionary<string, object?>(), cancellationToken: cts.Token));

        // The server survives the refusal and keeps serving — the property every unresolvable request
        // in this server is held to.
        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);
        Assert.NotEmpty(prompts);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(18, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsGet_AnUnknownPromptName_IsAProtocolError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.GetPromptAsync(
                "no_such_prompt", cancellationToken: cts.Token));

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// THE check the two BLOCKERs needed: every identifier and every spelled-out argument VALUE in the
    /// rendered procedure is one this server actually advertises or accepts.
    /// </summary>
    /// <remarks>
    /// Replaces a presence-only assertion that could not see either defect — see
    /// <see cref="Prompts.PromptSurfaceCrossCheck"/>'s header for what shipped and why. Swept across
    /// ALL EIGHT optional-argument combinations, because a section block is exactly where a stale
    /// instruction hides from a single-combination check.
    /// </remarks>
    [Fact]
    public async Task EveryIdentifierAndValue_MatchesTheAdvertisedSurface()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        foreach (var rendered in Prompts.AuthorScenarioPromptTests.AllRenderings())
        {
            await Prompts.PromptSurfaceCrossCheck.AssertRenderedPromptMatchesAdvertisedSurface(
                rendered, harness, cts.Token);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The handshake advertises <c>prompts</c>, and its <c>listChanged</c> matches what the tool and
    /// resource capabilities have always reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The capability is "correct by default", which is exactly why nothing asserted it.</b> The
    /// SDK sets it because a <c>PromptCollection</c> is present. A host decides whether to call
    /// <c>prompts/list</c> at all from this flag, so a regression that dropped the collection would
    /// leave every prompt unreachable while every prompt test still passed — those call the methods
    /// directly and never consult the handshake.
    /// </para>
    /// <para>
    /// <b>On <c>listChanged</c>: this test asserts TRUE, and the reviewer's expectation of false was
    /// inverted.</b> MEASURED on this build: <c>tools.listChanged</c>, <c>resources.listChanged</c> and
    /// <c>prompts.listChanged</c> are ALL true — the SDK's uniform default for a populated collection,
    /// unchanged since Sprint 1 and reviewed repeatedly since. It is a mild over-promise for all three
    /// (these collections are fixed at build time, so no <c>list_changed</c> notification is ever
    /// sent, and a subscribing host simply never hears one), but it is the same over-promise
    /// everywhere. Forcing prompts alone to false would make the newest primitive the inconsistent one
    /// and would be a change to shipped behaviour justified by nothing measured. Asserting all three
    /// TOGETHER is what makes this a real guard: if a future SDK bump changes the default, one
    /// assertion catches it for the whole surface rather than for prompts by luck.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHandshake_AdvertisesPrompts_WithTheSameListChangedTheOtherPrimitivesReport()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var capabilities = harness.Client.ServerCapabilities;

        Assert.NotNull(capabilities.Prompts);
        Assert.NotNull(capabilities.Tools);
        Assert.NotNull(capabilities.Resources);

        Assert.True(capabilities.Prompts!.ListChanged);
        Assert.Equal(capabilities.Tools!.ListChanged, capabilities.Prompts.ListChanged);
        Assert.Equal(capabilities.Resources!.ListChanged, capabilities.Prompts.ListChanged);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task AnArgumentValueLongerThanTheCap_IsTruncatedVisiblyRatherThanEchoedWhole()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "author_scenario",
            new Dictionary<string, object?> { ["flowDescription"] = new string('x', 5_000) },
            cancellationToken: cts.Token);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;

        // Bounded, and the shortening is VISIBLE — the posture every other bound in this server takes.
        Assert.DoesNotContain(new string('x', 5_000), text, StringComparison.Ordinal);
        Assert.Contains("(truncated)", text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task AddingPrompts_ChangedNeitherTheToolNorTheResourceSurface()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // This story adds PROMPTS, never tools (the sprint's own exit checklist) — and the resource
        // surface US-S5-01 established must be equally untouched. Asserted here rather than assumed,
        // because a new primitive collection is exactly the kind of change that quietly perturbs the
        // handshake's other collections.
        Assert.Equal(18, (await harness.Client.ListToolsAsync(cancellationToken: cts.Token)).Count);
        Assert.Equal(3, (await harness.Client.ListResourcesAsync(cancellationToken: cts.Token)).Count);
        Assert.Equal(7, (await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token)).Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
