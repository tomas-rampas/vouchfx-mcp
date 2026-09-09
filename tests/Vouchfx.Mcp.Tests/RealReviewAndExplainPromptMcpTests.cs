using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-04's wire-facing checks for <c>review_spec</c> and <c>explain_failure</c>: the
/// advertised-surface cross-check, and the <c>prompts/get</c> round trips.
/// </summary>
/// <remarks>
/// Both prompts go through <see cref="Prompts.PromptSurfaceCrossCheck"/> across every render shape —
/// the step US-S5-03 omitted for <c>heal_run</c>, which is why that prompt reached review naming a
/// token the check would have rejected.
/// </remarks>
public class RealReviewAndExplainPromptMcpTests
{
    public static TheoryData<string> AllFourPromptNames() =>
        [.. PromptCatalogue.All.Select(prompt => prompt.Name)];

    /// <summary>
    /// Sprint 5's complete prompt set, re-typed from the sprint goal rather than read from
    /// <see cref="PromptCatalogue"/> — so "the four this sprint promised" is asserted against the
    /// requirement, not against the code's own opinion of itself.
    /// </summary>
    private static readonly string[] ExpectedPromptNames =
        ["author_scenario", "explain_failure", "heal_run", "review_spec"];

    [Fact]
    public async Task ReviewSpec_EveryIdentifierAndValue_MatchesTheAdvertisedSurface()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        foreach (var rendered in Prompts.ReviewSpecPromptTests.AllRenderings())
        {
            await Prompts.PromptSurfaceCrossCheck.AssertRenderedPromptMatchesAdvertisedSurface(
                rendered, harness, cts.Token);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainFailure_EveryIdentifierAndValue_MatchesTheAdvertisedSurface()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        foreach (var rendered in Prompts.ExplainFailurePromptTests.AllRenderings())
        {
            await Prompts.PromptSurfaceCrossCheck.AssertRenderedPromptMatchesAdvertisedSurface(
                rendered, harness, cts.Token);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PromptsList_AdvertisesAllFourPrompts()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);

        Assert.Equal(
            ExpectedPromptNames,
            prompts.Select(prompt => prompt.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ReviewSpec_PromptsGet_ReturnsTheRenderedChecklistProcedure()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "review_spec",
            new Dictionary<string, object?> { ["path"] = "e2e/provision-customer.e2e.yaml" },
            cancellationToken: cts.Token);

        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);

        var text = Assert.IsType<TextContentBlock>(message.Content).Text;

        Assert.Contains("e2e/provision-customer.e2e.yaml", text, StringComparison.Ordinal);
        Assert.Contains("plan_coverage", text, StringComparison.Ordinal);
        Assert.DoesNotContain("get_topology", text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainFailure_PromptsGet_ReturnsTheRenderedExplanationProcedure()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "explain_failure",
            new Dictionary<string, object?> { ["runId"] = "run-42", ["stepId"] = "check-balance" },
            cancellationToken: cts.Token);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;

        Assert.Contains("run-42", text, StringComparison.Ordinal);
        Assert.Contains("check-balance", text, StringComparison.Ordinal);
        Assert.Contains("200 words", text, StringComparison.Ordinal);

        // The taxonomy vocabulary a newcomer should learn — and never the engine's wire spellings.
        Assert.Contains("EnvironmentError", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ENV_ERROR", text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [MemberData(nameof(AllFourPromptNames))]
    public async Task EveryPrompt_RefusesAGetWithNoArgumentsAtAll(string promptName)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // Every one of the four has at least one required argument, so an argument-free get is a
        // protocol error for all of them — and the server keeps serving afterwards.
        await Assert.ThrowsAnyAsync<McpException>(
            async () => await harness.Client.GetPromptAsync(
                promptName, new Dictionary<string, object?>(), cancellationToken: cts.Token));

        Assert.Equal(
            PromptCatalogue.All.Count,
            (await harness.Client.ListPromptsAsync(cancellationToken: cts.Token)).Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// N2: an explicit JSON <c>null</c> for a defaulted argument means "not specified", so the default
    /// applies.
    /// </summary>
    /// <remarks>
    /// The complement of H4's fix, and the boundary that fix drew: an empty ARRAY is a value (apply
    /// nothing), a <c>null</c> is a declined choice (apply the default). Both are reachable from a
    /// conforming host, they mean opposite things, and only a wire test can prove this server
    /// distinguishes them where a host actually sits.
    /// </remarks>
    [Fact]
    public async Task AnExplicitJsonNullForADefaultedArgument_TakesTheDefault()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.GetPromptAsync(
            "heal_run",
            new Dictionary<string, object?> { ["runId"] = "run-42", ["allowedScopes"] = null },
            cancellationToken: cts.Token);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;

        foreach (var scope in Vouchfx.Mcp.Diagnosis.SpecEditScopes.All)
        {
            Assert.Contains(scope, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Apply nothing", text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
