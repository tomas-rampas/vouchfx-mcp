using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The engine-side watch on vouchfx-mcp#96's transcribed claims: that the pinned engine still
/// REFUSES a dependency <c>env</c> entry naming an engine-set variable, still says so on stdout in
/// wording <see cref="EngineDiagnosticExcerpt"/>'s signature matches, still exits 4, still writes no
/// events file — and, the security-relevant one, still OMITS the author's VALUE from the message it
/// prints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists (a security review's pin-bump tripwire).</b> Every fact #96 rests on is a
/// transcription of what the engine does TODAY, and nothing else in this codebase notices when the
/// engine changes underneath it. Two failure modes matter and neither is loud without this test:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>A reworded diagnostic silently disarms the feature.</b> The signature is a case-insensitive
/// substring of the engine's own prose; a pin that renames the phrase makes
/// <c>StdoutDiagnosticExcerpt</c> null again and the hint reverts to the pre-#96
/// nothing-at-all — a regression that is invisible against a fake runner, because every other test in
/// this suite supplies the matched line itself.
/// </description></item>
/// <item><description>
/// <b>A pin that starts splicing the author's VALUE into the message is a HYGIENE regression this
/// server would relay verbatim.</b> The engine omits values deliberately — two of the nine engine-set
/// names it refuses (<c>MINIO_ROOT_USER</c>, <c>MINIO_ROOT_PASSWORD</c>) are credentials — and
/// <c>BuildEngineRefusalHint</c> relays the sentence unchanged precisely so that omission survives.
/// If the engine ever included the value, this server would put a suite-supplied secret into a tool
/// result without any code here changing. The probe's <c>env</c> value is therefore a distinctive
/// token asserted ABSENT from the excerpt.
/// </description></item>
/// </list>
/// <para>
/// <b>Runs only when the installed CLI matches ENGINE_PIN; skips cleanly otherwise</b> — the exact
/// self-gating pattern <see cref="RealListStepTypesAgainstPinnedCliTests"/>,
/// <see cref="RealPlanCoverageAgainstPinnedCliTests"/> and
/// <see cref="RealValidateAgainstPinnedCliTests"/> use, reusing the PRODUCTION
/// <see cref="CliPinVerifier"/> against the real PATH rather than inventing a second skip mechanism.
/// Past that gate, a broken probe fails LOUDLY rather than skipping.
/// </para>
/// <para>
/// <b>No container runtime is needed — MEASURED on BOTH hosts, and the CI half is no longer an
/// inference.</b> Maintainer host (Windows, v1.0.0-rc.5, 2026-09-15): the refusal is raised by the
/// engine's <c>EnvironmentMapper</c> before any topology is built, so the whole run completes in about
/// half a second with no DCP or Docker activity. <b>CI (PR #116, run 35014129095,
/// <c>ubuntu-latest</c>): this test ran for real</b> — <c>termination=CompletedNormally,
/// exitCode=4, eventsFileWritten=False, stdoutDiagnosticExcerpt=captured</c>, in 0.75 s. That settles
/// what was previously argued as plausible: the refusal IS reached before the DCP resolution that
/// strands <see cref="RealStepAttemptEnvelopeAgainstPinnedCliTests"/> at "topology failed to start" on
/// that same image, so this class does NOT share that class's second, unasserted container-runtime
/// gate and genuinely exercises its oracle on every runner carrying the pinned CLI.
/// </para>
/// <para>
/// <b>And the first CI run immediately earned its keep: it caught a defect no local run could.</b>
/// The byte-equality tripwire below failed, because
/// <c>EngineDiagnosticExcerptTests.MeasuredRc5RefusalLine</c> had been transcribed on a cp852 host and
/// was therefore the TRANSCODED form (em dashes best-fitted to ASCII hyphens by the engine's own
/// encode, before this server sees a byte — issue #89's mechanism). Two independent local
/// measurements had agreed with each other and both were wrong in the same direction; only a UTF-8
/// host could tell. That is the argument for keeping this assertion exact, and for projecting the
/// expectation through <c>EngineOutputEncoding.Current</c> rather than loosening it.
/// </para>
/// <para>
/// <b>If a FUTURE pin moves the refusal behind topology startup, the failure is LOUD, never a silent
/// skip.</b> This class's only gate is <see cref="CliPinVerifier"/>, which CI satisfies (build.yml
/// installs the pinned CLI, vouchfx-mcp#40), so such a pin reaches the assertions and fails on
/// <c>ExitCode</c> or a null excerpt — visible, attributable, and fixed by gating this class the way
/// <see cref="RealStepAttemptEnvelopeAgainstPinnedCliTests"/> is gated rather than by widening a
/// timeout.
/// </para>
/// </remarks>
public class RealEnvRefusalAgainstPinnedCliTests
{
    /// <summary>
    /// The author-supplied value the probe suite sets. Distinctive on purpose: asserting its ABSENCE
    /// from the engine's message is only meaningful if it could not plausibly occur by chance.
    /// </summary>
    private const string DistinctiveEnvValue = "probe-value-7f3a9c";

