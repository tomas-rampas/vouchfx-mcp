using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-03's wire-facing checks for <c>heal_run</c>: the <c>prompts/get</c> round trip, the
/// advertised argument surface, and the advertised-surface cross-check every prompt must pass.
/// </summary>
/// <remarks>
/// <b>The cross-check is the reason this class exists.</b> <c>heal_run</c> shipped its first round
/// without being run through <see cref="Prompts.PromptSurfaceCrossCheck"/> at all — and would have
/// FAILED it, because <c>capture_unmet</c> matches the tool-name shape and is not a tool. A prompt's
/// unit tests can verify its text says what was intended; only this can verify that what it says is
/// real.
/// </remarks>
public class RealHealRunPromptMcpTests
{
    private static readonly string[] NarrowedScopes = ["environment"];

    [Fact]
    public async Task EveryIdentifierAndValue_MatchesTheAdvertisedSurface()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        foreach (var rendered in Prompts.HealRunPromptTests.AllRenderings())
        {
            await Prompts.PromptSurfaceCrossCheck.AssertRenderedPromptMatchesAdvertisedSurface(
                rendered, harness, cts.Token);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsGet_ReturnsTheRenderedProcedureAsAUserMessage()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "heal_run",
            new Dictionary<string, object?> { ["runId"] = "run-42" },
            cancellationToken: cts.Token);

        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);

        var text = Assert.IsType<TextContentBlock>(message.Content).Text;

        Assert.Contains("run-42", text, StringComparison.Ordinal);
        Assert.Contains("diagnose_run", text, StringComparison.Ordinal);
        Assert.DoesNotContain("write_spec", text, StringComparison.Ordinal);

        // The taxonomy prohibition, over the wire. Whitespace-collapsed for the reason
        // PromptTextAssertions records: the body is hand-wrapped markdown.
        Assert.Contains(
            "do not weaken an assertion to force a pass",
            string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Replace("*", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsList_AdvertisesTheDefaultAllowedScopes_InTheArgumentDescription()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);
        var healRun = Assert.Single(prompts, prompt => prompt.Name == "heal_run");

        var allowedScopes = Assert.Single(
            healRun.ProtocolPrompt.Arguments ?? [], argument => argument.Name == "allowedScopes");

        // H2: the default now genuinely reaches the wire. MCP's PromptArgument has no `default` field,
        // so the description is the only channel — and a host that cannot see what omitting the
        // argument will send cannot show a user what it is about to do.
        Assert.NotNull(allowedScopes.Description);
        foreach (var scope in SpecEditScopes.All)
        {
            Assert.Contains(scope, allowedScopes.Description!, StringComparison.Ordinal);
        }

        Assert.Contains("Defaults to:", allowedScopes.Description!, StringComparison.Ordinal);

        // And the advertised literal is the SAME string the render substitutes — one flattened value,
        // not two that could drift.
        var declared = Assert.Single(
            PromptRepository.Get("heal_run").Arguments, argument => argument.Name == "allowedScopes");
        Assert.Contains(declared.Default!, allowedScopes.Description!, StringComparison.Ordinal);

        // The required argument has no default and must not have acquired a sentence claiming one.
        var runId = Assert.Single(
            healRun.ProtocolPrompt.Arguments ?? [], argument => argument.Name == "runId");
        Assert.DoesNotContain("Defaults to:", runId.Description ?? string.Empty, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// H4's safety inversion, asserted over the wire in all three shapes a host can send.
    /// </summary>
    /// <remarks>
    /// The defect: an explicit empty list flowed through to the FULL default, so the most restrictive
    /// request a host could make produced the most permissive instruction — silently. A UI with every
    /// scope checkbox cleared sends exactly this.
    /// </remarks>
    [Fact]
    public async Task AnExplicitlyEmptyScopeList_ForbidsApplyingAnything_RatherThanRestoringTheDefault()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        async Task<string> RenderAsync(Dictionary<string, object?> arguments) =>
            Assert.IsType<TextContentBlock>(
                Assert.Single(
                    (await harness.Client.GetPromptAsync("heal_run", arguments, cancellationToken: cts.Token))
                        .Messages).Content).Text;

        // 1. ABSENT → the default applies.
        var absent = await RenderAsync(new Dictionary<string, object?> { ["runId"] = "run-42" });
        foreach (var scope in SpecEditScopes.All)
        {
            Assert.Contains(scope, absent, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Apply nothing", absent, StringComparison.Ordinal);

        // 2. EXPLICITLY EMPTY → apply nothing. Sent as a real JSON array, which is what a conforming
        //    host sends and what the old code turned into the full default.
        var empty = await RenderAsync(
            new Dictionary<string, object?> { ["runId"] = "run-42", ["allowedScopes"] = Array.Empty<string>() });

        Assert.Contains("Apply nothing", empty, StringComparison.Ordinal);
        Assert.Contains("apply none of them", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Scopes you may apply in this session:** environment", empty, StringComparison.Ordinal);

        // 3. NARROWED → exactly what was asked for.
        var narrowed = await RenderAsync(
            new Dictionary<string, object?> { ["runId"] = "run-42", ["allowedScopes"] = NarrowedScopes });

        Assert.Contains("environment", narrowed, StringComparison.Ordinal);
        Assert.DoesNotContain("Apply nothing", narrowed, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsGet_MissingTheRequiredRunId_IsAProtocolError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.GetPromptAsync(
                "heal_run", new Dictionary<string, object?>(), cancellationToken: cts.Token));

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
