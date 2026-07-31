using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Planning;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// REQ-012's un-tested clause, closed: <c>plan_coverage</c> MUST obtain its report from the
/// <b>pinned engine</b> "so MCP and CLI cannot drift". Every other Planner test
/// (<see cref="RealPlanCoverageMcpTests"/>) drives <c>plan_coverage</c> through
/// <see cref="FakeVouchfxCli"/> — deliberately, so CI never depends on a real <c>vouchfx</c>
/// install (see that class's own remarks and <c>McpTestHarness</c>'s "CLI is FAKED by default"
/// remark). None of that proves the tool actually shells out to the real, pinned CLI binary
/// rather than e.g. a bundled/vendored report. This class is the one place in the suite that
/// does: it constructs the REAL, process-spawning <see cref="VouchfxCliProcessRunner"/> — the
/// exact same production type <c>Program.cs</c> wires up — and drives <c>plan_coverage</c>
/// through it, then separately invokes that same real binary directly, and asserts the two
/// reports agree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runs only when the actually-installed CLI matches ENGINE_PIN; skips cleanly otherwise.</b>
/// This repo has no dynamic-skip test package (plain xunit 2.9.3 — see
/// <c>Vouchfx.Mcp.Tests.csproj</c>) and no existing runtime skip scheme to reuse, because no other
/// test in this repo depends on the real CLI's presence at all (every other "Real*" test name
/// refers to driving the real in-memory MCP wire protocol or a real spawned <em>vouchfx-mcp</em>
/// process — see <see cref="RealServerProcessTests"/> — never the real <c>vouchfx</c> engine CLI).
/// The gate below therefore reuses the exact PRODUCTION decision it must agree with —
/// <see cref="CliPinVerifier.VerifyAsync"/>, the same type <c>run_suite</c>/<c>scaffold_suite</c>/
/// <c>plan_coverage</c> themselves call — against the real <see cref="VouchfxCliProcessRunner"/>
/// and the real <c>ENGINE_PIN</c> file, and returns early (a silent pass, not a failure) when the
/// result is not <see cref="CliPinResult.Ok"/>. This is deliberately NOT a new invented skip
/// mechanism: it is the same CLI-presence/version gate every CLI-dependent tool already goes
/// through, merely evaluated once up front to decide whether this ONE test's environment
/// precondition (a matching CLI on PATH) holds. <see cref="PlanCoverage_RealCliGate_IsNotOk_WhenExpectedPinCannotPossiblyMatch"/>
/// below proves that gate's non-Ok path actually fires against the real CLI (see its own remarks).
/// </para>
/// <para>
/// <b>Docker-free and fast.</b> <c>vouchfx plan</c> is a read-only static analysis over suite files
/// and (optionally) a JSON Lines event history — no container, no network. This test supplies no
/// <c>eventsPath</c> at all (a valid, successful analysis per REQ-009), so every step surfaces as
/// "never exercised" — deterministic, and irrelevant to what this test actually proves.
/// </para>
/// </remarks>
public class RealPlanCoverageAgainstPinnedCliTests
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _testOutput;

    public RealPlanCoverageAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    // ── The real, non-fake end-to-end check ─────────────────────────────────────────────────────

    [Fact]
    public async Task PlanCoverage_AgainstPinnedInstalledCli_McpAndDirectCliReportsAgreeOnSubstantiveContent()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var realCli = new VouchfxCliProcessRunner();

        // The gate: skip cleanly (not a failure) when no installed CLI matches ENGINE_PIN — see
        // this class's own remarks for why this reuses CliPinVerifier rather than inventing a new
        // scheme. On THIS repo's own dev machine as of this test's authoring, the real CLI is
        // v1.0.0-rc.3 and ENGINE_PIN pins exactly that, so this branch is not taken there — but any
        // CI runner without the CLI installed (see build.yml's own remarks: it deliberately never
        // installs one) takes it and the test passes trivially.
        var pinCheck = await new CliPinVerifier(realCli, pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN " +
                $"({pin.Version}); this test only exercises the real CLI when one is present. " +
                $"Gate outcome: {pinCheck.GetType().Name}.");
            return;
        }

        var fixturePath = FixturePath("good-suite.e2e.yaml");

        // Side A: plan_coverage through the full MCP wire protocol, wired to the REAL CLI runner
        // (never FakeVouchfxCli) and the REAL ENGINE_PIN (never McpTestHarness.DefaultTestPin).
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: realCli, enginePin: pin);
        var mcpCallResult = await harness.Client.CallToolAsync(
            "plan_coverage",
            new Dictionary<string, object?> { ["path"] = fixturePath },
            cancellationToken: cts.Token);

        Assert.False(mcpCallResult.IsError ?? false);
        var mcpPayload = mcpCallResult.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent from plan_coverage.");
        var mcpResult = mcpPayload.Deserialize<PlanCoverageResult>(ReportJsonOptions)
            ?? throw new InvalidOperationException("plan_coverage's StructuredContent deserialised to null.");

        // Side B: the SAME pinned binary, invoked directly with the identical arguments
        // (`plan <fixturePath> --json`, no events, no threshold overrides) — the exact CLI
        // invocation PlanCoverageOrchestrator.BuildArguments itself would build for this call.
        var directInvocation = await realCli.RunAsync(
            ["plan", fixturePath, "--json"], VouchfxCliProcessRunner.MaxPlanOutputBytes, cts.Token);
        Assert.True(directInvocation is { Launched: true, ExitCode: 0 });
        var cliResult = JsonSerializer.Deserialize<PlanCoverageResult>(directInvocation.Stdout!, ReportJsonOptions)
            ?? throw new InvalidOperationException("Direct `vouchfx plan --json` stdout deserialised to null.");

        // ── What is compared, and how ───────────────────────────────────────────────────────
        //
        // Every substantive field the frozen plan-report DTO models: schemaVersion, engineVersion,
        // effective thresholds, the full inventory (suites/services/dependencies/stepTypes/counts),
        // and the full finding set (kind/suite/stepId/suggestedTypes/suggestedStepId/detail/etc for
        // every finding) -- via a canonical-JSON round trip (below), plus explicit scalar asserts
        // here for a readable failure message if the two ever disagree.
        //
        // Deliberately NOT compared: the raw stdout TEXT byte-for-byte. The CLI's own stdout is
        // indented/pretty-printed; the MCP protocol's StructuredContent is compact JSON re-encoded
        // by the MCP SDK -- a textual diff would report a mismatch on whitespace alone despite
        // identical content. Re-parsing both into the identical DTO and re-serialising with the
        // SAME JsonSerializerOptions (below) normalises that formatting difference away without
        // weakening what is actually asserted: every field the DTO carries still has to agree, or
        // the canonical JSON strings will differ.
        Assert.Equal(cliResult.SchemaVersion, mcpResult.SchemaVersion);
        Assert.Equal(cliResult.EngineVersion, mcpResult.EngineVersion);
        Assert.Equal(cliResult.Inventory.Suites.Count, mcpResult.Inventory.Suites.Count);
        Assert.Equal(cliResult.Inventory.StepTypes, mcpResult.Inventory.StepTypes);
        Assert.Equal(cliResult.Findings.Count, mcpResult.Findings.Count);

        // The comprehensive backstop: round-trip BOTH sides through the identical DTO + serializer
        // options so property ordering/whitespace cannot cause a false mismatch, then compare the
        // resulting canonical JSON text for exact equality -- this is what actually proves the
        // FULL inventory and FULL finding set (not just the counts asserted above) agree field by
        // field, including nested suggestedTypes/relatedSuites arrays that xunit's own Assert.Equal
        // cannot safely deep-compare once nested inside a record (see this file's own authoring
        // notes: List<T>/array members of a record fall back to reference equality under the
        // record's compiler-generated Equals, so comparing the .NET objects directly would risk a
        // false failure here even when the content genuinely agrees).
        var canonicalCli = JsonSerializer.Serialize(cliResult, ReportJsonOptions);
        var canonicalMcp = JsonSerializer.Serialize(mcpResult, ReportJsonOptions);
        Assert.Equal(canonicalCli, canonicalMcp);

        // ── The anti-drift assertion REQ-012 exists for: engineVersion must match ENGINE_PIN ────
        var reportedEngineVersion = mcpResult.EngineVersion
            ?? throw new InvalidOperationException("Expected a non-null engineVersion from the pinned engine.");
        var normalisedReportedVersion = CliVersionNormaliser.Normalise(reportedEngineVersion);
        var normalisedPinVersion = CliVersionNormaliser.Normalise(pin.Version);
        Assert.Equal(normalisedPinVersion, normalisedReportedVersion, ignoreCase: true);

        // Stronger still: the exact commit SHA embedded in the running engine binary's own
        // '+<sha>' build-metadata suffix (see CliVersionNormaliser's remarks for why the real CLI
        // always carries one) must equal ENGINE_PIN's own CommitSha field -- not merely the same
        // semantic version, the same COMMIT.
        var buildMetadataIndex = reportedEngineVersion.IndexOf('+', StringComparison.Ordinal);
        Assert.True(
            buildMetadataIndex >= 0,
            $"Expected engineVersion '{reportedEngineVersion}' to carry a '+<commit-sha>' build-metadata suffix.");
        var embeddedCommitSha = reportedEngineVersion[(buildMetadataIndex + 1)..];
        Assert.Equal(pin.CommitSha, embeddedCommitSha, ignoreCase: true);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Proves the skip mechanism above actually fires against the real CLI ─────────────────────

    [Fact]
    public async Task PlanCoverage_RealCliGate_IsNotOk_WhenExpectedPinCannotPossiblyMatch()
    {
        // Proves the exact gate PlanCoverage_AgainstPinnedInstalledCli_... uses to decide "run for
        // real vs. skip cleanly" genuinely refuses to proceed against the REAL CLI runner when the
        // expected pin does not match -- the situation on every machine without today's exact
        // pinned version installed (every CI runner included; see build.yml's own remarks: a real
        // CLI is deliberately never installed there). A deliberately impossible-to-match version is
        // used rather than mutating PATH to simulate "CLI absent": mutating the real, process-wide
        // PATH would risk racing any OTHER test that resolves the real CLI concurrently (xunit
        // parallelises test classes by default), whereas this needs no such mutation -- whatever
        // real CLI is (or is not) installed here, it cannot match a version string invented for
        // this test.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var impossiblePin = new EnginePin("v0.0.1-cannot-possibly-match", new string('a', 40));
        var realCli = new VouchfxCliProcessRunner();

        var pinCheck = await new CliPinVerifier(realCli, impossiblePin).VerifyAsync(cts.Token);

        // Whichever real, concrete non-Ok case fires (NotFound if no CLI is on PATH at all;
        // VersionMismatch if one is present but -- as it must be -- reports something other than
        // "0.0.1-cannot-possibly-match") both prove the SAME thing: the gate never reports Ok for a
        // pin the installed CLI cannot possibly satisfy, so the primary test's skip branch is
        // reachable and correct.
        Assert.IsNotType<CliPinResult.Ok>(pinCheck);
        _testOutput.WriteLine($"Real CLI gate correctly reported: {pinCheck.GetType().Name}.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