    private readonly ITestOutputHelper _testOutput;

    public RealEnvRefusalAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    [Fact]
    public async Task DependencyEnvRefusal_AgainstPinnedInstalledCli_StillPrintsTheSignature_AndStillOmitsTheValue()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());

        // The gate: skip cleanly (not a failure) when no installed CLI matches ENGINE_PIN. Reuses the
        // SAME production CliPinVerifier every CLI-backed tool goes through — deliberately NOT a new
        // invented skip mechanism (see this class's remarks).
        var pinCheck = await new CliPinVerifier(new VouchfxCliProcessRunner(), pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). " +
                $"Gate outcome: {pinCheck.GetType().Name}. NOTE: this leaves vouchfx-mcp#96's engine-side " +
                "claims unexercised — a green run here is NOT evidence the engine still emits the " +
                "signature, still omits the author's env VALUE, or still writes no events file.");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("vfx-mcp-env-refusal-probe-");
        try
        {
            var suitePath = Path.Combine(workspace.FullName, "env-refusal.e2e.yaml");
            var eventsPath = Path.Combine(workspace.FullName, "events.jsonl");
            await File.WriteAllTextAsync(suitePath, BuildProbeSuite(), cts.Token);

            // The PRODUCTION runner, spawning the real pinned CLI with the flags it always passes —
            // not a hand-rolled Process.Start, so what this measures is exactly what run_suite does.
            var started = DateTime.UtcNow;
            var result = await new VouchfxCliSuiteRunner().RunAsync(
                new SuiteRunSpec(suitePath, [], eventsPath),
                _ => { },
                cts.Token);
            var elapsed = DateTime.UtcNow - started;

            _testOutput.WriteLine(
                $"MEASURED live against pinned CLI ({pin.Version}) in {elapsed.TotalSeconds:N2}s: " +
                $"termination={result.Termination}, exitCode={result.ExitCode}, " +
                $"eventsFileWritten={File.Exists(eventsPath)}, " +
                $"stdoutDiagnosticExcerpt={(result.StdoutDiagnosticExcerpt is null ? "<null>" : "captured")}.");
            _testOutput.WriteLine($"Excerpt: {result.StdoutDiagnosticExcerpt ?? "<null>"}");

            // A run that never launched is a BROKEN probe past the gate — a failure, never agreement.
            Assert.Equal(RunTermination.CompletedNormally, result.Termination);

            // 4 = Inconclusive in the engine's own exit-code taxonomy. This is what makes
            // ClassifyFallbackVerdict's exit-4 arm the one #96 relays on, so a pin that moved the
            // refusal to a different code would change which arm fires.
            Assert.Equal(4, result.ExitCode);

            // The signature still matches the engine's own wording.
            Assert.NotNull(result.StdoutDiagnosticExcerpt);
            Assert.True(
                EngineDiagnosticExcerpt.IsDiagnosticLine(result.StdoutDiagnosticExcerpt!),
                $"The captured line no longer carries the enumerated signature "
                + $"('{EngineDiagnosticExcerpt.EnvironmentConfigurationErrorSignature}'): "
                + $"'{result.StdoutDiagnosticExcerpt}'.");

            // THE HYGIENE ASSERTION — deliberately BEFORE the byte-equality tripwire below. The
            // engine names the VARIABLE but never the VALUE; this server relays the sentence
            // unchanged, so a pin that started including the value would leak a suite-supplied
            // secret into a tool result with no code change here. Any such splice also breaks the
            // equality assertion, so ordering this one first is what keeps the FAILURE MESSAGE the
            // hygiene one (naming the leaked value) rather than a generic string diff.
            Assert.DoesNotContain(
                DistinctiveEnvValue, result.StdoutDiagnosticExcerpt!, StringComparison.Ordinal);

            // THE TRIPWIRE ITSELF (a peer review's MINOR finding). EngineDiagnosticExcerptTests's
            // MeasuredRc5RefusalLine constant is described as a pin-bump tripwire, but until this
            // assertion existed NOTHING compared it to the live engine: every other test in the suite
            // feeds that constant in as its own input, so a reworded refusal at a future pin would
            // leave all of them green while the constant quietly became fiction.
            //
            // PROJECTED THROUGH THE CONSOLE ENCODING, not compared directly — the same model
            // GetSchemaOrchestrator uses for issue #89. The engine ENCODES its stdout in the console
            // output code page, and on a cp852 host U+2014 has no representation there, so it is
            // best-fitted to an ASCII hyphen BEFORE this server sees a byte: the true text is simply
            // unreachable on that host, and a direct comparison would fail on every Windows machine
            // while passing on Linux. `enc.GetString(enc.GetBytes(...))` reproduces exactly that one
            // transformation, so the expectation becomes "the engine's true line as THIS host can
            // possibly receive it".
            //
            // This is EXACT, not lenient. It models the single transformation the console applies and
            // nothing else — no normalisation, no substring, no case folding — so a genuinely reworded
            // refusal still fails loudly on every host, which is the whole point of keeping a verbatim
            // constant. (The first CI run proved the mechanism twice over: it caught the constant
            // itself being the cp852-transcoded form.)
            //
            // The decoder this models is EngineOutputEncoding.Current, and since issue #115 the
            // runner's own relay decodes with that EXACT SAME instance (VouchfxCliSuiteRunner reads
            // `process.StandardOutput.BaseStream` through it, rather than `Process.StandardOutput`'s
            // own default-decoding StreamReader) — so this projection is now accurate BY
            // CONSTRUCTION, not merely because the two happened to resolve to the same console page.
            var engineEncoding = EngineOutputEncoding.Current;
            var expectedAsThisHostCanReceiveIt = EngineDiagnosticExcerpt.SanitiseAndCap(
                engineEncoding.GetString(
                    engineEncoding.GetBytes(Run.EngineDiagnosticExcerptTests.MeasuredRc5RefusalLine)));

            Assert.Equal(expectedAsThisHostCanReceiveIt, result.StdoutDiagnosticExcerpt);

            // The variable NAME is expected — it is what makes the message actionable, and it is the
            // half the engine deliberately does keep.
            Assert.Contains("ES_JAVA_OPTS", result.StdoutDiagnosticExcerpt!, StringComparison.Ordinal);

            // No events file at all: the fact that makes the hint the ONLY explanation available, and
            // the reason explain_run/diagnose_run/get_step_timeline have nothing to offer here.
            Assert.False(
                File.Exists(eventsPath),
                "The engine wrote an events file for a refused suite — #96's premise (that the hint is "
                + "the only explanation available) no longer holds at this pin, and the reader tools "
                + "should be preferred over the hint.");
        }
        finally
        {
            try
            {
                workspace.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// A schema-valid suite whose ONLY defect is the refused <c>env</c> entry: an elasticsearch
    /// dependency naming <c>ES_JAVA_OPTS</c> (one of the nine engine-set variables at rc.5) alongside
    /// a real service and one step, so the engine gets far enough to reach the environment mapping
    /// rather than bouncing the suite for an unrelated reason.
    /// </summary>
    private static string BuildProbeSuite() =>
        $"""
        metadata:
          name: "vouchfx-mcp#96 probe: engine-set dependency env refusal"
          owner: "vouchfx-mcp-tests"

        environment:
          services:
            whoami:
              image: traefik/whoami:v1.10
              httpPort: 80
          dependencies:
            search:
              type: elasticsearch
              env:
                ES_JAVA_OPTS: "{DistinctiveEnvValue}"

        steps:
          - id: get-root
            type: http.rest
            description: "Never reached: the refusal fires before any topology is built."
            target: whoami
            method: GET
            path: /
            expect:
              status: 200
        """;
}
