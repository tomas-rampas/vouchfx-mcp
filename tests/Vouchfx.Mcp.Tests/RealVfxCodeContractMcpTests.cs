using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S1-04's wire-level contract goldens: every tool's SUCCESS shape and at least one ERROR shape,
/// asserted at the MCP boundary a real host sees, after the migration from ad-hoc <c>kind</c>
/// strings to stable <c>VFX-E-####</c>/<c>VFX-D-####</c> codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these live in one class rather than being scattered into the per-tool <c>Real*McpTests</c>
/// files:</b> the property under test is a CROSS-tool invariant — "every error a tool HANDLER in
/// this server returns is a single <see cref="Vouchfx.Mcp.Contracts.VfxError"/> JSON object carrying
/// a catalogued code, and every diagnostic stays data on a successful call". Scoped to handler-minted
/// errors deliberately: a call that fails the MCP SDK's own argument binding (a missing required
/// parameter, a type mismatch) is rejected BEFORE any handler runs and surfaces as the SDK's plain-text
/// error, which this server neither produces nor can reshape. Split across nine files, a tool added
/// later simply would not appear anywhere and nothing would notice; gathered here, the gap is
/// visible in one place. Per-tool behavioural detail (which is not this class's job) stays in the
/// existing <c>Real*McpTests</c> classes, which are extended in place rather than duplicated.
/// </para>
/// <para>
/// <b>The classification rule these goldens pin</b> (spec §4.4): a <c>VFX-D-</c> code means the
/// pipeline DETERMINED something about the suite/run and reports it as data on a successful call
/// (<c>isError: false</c>); a <c>VFX-E-</c> code means the call itself could not be performed and
/// the answer was never determined (<c>isError: true</c>). <c>search_docs</c> is the one tool with
/// no error shape at all — by design, not by omission — and its golden asserts exactly that.
/// </para>
/// </remarks>
public class RealVfxCodeContractMcpTests
{
    // ── Story Gherkin (1): run_suite's suite-invalid payload is a DIAGNOSTIC, not an error ─────

    [Fact]
    public async Task RunSuite_SchemaInvalidSuite_IsErrorFalseAndCarriesSchemaValidationDiagnosticCode()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await CallAsync(harness, "run_suite", new() { ["path"] = FixturePath("bad-suite.e2e.yaml") }, cts.Token);

        // The precedent this story must not break: an MCP client keying off isError never sees a
        // schema-invalid suite as a tool failure.
        Assert.False(result.IsError ?? false);

        var payload = Structured(result);
        Assert.Equal("VFX-D-1100", payload.GetProperty("code").GetString());

