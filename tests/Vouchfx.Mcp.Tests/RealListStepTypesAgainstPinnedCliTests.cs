using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The engine-side watch on the catalogue tools' transcribed claims about the pinned engine's
/// <c>vouchfx list --json</c>: that it reports the four <c>ProviderInfo</c> members this server
/// relays and never the hub-owned one, that the production parser relays them as written, and that
/// its live step-type set is exactly the vendored catalogue's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists (M2, second-reviewer follow-up).</b> These facts are pure transcription
/// of what the engine emits TODAY, and nothing in the codebase notices when the engine changes
/// underneath them:
/// </para>
/// <list type="number">
/// <item><description>
/// The split in <see cref="ProviderInfoContract"/> rests on what the engine emits. Until engine
/// v1.0.0-rc.6 this assertion was the tripwire for upstream ask U5 LANDING — it failed, naming the
/// field, the moment the repin to rc.6 brought <c>tier</c>/<c>supportedVerifyModes</c>/<c>docsUrl</c>/<c>example</c>
/// onto every entry, which is how the relays were added. It now watches both directions: every
/// entry must still carry all four (a later engine dropping one would leave the tools advertising
/// a field they can no longer fill), and none may carry a <see cref="ProviderInfoContract.HubOwned"/>
/// field (an engine starting to emit <c>vouched</c> would make "deliberately absent" a stale claim).
/// It reads the REAL CLI's raw stdout, because <see cref="StepCatalogueParser"/> ignores any member
/// it does not read, so a new field would be invisible in the parsed shape.
/// </description></item>
/// <item><description>
/// The relays themselves: every parsed entry carries the raw entry's values, with
/// <c>supportsVerifyMode</c> read off whether <c>supportedVerifyModes</c> lists <c>RETRY</c>, and
/// every entry is a Core one — the premise the absent-field notice's "the badge does not apply"
/// reasoning in <see cref="ProviderInfoContract"/>'s remarks rests on.
/// </description></item>
/// <item><description>
/// <see cref="RequiredResourceCatalogue"/>'s <see langword="null"/> arm (a live type the vendored
/// schema does not define) is the SAME machine as a live-vs-vendored drift. Assertion 3 asserts the
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
/// the result is not <see cref="CliPinResult.Ok"/>. CI installs the pinned CLI (build.yml's
/// install+assert pair, vouchfx-mcp#40), so this branch is taken only on a machine without it — a
/// maintainer's machine that has not run the install step, or a future CI runner whose pin drifted
/// from what got installed. Once PAST that gate (a matching CLI was found), a broken CLI probe
/// (launched non-zero, or unparseable output) fails LOUDLY rather than skipping, so the oracle
/// cannot be silently disarmed by a flaky invocation of a CLI that was genuinely present.
/// </para>
/// </remarks>
public class RealListStepTypesAgainstPinnedCliTests
{
    /// <summary>The <c>list --json</c> members the catalogue tools relay (engine v1.0.0-rc.6).</summary>
    private static readonly string[] RelayedMembers = ["tier", "supportedVerifyModes", "docsUrl", "example"];

    private readonly ITestOutputHelper _testOutput;

    public RealListStepTypesAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    [Fact]
    public async Task ListJson_AgainstPinnedInstalledCli_CarriesTheRelayedMembers_NoHubOwnedOne_AndTheVendoredTypeSet()
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
                $"Gate outcome: {pinCheck.GetType().Name}. NOTE: this leaves the catalogue-relay/vendored-drift " +
                "oracle unexercised — a green run here is NOT evidence of what the engine emits.");
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

        // ── Assertion 1: every raw entry carries each relayed member and no hub-owned one ─────────
        //
        // Deliberately over the RAW stdout, not the parsed StepTypeInfo: StepCatalogueParser reads
        // only the members it knows, so a member the engine started (or stopped) emitting would be
        // invisible in the parsed shape — the exact blind spot this assertion closes.
        using var document = JsonDocument.Parse(stdout!);
        var stepTypes = document.RootElement.GetProperty("stepTypes");
        Assert.Equal(JsonValueKind.Array, stepTypes.ValueKind);
        Assert.NotEqual(0, stepTypes.GetArrayLength());

        var missingRelays = new List<string>();
        var hubOwnedHits = new List<string>();
        var rawByType = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in stepTypes.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var typeName = entry.TryGetProperty("type", out var t) ? t.GetString() ?? "<null>" : "<unknown>";
            rawByType[typeName] = entry;
            foreach (var member in RelayedMembers)
            {
                if (!entry.TryGetProperty(member, out _))
                {
                    missingRelays.Add($"{typeName}.{member}");
                }
            }

            foreach (var absent in ProviderInfoContract.HubOwned)
            {
                if (entry.TryGetProperty(absent, out _))
                {
                    hubOwnedHits.Add($"{typeName}.{absent}");
                }
            }
        }

        Assert.True(
            missingRelays.Count == 0,
            "The pinned engine's `vouchfx list --json` no longer carries member(s) both catalogue tools "
            + "advertise as relayed: " + string.Join(", ", missingRelays) + ". Update the tool "
            + "descriptions and ProviderInfoContract.DerivedToday to match what the engine emits.");
        Assert.True(
            hubOwnedHits.Count == 0,
            "The pinned engine's `vouchfx list --json` now emits hub-owned ProviderInfo field(s) that both "
            + "catalogue tools advertise as deliberately absent: " + string.Join(", ", hubOwnedHits)
            + ". Decide whether to relay them and move them out of ProviderInfoContract.HubOwned.");

        // ── Assertion 2: the production parser relays each member as the engine wrote it ─────────
        var parsed = StepCatalogueParser.Parse(stdout!);
        foreach (var info in parsed)
        {
            var raw = rawByType[info.Type];
            Assert.Equal(raw.GetProperty("tier").GetString(), info.Tier);
            Assert.Equal(raw.GetProperty("docsUrl").GetString(), info.DocsUrl);
            Assert.Equal(raw.GetProperty("example").GetString(), info.Example);
            Assert.Equal(
                raw.GetProperty("supportedVerifyModes").EnumerateArray().Any(m => m.GetString() == "RETRY"),
                info.SupportsVerifyMode);

            // The CLI lists only the providers it ships, so every entry is Core — which is also why
            // the engine can publish a docsUrl and an example for each of them.
            Assert.Equal("core", info.Tier);
            Assert.False(string.IsNullOrWhiteSpace(info.DocsUrl), $"{info.Type} carries no docsUrl.");
            Assert.False(string.IsNullOrWhiteSpace(info.Example), $"{info.Type} carries no example.");
        }

        // ── Assertion 3: the live step-type set equals the vendored catalogue's ───────────────────
        //
        // Parses through the SAME production parser the tools use, then compares type sets. This is
        // the null-omission arm made loud: a type the live engine carries but the vendored schema
        // does not (or vice versa) at the SAME pin is drift, not a silent run of omitted fields.
        var liveTypes = parsed
            .Select(s => s.Type)
            .ToHashSet(StringComparer.Ordinal);
        var vendoredTypes = StepTypeCatalogue.All
            .Select(s => s.Type)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            vendoredTypes.OrderBy(s => s, StringComparer.Ordinal),
            liveTypes.OrderBy(s => s, StringComparer.Ordinal));

        _testOutput.WriteLine(
            $"MEASURED live against pinned CLI ({pin.Version}): {liveTypes.Count} step types, each "
            + $"carrying {string.Join("/", RelayedMembers)} relayed as written, 0 hub-owned fields on "
            + $"any entry, live set == vendored set ({vendoredTypes.Count} types).");
    }
}
