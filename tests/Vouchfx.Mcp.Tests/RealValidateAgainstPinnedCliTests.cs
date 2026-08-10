using System.Diagnostics;
using System.Globalization;
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
            // Promoted here from KnownWordingGapFixtures by that theory's own NotEqual guard: the
            // nested-container branch was dead code (it tested for a 5-segment pointer the schema
            // cannot produce), so this reported the flat "on service 'app'" form. It now matches
            // the CLI exactly.
            "health-check required: nested container form, 'in service X (at healthCheck)'",
            """
            environment:
              services:
                app:
                  image: app:1
                  ports: ["8080:8080"]
                  healthCheck:
                    type: tcp
            steps:
              - id: ok
                type: http.rest
                target: app
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
    };

    /// <summary>
    /// Shapes where the two sides agree on WHICH errors exist and WHERE, but the engine's message
    /// is richer than this validator's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are a deliberate, measured stopping point, not an oversight. The engine derives its
    /// wording from per-clause formatters (<c>FormatEnumError</c>, <c>FormatConstError</c>,
    /// <c>FormatForbiddenPropertyError</c>) whose text is hand-authored per schema clause, sits
    /// inside its frozen message surface, and in places carries release-position prose. Copying
    /// that here would create a second copy that rots independently of the engine — the exact
    /// failure mode this whole class exists to detect.
    /// </para>
    /// <para>
    /// What is asserted for them is the part that matters and that CAN be held: the same findings
    /// at the same SOURCE LINES (the only locator both sides emit — the CLI's
    /// <c>[Schema] (line N)</c> against this validator's <c>Line</c>), and never the opaque
    /// <c>[]</c> empty-keyword tag. A regression that adds, drops, or moves a finding fails here
    /// even though the wording is allowed to differ.
    /// </para>
    /// </remarks>
    public static TheoryData<string, string> KnownWordingGapFixtures() => new()
    {
        {
            "enum: the engine names the offending value and the accepted set",
            """
            environment:
              dependencies:
                db:
                  type: cassandra
            steps:
              - id: ok
                type: http.rest
                target: api
                method: GET
                path: /x
            """
        },
        {
            // Also the forbidden-CONTAINER subsumption: a `security` block on a redis dependency
            // cannot exist at all, so the two findings INSIDE it (wrong-case profile, missing
            // endpoint) are moot — reporting them tells the author to repair a block they must
            // delete. Measured before that pass was ported: 3 findings here against the CLI's 1.
            // The count now agrees; only the engine's per-clause explanation is richer.
            "forbidden container: everything inside a refused block is subsumed by the refusal",
            """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: TLS
            steps:
              - id: ok
                type: http.rest
                target: api
                method: GET
                path: /x
            """
        },
        {
            "forbidden property: the engine explains WHY the property is refused here",
            """
            environment:
              services:
                app:
                  image: app:1
                  project: ./app.csproj
            steps:
              - id: ok
                type: http.rest
                target: app
                method: GET
                path: /x
            """
        },
    };

    /// <summary>A wholly valid suite: neither side may invent an error.</summary>
    public static TheoryData<string, string> ValidFixtures() => new()
    {
        {
            "a wholly valid suite",
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
        var (mine, theirs) = await CompareAsync(description, yaml);
        if (mine is null)
        {
            return;
        }

        // Every fixture in this set is expected to be rejected. Asserting the CLI produced findings
        // is what stops a CLI that failed to launch, or whose output shape changed, from being
        // silently read as agreement — two empty lists compare equal.
        Assert.NotEmpty(theirs!.Messages);

        // Compared member-wise, never as whole records: List<T> equality is by reference, so
        // Assert.Equal on the records would pass only by accident.
        Assert.Equal(theirs.Messages, mine.Messages);
        Assert.Equal(theirs.Lines, mine.Lines);
    }

    [Theory]
    [MemberData(nameof(KnownWordingGapFixtures))]
    public async Task ValidateSuite_KnownWordingGaps_StillAgreeOnWhichErrorsExistAndWhere(string description, string yaml)
    {
        var (mine, theirs) = await CompareAsync(description, yaml);
        if (mine is null)
        {
            return;
        }

        Assert.NotEmpty(theirs!.Messages);

        // The contract for these: same findings in the same PLACES, wording may be richer on the
        // CLI side. Locations are compared by source LINE, which is the only locator both sides
        // emit — the CLI reports `[Schema] (line N)`, this validator reports SuiteValidationError.
        // Line. Comparing them as sorted multisets catches a finding that moved, was added, or was
        // dropped, which a bare count comparison cannot.
        Assert.Equal(theirs!.Lines, mine.Lines);

        // And never the opaque empty-keyword tag, which tells an author nothing at all.
        Assert.DoesNotContain(mine.Messages, m => m.StartsWith("[]", StringComparison.Ordinal));

        // If a fixture here ever reaches exact message equality, promote it into DriftFixtures —
        // the wording gap it documents has been closed and should stop being licensed.
        Assert.NotEqual(theirs.Messages, mine.Messages);
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public async Task ValidateSuite_AValidSuite_IsAcceptedByBothSides(string description, string yaml)
    {
        var (mine, theirs) = await CompareAsync(description, yaml);
        if (mine is null)
        {
            return;
        }

        Assert.Empty(mine.Messages);
        Assert.Empty(theirs!.Messages);
    }

    /// <summary>
    /// Writes <paramref name="yaml"/> to a temp file and runs it through both validators. Returns
    /// <c>(null, null)</c> when no installed CLI matches ENGINE_PIN, which callers treat as a skip.
    /// </summary>
    /// <summary>One side's findings: the messages, and the source lines they were reported at.</summary>
    private sealed record Findings(List<string> Messages, List<long?> Lines)
    {
        public int Count => Messages.Count;
    }

    private async Task<(Findings? Mine, Findings? Theirs)> CompareAsync(string description, string yaml)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var pinCheck = await new CliPinVerifier(new VouchfxCliProcessRunner(), pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). " +
                $"Gate outcome: {pinCheck.GetType().Name}. NOTE: this leaves the drift oracle " +
                "unexercised — a green run here is NOT evidence that MCP and CLI agree.");
            return (null, null);
        }

        var suitePath = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-drift-{Guid.NewGuid():N}.e2e.yaml");
        // LF and no BOM: the CLI and this validator must be handed byte-identical input, or a
        // difference in their output cannot be attributed to their logic.
        await File.WriteAllTextAsync(
            suitePath, yaml.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(false), cts.Token);

        try
        {
            var errors = SuiteValidator.ValidateFile(suitePath).Errors.Where(e => e.Kind == "schema").ToList();

            var mine = new Findings(
                errors.Select(e => e.Message).OrderBy(m => m, StringComparer.Ordinal).ToList(),
                errors.Select(e => e.Line).OrderBy(l => l).ToList());

            var theirs = await RunCliValidateAsync(suitePath, cts.Token);

            _testOutput.WriteLine($"fixture: {description}");
            _testOutput.WriteLine($"  MCP ({mine.Count}) lines [{string.Join(",", mine.Lines)}]: {string.Join(" | ", mine.Messages)}");
            _testOutput.WriteLine($"  CLI ({theirs.Count}) lines [{string.Join(",", theirs.Lines)}]: {string.Join(" | ", theirs.Messages)}");

            return (mine, theirs);
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
    private static async Task<Findings> RunCliValidateAsync(string suitePath, CancellationToken cancellationToken)
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

        // Both pipes drained CONCURRENTLY. Draining stdout to completion first deadlocks the child
        // as soon as it writes more to stderr than that pipe's buffer holds — latent at today's
        // output sizes, but the classic shape and free to avoid.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // A validate run resolves to 0 (valid) or 4 (invalid). Anything else means the CLI did not
        // do what this comparison assumes, and an empty finding list would otherwise read as
        // agreement — the failure mode that made an earlier revision of this test unable to fail.
        Assert.True(
            process.ExitCode is 0 or 4,
            $"vouchfx validate exited {process.ExitCode}, which is neither valid (0) nor invalid (4). " +
            $"stdout: {stdout}{Environment.NewLine}stderr: {stderr}");

        var messages = new List<string>();
        var lines = new List<long?>();
        foreach (var raw in (stdout + "\n" + stderr).Split('\n'))
        {
            var line = raw.Trim();
            const string marker = "[Schema] (line ";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var close = line.IndexOf(')', marker.Length);
            if (close < 0)
            {
                continue;
            }

            messages.Add(line[(close + 1)..].TrimStart());
            lines.Add(long.TryParse(
                line[marker.Length..close],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var lineNumber)
                ? lineNumber
                : null);
        }

        return new Findings(
            messages.OrderBy(m => m, StringComparer.Ordinal).ToList(),
            lines.OrderBy(l => l).ToList());
    }
}