        var errors = payload.GetProperty("validation").GetProperty("errors").EnumerateArray().ToArray();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => AssertInRange(e.GetProperty("code").GetString(), 1100, 1299));
        Assert.Contains(errors, e => e.GetProperty("code").GetString() == "VFX-D-1101");

        Assert.Equal(0, runner.InvocationCount);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Story Gherkin (2): plan_coverage never fails on a coverage gap ─────────────────────────

    [Fact]
    public async Task PlanCoverage_CoverageGap_IsErrorFalseAndTheGapIsData()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pinVersion = CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version);
        var cli = FakeVouchfxCli.WithPlanHandler(
            pinVersion,
            _ => CliInvocationResult.Completed(0, GapReportJson, string.Empty));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var result = await CallAsync(harness, "plan_coverage", new() { ["path"] = "suites/" }, cts.Token);

        Assert.False(result.IsError ?? false);

        var finding = Assert.Single(Structured(result).GetProperty("findings").EnumerateArray());

        // The gap is DATA. Its `kind` is the ENGINE's own finding taxonomy, relayed verbatim from
        // `vouchfx plan --json` — deliberately NOT migrated to a VFX code by US-S1-04, because
        // rewriting a value the engine minted would break this repository's governing invariant
        // ("CLI and MCP must not drift"). Only kinds this SERVER mints were migrated.
        Assert.Equal("dependency-missing-step-type", finding.GetProperty("kind").GetString());
        Assert.Equal("orders-db", finding.GetProperty("target").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Story Gherkin (3): an unknown step type is exactly VFX-D-1201 ──────────────────────────

    [Fact]
    public async Task ValidateSuite_UnknownStepType_CodeIsExactlyVfxD1201()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallAsync(harness, "validate_suite", new() { ["path"] = FixturePath("bad-suite.e2e.yaml") }, cts.Token);

        Assert.False(result.IsError ?? false);
        var errors = Structured(result).GetProperty("errors").EnumerateArray().ToArray();

        var unknownType = Assert.Single(errors, e => e.GetProperty("instancePath").GetString() == "/steps/0/type");

        // Exactly 1201 — spec §5.5 names this code, and Sprint 2's semantic-rules work builds on
        // it. A near-miss (1202, or a fresh code for the same finding) is the specific failure this
        // assertion exists to catch.
        Assert.Equal("VFX-D-1201", unknownType.GetProperty("code").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Story Gherkin (4): an unrecoverable call failure is a VfxError, not a diagnostic ───────

    [Fact]
    public async Task ValidateSuite_NonexistentPath_IsErrorTrueWithASingleVfxErrorObject()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        var result = await CallAsync(harness, "validate_suite", new() { ["path"] = missingPath }, cts.Token);

        Assert.True(result.IsError);

        var error = SingleVfxError(result);
        AssertInRange(error.GetProperty("code").GetString(), 1000, 1199);
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // The server is unharmed by a failed call. The count moves with every tool added — see
        // McpServerSkeletonTests.ListTools_ReturnsExactlyTheSeventeenAdvertisedTools, which is the
        // authoritative lock; this one only needs "still serving everything".
        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(17, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Per-tool ERROR goldens ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateSuite_UncPath_ErrorGoldenIsPathOutsideWorkspace()
    {
        await AssertErrorCodeAsync(
            "validate_suite",
            new() { ["path"] = @"\\attacker-host\share\suite.e2e.yaml" },
            expectedCode: "VFX-E-1001",
            expectedRetryable: false);
    }

    [Fact]
    public async Task RunSuite_PathBeginningWithDash_ErrorGoldenIsInvalidToolArgument()
    {
        await AssertErrorCodeAsync(
            "run_suite",
            new() { ["path"] = "--version" },
            expectedCode: "VFX-E-1006",
            expectedRetryable: false);
    }

    [Fact]
    public async Task RunSuite_CliNotFound_ErrorGoldenIsEngineCliUnavailable()
    {
        await AssertErrorCodeAsync(
            "run_suite",
            new() { ["path"] = FixturePath("good-suite.e2e.yaml") },
            expectedCode: "VFX-E-1401",
            expectedRetryable: false,
            cli: FakeVouchfxCli.NotFound());
    }

    [Fact]
    public async Task PlanCoverage_CliNotFound_ErrorGoldenIsEngineCliUnavailable()
    {
        await AssertErrorCodeAsync(
            "plan_coverage",
            new() { ["path"] = "suites/" },
            expectedCode: "VFX-E-1401",
            expectedRetryable: false,
            cli: FakeVouchfxCli.NotFound());
    }

    [Fact]
    public async Task ScaffoldSuite_EmptySteps_ErrorGoldenIsInvalidToolArgument()
    {
        await AssertErrorCodeAsync(
            "scaffold_suite",
            new() { ["steps"] = Array.Empty<object>() },
            expectedCode: "VFX-E-1006",
            expectedRetryable: false);
    }

    [Fact]
    public async Task ListStepTypes_CliNotFound_ErrorGoldenIsEngineCliUnavailable()
    {
        await AssertErrorCodeAsync(
            "list_step_types",
            arguments: null,
            expectedCode: "VFX-E-1401",
            expectedRetryable: false,
            cli: FakeVouchfxCli.NotFound());
    }

    [Fact]
    public async Task DescribeStepType_UnknownType_ErrorGoldenIsStepTypeNotInCatalogue()
    {
        await AssertErrorCodeAsync(
            "describe_step_type",
            new() { ["type"] = "nope.nope" },
            expectedCode: "VFX-E-1250",
            expectedRetryable: false);
    }

    [Fact]
    public async Task ExplainRun_NoRunThisSession_ErrorGoldenIsNoRunToExplain()
    {
        await AssertErrorCodeAsync(
            "explain_run",
            arguments: null,
            expectedCode: "VFX-E-1601",
            expectedRetryable: false);
    }

    [Fact]
    public async Task DiagnoseRun_NoRunThisSession_ErrorGoldenIsNoRunToExplain()
    {
        await AssertErrorCodeAsync(
            "diagnose_run",
            arguments: null,
            expectedCode: "VFX-E-1601",
            expectedRetryable: false);
    }

    [Fact]
    public async Task ExplainDiagnostic_UnknownCode_ErrorGoldenIsUnknownDiagnosticCode()
    {
        await AssertErrorCodeAsync(
            "explain_diagnostic",
            new() { ["code"] = "VFX-E-1850" },
            expectedCode: "VFX-E-1903",
            expectedRetryable: false);
    }

    // ── search_docs: the one tool with NO error shape, asserted as such ────────────────────────

    [Fact]
    public async Task SearchDocs_HasNoErrorShape_EvenForAQueryThatMatchesNothing()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // search_docs' own tool description promises this: "every query, including one with no
        // matches or an over-long query, returns a structured result — an empty match list, never
        // an error". It is therefore the documented exception to this story's
        // one-error-golden-per-tool rule, not a tool whose error golden was forgotten.
        var noMatches = await CallAsync(harness, "search_docs", new() { ["query"] = "zzzqqqxxnomatchwhatsoever" }, cts.Token);
        Assert.False(noMatches.IsError ?? false);

        var overLong = await CallAsync(harness, "search_docs", new() { ["query"] = new string('a', 5000) }, cts.Token);
        Assert.False(overLong.IsError ?? false);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The published docs-site URL shape every catalogued <c>VfxError</c> code carries. The
    /// published shape is this repo's own site, not the engine's — see
    /// <c>VfxCodeCatalogue.DocsUrlPrefix</c>'s remarks — with a ".html" suffix matching how
    /// <c>scripts/build_site.py</c> actually renders <c>docs/errors/&lt;CODE&gt;.md</c>.
    /// </summary>
    private const string DocsUrlPrefix = "https://vouchfx-mcp.vouchfx.io/docs/errors/";

    private const string DocsUrlSuffix = ".html";

    /// <summary>
    /// Calls <paramref name="toolName"/> expecting a tool-level error, and asserts the FULL
    /// per-tool <c>VfxError</c> contract this whole class exists to pin — not just the code and
    /// retryable flag, but the "single well-formed object, no <c>meta</c> stamp, well-shaped
    /// <c>docsUrl</c>" shape every one of the nine error-capable tools must share. Folding that
    /// once-single-tool assertion in here (a Sprint-1 close review fix) means the property is now
    /// SWEPT across all nine call sites below rather than sampled from <c>list_step_types</c> alone,
    /// closing the gap a prior test name ("EveryErrorResult...") had promised but not delivered.
    /// </summary>
    /// <remarks>
    /// The no-<c>meta</c> half of this assertion is also STRUCTURAL, not merely a golden this test
    /// happens to check: <see cref="Vouchfx.Mcp.Tools.StructuredToolResult.Error"/> builds its
    /// <c>CallToolResult</c> directly from the <see cref="Vouchfx.Mcp.Contracts.VfxError"/> payload
    /// and never calls the private <c>SerialiseWithMeta</c> helper <c>Success</c> uses to stamp
    /// <c>meta</c> — there is no code path by which an error result could carry one. Do not weaken
    /// this assertion (or the choke point it pins) to "usually absent"; a future reader should be
    /// able to trust that an error response is exactly the <c>VfxError</c> shape, nothing more.
    /// </remarks>
    private static async Task AssertErrorCodeAsync(
        string toolName,
        Dictionary<string, object?>? arguments,
        string expectedCode,
        bool expectedRetryable,
        IVouchfxCli? cli = null)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, vouchfxCli: cli, suiteRunner: FakeSuiteRunner.NeverExpectedToRun());

        var result = await CallAsync(harness, toolName, arguments, cts.Token);

        Assert.True(result.IsError, $"Expected '{toolName}' to return isError: true.");

        var error = SingleVfxError(result);
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Equal(expectedRetryable, error.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // Exactly the VfxError contract: code + message + retryable always; docsUrl present because
        // every catalogued code has one; and NO `meta` stamp (US-S1-02 scopes that to successes —
        // see this method's own remarks for why that is structural, not incidental).
        Assert.False(error.TryGetProperty("meta", out _));
        var docsUrl = error.GetProperty("docsUrl").GetString()!;
        Assert.StartsWith(DocsUrlPrefix, docsUrl, StringComparison.Ordinal);
        Assert.EndsWith(DocsUrlSuffix, docsUrl, StringComparison.Ordinal);
        Assert.Equal(
            error.GetProperty("code").GetString(),
            docsUrl[DocsUrlPrefix.Length..^DocsUrlSuffix.Length]);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Reads the single <c>VfxError</c> JSON object an error result carries, asserting the "single
    /// object" half of the contract (exactly one content block, and it parses as a JSON object)
    /// before returning it.
    /// </summary>
    private static JsonElement SingleVfxError(CallToolResult result)
    {
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));

        using var document = JsonDocument.Parse(content.Text);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.Clone();
    }

    private static void AssertInRange(string? code, int lowInclusive, int highInclusive)
    {
        Assert.NotNull(code);
        Assert.Matches(@"^VFX-[ED]-\d{4}$", code);

        var number = int.Parse(code.AsSpan(6), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(number, lowInclusive, highInclusive);
    }

    private static ValueTask<CallToolResult> CallAsync(
        McpTestHarness harness, string toolName, Dictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static JsonElement Structured(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    /// <summary>
    /// A minimal <c>vouchfx plan --json</c> report carrying exactly one gap finding — enough to
    /// prove a gap comes back as data on a successful call, without restating
    /// <c>RealPlanCoverageMcpTests</c>' fuller fixture.
    /// </summary>
    private const string GapReportJson = """
        {
          "schemaVersion": 1,
          "engineVersion": "1.0.0-test",
          "thresholds": { "staleDays": 30, "flakyMinRuns": 2, "fragileMinEnvErrors": 2, "inconclusiveMin": 2 },
          "inventory": {
            "suites": [ { "path": "checkout.e2e.yaml", "scenarioId": "checkout-flow", "name": "checkout-flow", "stepCount": 2 } ],
            "services": [ "api" ],
            "dependencies": [ { "name": "orders-db", "type": "postgres", "suite": "checkout.e2e.yaml" } ],
            "stepTypes": [ "db-assert.postgres", "http.rest" ],
            "runCount": 1,
            "firstEventTs": "2026-01-01T00:00:00+00:00",
            "lastEventTs": "2026-01-01T00:05:00+00:00",
            "skippedEventLines": 0,
            "unmatchedObservations": 0,
            "unanalysableSuites": [],
            "unmappableDependencies": []
          },
          "findings": [
            {
              "kind": "dependency-missing-step-type",
              "suite": "checkout.e2e.yaml",
              "stepId": null,
              "target": "orders-db",
              "targetKind": "dependency",
              "suggestedTypes": ["db-assert.postgres"],
              "suggestedStepId": "assert-orders-db",
              "ambiguous": false,
              "ambiguityReason": null,
              "history": null,
              "detail": "Dependency 'orders-db' (postgres) has no analysed step of a candidate asserting type.",
              "relatedSuites": []
            }
          ]
        }
        """;
}
