using System.Diagnostics;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The executable form of this repo's "CLI and MCP must not drift" rule, for the one surface where
/// both sides independently evaluate the SAME vendored schema: <c>validate_suite</c> versus
/// <c>vouchfx validate</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists.</b> <see cref="SuiteValidator"/> is a reimplementation, not a wrapper
/// — it re-derives the engine's error-collection behaviour (discriminator-noise filtering,
/// applicator roll-up suppression, satisfied-composite-branch suppression, the
/// <c>unevaluatedProperties</c> cascade, closure-message rewriting) against the same schema the
/// engine composes. Every re-derived rule is a place the two can silently diverge, and a schema
/// change alone is enough to cause it: the <c>v1.0.0-rc.3</c>→<c>v1.0.0-rc.4</c> repin closed
/// <c>$defs/step</c> and added seven composites, and both changes moved this validator's output
/// without a line of code changing here. Unit tests over hand-written expectations cannot catch
/// that class of drift, because the expectations are written by the same person holding the same
/// wrong assumption. Asking the pinned binary is the only independent oracle available.
/// </para>
/// <para>
/// <b>Runs only when the installed CLI matches ENGINE_PIN; skips cleanly otherwise</b>, exactly as
/// <see cref="RealPlanCoverageAgainstPinnedCliTests"/> does and for the same reasons — see that
/// class's remarks. CI deliberately installs no CLI, so this passes trivially there and does its
/// work on a maintainer's machine, which is where pin bumps are actually performed.
/// </para>
/// <para>
/// <b>Scope, stated so a failure is not misread.</b> Only <c>[Schema]</c>-family findings are
/// compared. The CLI runs a fuller pipeline than schema validation: it short-circuits on an
/// unknown step type at parse time (<c>[Parse]</c>) and performs provider model validation
/// (<c>[Pipeline]</c>) that resolves a step's <c>target</c> against declared dependencies. This
/// validator is schema-only and offline by design, so those categories are legitimately absent
/// here and are excluded rather than papered over.
/// </para>
/// </remarks>
public class RealValidateAgainstPinnedCliTests
{
    private readonly ITestOutputHelper _testOutput;

    public RealValidateAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    /// <summary>
    /// Suites chosen to cover every re-derived rule in <see cref="SuiteValidator"/>, each one a
    /// shape that was measured WRONG at some point during the rc.4 repin.
    /// </summary>
    public static TheoryData<string, string> DriftFixtures() => new()
    {
        {
            "closure-only typo on a step (rewritten message + step-type attribution)",
            """
            steps:
              - id: typo-only
                type: http.rest
                method: GET
                path: /orders
                target: orders-api
                bogusField: nope
            """
        },
        {
            "typo alongside a real defect (unevaluatedProperties cascade)",
            """
            steps:
              - id: typo-and-defect
                type: http.rest
                method: GET
                path: /orders
                taget: orders-api
            """
        },
        {
            "satisfied oneOf + anyOf on the same step (losing-branch noise must not surface, and must not hide the typo)",
            """
            steps:
              - id: asb
                type: mq-expect.azureservicebus
                target: bus
                queue: orders
                expectPayloadContains: "ok"
                taget: typo
            """
        },
        {
            "satisfied oneOf on a service (image/project) alongside a step defect",
            """
            environment:
              services:
                orders-api:
                  image: orders:1
                  httpPort: 8080
            steps:
              - id: broken
                type: http.rest
                target: orders-api
                method: GET
            """
        },
        {
            "unknown key on a closed dependency (additionalProperties message + container naming)",
            """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  bogusKey: nope
            steps:
              - id: ok
                type: http.rest
                target: orders-api
                method: GET
                path: /x
            """
        },
        {
            "unknown key on a closed service (container naming, service form)",
            """
            environment:
              services:
                orders-api:
                  image: orders:1
                  httpPort: 8080
                  bogusServiceKey: nope
            steps:
              - id: ok
                type: http.rest
                target: orders-api
                method: GET
                path: /x
            """
        },
        {
            "security block: a real 'required' failure AND a closure rejection on the same node",
            """
            environment:
              dependencies:
                events-kafka:
                  type: kafka
                  security:
                    bogusSecurityKey: nope
            steps:
              - id: ok
                type: http.rest
                target: orders-api
                method: GET
                path: /x
            """
        },
        {
            "two steps, one defective and one merely typo'd (per-step cascade scoping)",
            """
            steps:
              - id: defective
                type: http.rest
                target: api
                method: GET
              - id: typod
                type: http.rest
                target: api
                method: GET
                path: /x
                bogusField: nope
            """
        },
        {
            "a wholly valid suite (neither side may invent an error)",
            """
            steps:
              - id: fine
                type: http.rest
                target: api
                method: GET
                path: /x
            """
        },
    };

    [Theory]
    [MemberData(nameof(DriftFixtures))]
    public async Task ValidateSuite_AgainstPinnedInstalledCli_ReportsTheSameSchemaErrors(string description, string yaml)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var pinCheck = await new CliPinVerifier(new VouchfxCliProcessRunner(), pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). " +
                $"Gate outcome: {pinCheck.GetType().Name}.");
            return;
        }

        var suitePath = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-drift-{Guid.NewGuid():N}.e2e.yaml");
        // LF and no BOM: the CLI and this validator must be handed byte-identical input, or a
        // difference in their output cannot be attributed to their logic.
        await File.WriteAllTextAsync(
            suitePath, yaml.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(false), cts.Token);

        try
        {
            var mine = SuiteValidator.ValidateFile(suitePath).Errors
                .Where(e => e.Kind == "schema")
                .Select(e => e.Message)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

            var theirs = (await RunCliValidateAsync(suitePath, cts.Token))
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

            _testOutput.WriteLine($"fixture: {description}");
            _testOutput.WriteLine($"  MCP ({mine.Count}): {string.Join(" | ", mine)}");
            _testOutput.WriteLine($"  CLI ({theirs.Count}): {string.Join(" | ", theirs)}");

            Assert.Equal(theirs, mine);
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    /// <summary>
    /// Runs <c>vouchfx validate</c> and returns its <c>[Schema]</c> findings with the
    /// <c>[Schema] (line N)</c> prefix stripped, leaving the same "[keyword] message" shape
    /// <see cref="SuiteValidator"/> produces.
    /// </summary>
    private static async Task<List<string>> RunCliValidateAsync(string suitePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "vouchfx",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add(suitePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx CLI.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var findings = new List<string>();
        foreach (var raw in (stdout + "\n" + stderr).Split('\n'))
        {
            var line = raw.Trim();
            const string marker = "[Schema] (line ";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var close = line.IndexOf(')', marker.Length);
            if (close >= 0)
            {
                findings.Add(line[(close + 1)..].TrimStart());
            }
        }

        return findings;
    }
}
