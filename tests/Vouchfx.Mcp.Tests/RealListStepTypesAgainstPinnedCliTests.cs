using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The engine-side watch on US-S2-05's two transcribed claims: that the pinned engine's
/// <c>vouchfx list --json</c> still emits NONE of the U5-gated <c>ProviderInfo</c> fields, and that
/// its live step-type set is exactly the vendored catalogue's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists (M2, second-reviewer follow-up).</b> Two US-S2-05 facts are pure
/// transcription of what the engine emits TODAY, and nothing in the codebase notices when the engine
/// changes underneath them:
/// </para>
/// <list type="number">
/// <item><description>
/// The "pending upstream ask U5" claim (<see cref="ProviderInfoContract.U5Gated"/>) rests on the
/// engine NOT emitting <c>tier</c>/<c>vouched</c>/<c>supportsVerifyMode</c>/<c>example</c>/<c>docsUrl</c>
/// in <c>list --json</c>. When U5 lands and those fields appear, <see cref="StepCatalogueParser"/>
/// silently ignores them (it reads only the bar-B fields) and the catalogue tools keep advertising
/// "pending U5" — a stale lie. Assertion 1 below re-parses the REAL CLI's raw stdout and fails,
/// naming the field, the instant a gated field appears on any entry.
/// </description></item>
/// <item><description>
/// <see cref="RequiredResourceCatalogue"/>'s <see langword="null"/> arm (a live type the vendored
/// schema does not define) is the SAME machine as a live-vs-vendored drift. Assertion 2 asserts the
/// live step-type set equals <see cref="StepTypeCatalogue.All"/>'s, so a divergence at the same pin
/// is loud here rather than swallowed as a run of omitted fields.
/// </description></item>
/// </list>
/// <para>
/// <b>Runs only when the installed CLI matches ENGINE_PIN; skips cleanly otherwise</b> — the exact
/// self-gating pattern <see cref="RealPlanCoverageAgainstPinnedCliTests"/> and
/// <see cref="RealValidateAgainstPinnedCliTests"/> use, and for the same reasons: this repo has no
/// dynamic-skip package, so the gate reuses the PRODUCTION <see cref="CliPinVerifier"/> against the
/// real PATH and the real <c>ENGINE_PIN</c>, and returns early (a silent pass, not a failure) when
/// the result is not <see cref="CliPinResult.Ok"/>. CI installs no CLI and passes trivially; a
/// maintainer's machine — where pin bumps happen — runs it for real. A broken CLI probe (launched
/// non-zero, or unparseable output) fails LOUDLY rather than skipping, so the oracle cannot be
/// silently disarmed.
/// </para>
/// </remarks>
public class RealListStepTypesAgainstPinnedCliTests
{
    private readonly ITestOutputHelper _testOutput;

    public RealListStepTypesAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    [Fact]
    public async Task ListJson_AgainstPinnedInstalledCli_EmitsNoU5GatedField_AndMatchesTheVendoredTypeSet()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var realCli = new VouchfxCliProcessRunner();

        // The gate: skip cleanly (not a failure) when no installed CLI matches ENGINE_PIN. Reuses the
        // SAME production CliPinVerifier every CLI-backed tool goes through — deliberately NOT a new
        // invented skip mechanism (see this class's remarks and RealPlanCoverageAgainstPinnedCliTests).
        var pinCheck = await new CliPinVerifier(realCli, pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). " +
                $"Gate outcome: {pinCheck.GetType().Name}. NOTE: this leaves the U5/vendored-drift " +
                "oracle unexercised — a green run here is NOT evidence the engine still omits the gated fields.");
            return;
        }

        // The real, pinned binary, invoked exactly as LiveStepCatalogue invokes it.
        var invocation = await realCli.RunAsync(
            ["list", "--json"],
            VouchfxCliProcessRunner.MaxListJsonOutputBytes,
            VouchfxCliProcessRunner.DefaultTimeout,
            cts.Token);

        // A launched-but-non-zero run, or one that produced no stdout, is a BROKEN probe — reported
        // as a failure, never read as agreement. Two empty checks would otherwise pass silently.
        Assert.True(
            invocation is { Launched: true, ExitCode: 0 },
            $"`vouchfx list --json` did not exit cleanly (Launched={invocation.Launched}, "
            + $"ExitCode={invocation.ExitCode}, FailureReason={invocation.FailureReason}). "
            + $"stderr: {invocation.Stderr}");
        var stdout = invocation.Stdout;
        Assert.False(string.IsNullOrWhiteSpace(stdout), "`vouchfx list --json` produced no stdout.");

        // ── Assertion 1: the raw engine JSON carries no U5-gated field on any entry ───────────────
        //
        // Deliberately over the RAW stdout, not the parsed StepTypeInfo: StepCatalogueParser reads
        // only the bar-B fields, so a gated field the engine started emitting would be invisible in
        // the parsed shape — the exact blind spot this assertion closes.
        using var document = JsonDocument.Parse(stdout!);
        var stepTypes = document.RootElement.GetProperty("stepTypes");
        Assert.Equal(JsonValueKind.Array, stepTypes.ValueKind);
        Assert.NotEqual(0, stepTypes.GetArrayLength());

        var gatedHits = new List<string>();
        foreach (var entry in stepTypes.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var typeName = entry.TryGetProperty("type", out var t) ? t.GetString() : "<unknown>";
            foreach (var gated in ProviderInfoContract.U5Gated)
            {
                if (entry.TryGetProperty(gated, out _))
                {
                    gatedHits.Add($"{typeName}.{gated}");
                }
            }
        }

        Assert.True(
            gatedHits.Count == 0,
            "The pinned engine's `vouchfx list --json` now emits U5-gated ProviderInfo field(s) that "
            + "US-S2-05 records as 'pending upstream ask U5' and both catalogue tools still advertise "
            + "as absent: " + string.Join(", ", gatedHits) + ". Move the landed field(s) out of "
            + "ProviderInfoContract.U5Gated and populate them.");

        // ── Assertion 2: the live step-type set equals the vendored catalogue's ───────────────────
        //
        // Parses through the SAME production parser the tools use, then compares type sets. This is
        // the null-omission arm made loud: a type the live engine carries but the vendored schema
        // does not (or vice versa) at the SAME pin is drift, not a silent run of omitted fields.
        var liveTypes = StepCatalogueParser.Parse(stdout!)
            .Select(s => s.Type)
            .ToHashSet(StringComparer.Ordinal);
        var vendoredTypes = StepTypeCatalogue.All
            .Select(s => s.Type)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            vendoredTypes.OrderBy(s => s, StringComparer.Ordinal),
            liveTypes.OrderBy(s => s, StringComparer.Ordinal));

        _testOutput.WriteLine(
            $"MEASURED live against pinned CLI ({pin.Version}): {liveTypes.Count} step types, "
            + $"0 U5-gated fields on any entry, live set == vendored set "
            + $"({vendoredTypes.Count} types).");
    }
}
