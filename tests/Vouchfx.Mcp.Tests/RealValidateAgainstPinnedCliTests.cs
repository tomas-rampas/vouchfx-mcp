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
            // The FOURTH wording-gap shape, and the one that had no fixture at all: below a
            // `security` block the engine renders the precise nested locator
            // `in dependency 'X' (at security.serverArtifacts[0].<field>)` where this validator
            // renders the flat `on dependency 'X'`. Same finding, same line, coarser locator —
            // pinned here so the gap is held by an assertion rather than by prose in two files.
            "nested security locator: the engine names the sub-path, this validator names the container",
            """
            environment:
              dependencies:
                events-kafka:
                  type: kafka
                  security:
                    profile: tls
                    endpoint: broker
                    serverArtifacts:
                      - source: ./ca.pem
                        bogusArtifactKey: nope
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

        // The contract for these: same findings, at the same PLACES, of the same KIND — wording may
        // be richer on the CLI side. Both halves are asserted because either alone is weak: lines
        // alone would accept a finding replaced by a different-kind finding on the same line, and
        // kinds alone would accept one that moved. Together they catch anything but the wording.
        Assert.Equal(theirs!.Lines, mine.Lines);
        Assert.Equal(KeywordTags(theirs.Messages), KeywordTags(mine.Messages));

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
    /// US-S2-06 — the sprint-level regression guard: re-measures the <c>validate_suite</c> ↔
    /// <c>vouchfx validate</c> SCHEMA-channel agreement across the engine's whole rejected corpus,
    /// not the hand-picked drift shapes above, and pins it to the baseline the plan recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a whole-corpus re-measurement on top of the curated fixtures.</b> The theories above
    /// cover every re-derived <see cref="SuiteValidator"/> rule with shapes chosen to stress one rule
    /// each; this guard instead sweeps the ENGINE's own rejected corpus at the pinned commit, so a
    /// regression in a shape nobody thought to curate still surfaces as a count change. Sprint 2's
    /// US-S2-03 bolted a semantic pass (VFX-D-1201…1211) downstream of the schema pipeline; this guard
    /// is the corpus-level proof that the semantic work was ADDITIVE — that not one semantic diagnostic
    /// leaked into the schema channel this compares. It compares only the schema channel by construction:
    /// <see cref="SuiteValidator.ValidateFile"/> narrows to <see cref="ValidationLevel.Schema"/> (the
    /// semantic pass never runs), and the tally filters to the schema finding code <c>VFX-D-1101</c>,
    /// so <c>semanticDiagnostics</c> cannot enter this measurement even in principle.
    /// </para>
    /// <para>
    /// <b>Recorded baseline (durable, per the Sprint 1 ToolMeta-byte-count convention).</b>
    /// Measured 2026-09-04 against ENGINE_PIN <c>v1.0.0-rc.4</c> (commit
    /// <c>be12ebd126fdf03dcea9eade7bcec3afbcba001b</c>), whose rejected corpus is exactly 55 fixtures:
    /// <b>33 byte-identical / 13 same-findings-less-enriched / 0 differing</b>, with the remaining
    /// <b>9</b> fixtures excluded from the schema-channel tally because the engine rejects them at an
    /// EARLIER pipeline stage (<c>[Parse]</c>) and so emits no <c>[Schema]</c> finding to compare —
    /// the same <c>[Parse]</c>/<c>[Pipeline]</c> exclusion this class's remarks already describe.
    /// 33 + 13 + 0 + 9 = 55. This equals the plan §7 regression-guard baseline, unchanged after
    /// US-S2-01…05. A pin bump that resizes the corpus updates all four numbers here, deliberately.
    /// </para>
    /// <para>
    /// <b>Reach.</b> The corpus is a maintainer-local resource, exactly like the pinned CLI: it is
    /// extracted from the sibling <c>vouchfx</c> engine checkout AT THE PINNED COMMIT (via
    /// <c>git show</c>, so it is independent of that checkout's working-tree HEAD), never from a copy
    /// vendored into this repo. Absent CLI, absent engine checkout, or a pinned commit not present in
    /// that checkout all self-skip cleanly with a loud note — never a silent green. A <c>vouchfx</c>
    /// that answers but whose version is UNPARSEABLE is a broken probe, not an absent CLI, and fails
    /// loudly rather than skipping.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ValidateSuite_AgainstEnginesRejectedCorpus_SchemaAgreementIsUnchanged_33_13_0()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var pinCheck = await new CliPinVerifier(new VouchfxCliProcessRunner(), pin).VerifyAsync(cts.Token);

        // A broken probe is NOT an absent CLI: a vouchfx binary that answered but whose --version
        // output could not be parsed must fail loudly, never masquerade as "not installed" and skip
        // this guard silently (sprint-00 self-gating rule).
        if (pinCheck is CliPinResult.Unparseable unparseable)
        {
            Assert.Fail(
                "vouchfx responded to the pin probe but its version output was UNPARSEABLE — a broken " +
                "probe, not an absent CLI. Refusing to skip the corpus regression guard silently. " +
                $"Probe detail: {unparseable.Message}");
        }

        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). " +
                $"Gate outcome: {pinCheck.GetType().Name}. NOTE: this leaves the corpus regression " +
                "guard unexercised — a green run here is NOT evidence that MCP and CLI agree.");
            return;
        }

        var engineRepo = ResolveEngineRepoRoot();
        if (engineRepo is null)
        {
            _testOutput.WriteLine(
                "SKIPPED (not a failure): the sibling vouchfx engine checkout was not found (set " +
                "VOUCHFX_ENGINE_REPO or place it at <repoRoot>/../vouchfx). The pinned CLI is present " +
                "but the rejected corpus lives with the engine; without it this guard cannot run.");
            return;
        }

        var fixtures = await ExtractRejectedCorpusAtPinAsync(engineRepo, pin.CommitSha, cts.Token);
        if (fixtures.Count == 0)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): the pinned commit {pin.CommitSha} is not present in the " +
                $"engine checkout at '{engineRepo}' (a shallow clone, or a stale checkout), so its " +
                "rejected corpus could not be extracted. Full-clone the engine repo to exercise this guard.");
            return;
        }

        // The corpus size is itself pinned: the recorded 33/13/0 baseline is only meaningful against
        // the exact 55 fixtures that existed at this commit. A resize here means the pin moved without
        // this baseline being re-measured — surface it, do not average it away.
        Assert.True(
            fixtures.Count == 55,
            $"Expected exactly 55 rejected fixtures at pinned commit {pin.CommitSha}, found {fixtures.Count}. " +
            "If ENGINE_PIN was bumped, re-measure and update the 33/13/0/9/55 baseline recorded on this test.");

        int byteIdentical = 0, wordingGap = 0, differing = 0, parseOnlyExcluded = 0;
        var differingDetail = new System.Text.StringBuilder();

        foreach (var (name, content) in fixtures.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var suitePath = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-corpus-{Guid.NewGuid():N}.e2e.yaml");
            // Written verbatim (the exact bytes git returned for the blob) so the CLI and this
            // validator are handed byte-identical input — the same discipline CompareAsync uses.
            await File.WriteAllBytesAsync(suitePath, content, cts.Token);

            try
            {
                var errors = SuiteValidator.ValidateFile(suitePath).Errors
                    .Where(e => e.Code == "VFX-D-1101").ToList();
                var mine = new Findings(
                    errors.Select(e => e.Message).OrderBy(m => m, StringComparer.Ordinal).ToList(),
                    errors.Select(e => e.Line).OrderBy(l => l).ToList());

                var theirs = await RunCliValidateAsync(suitePath, cts.Token);

                if (theirs.Messages.Count == 0)
                {
                    // The engine rejected this fixture EARLIER than schema validation (a [Parse] or
                    // [Pipeline] error), so there is no [Schema] finding to compare — legitimately out
                    // of this schema-only validator's scope, exactly as this class's remarks state. It
                    // is excluded from the agreement tally, but it is still a REJECTED fixture, so this
                    // validator must not silently accept it: a schema pass that stopped flagging it is a
                    // regression this asserts against.
                    Assert.True(
                        mine.Messages.Count > 0,
                        $"Fixture '{name}' is a rejected corpus fixture the CLI rejects at [Parse]/[Pipeline], " +
                        "yet this schema validator produced NO finding for it — a silent acceptance regression.");
                    parseOnlyExcluded++;
                    continue;
                }

                var linesEqual = theirs.Lines.SequenceEqual(mine.Lines);
                var messagesEqual = theirs.Messages.SequenceEqual(mine.Messages);
                var tagsEqual = KeywordTags(theirs.Messages).SequenceEqual(KeywordTags(mine.Messages));

                if (linesEqual && messagesEqual)
                {
                    byteIdentical++;
                }
                else if (linesEqual && tagsEqual)
                {
                    // Same findings at the same lines, of the same kind — only the engine's wording is
                    // richer. The licensed, measured stopping point (see KnownWordingGapFixtures).
                    wordingGap++;
                }
                else
                {
                    differing++;
                    differingDetail
                        .AppendLine(CultureInfo.InvariantCulture, $"{Environment.NewLine}  [DIFFERING] {name}")
                        .AppendLine(CultureInfo.InvariantCulture, $"    MCP ({mine.Messages.Count}) lines[{string.Join(",", mine.Lines)}]: {string.Join(" | ", mine.Messages)}")
                        .AppendLine(CultureInfo.InvariantCulture, $"    CLI ({theirs.Messages.Count}) lines[{string.Join(",", theirs.Lines)}]: {string.Join(" | ", theirs.Messages)}");
                }
            }
            finally
            {
                File.Delete(suitePath);
            }
        }

        _testOutput.WriteLine(
            $"Corpus schema-agreement @ {pin.Version} ({pin.CommitSha[..12]}…): " +
            $"{byteIdentical} byte-identical / {wordingGap} wording-gap / {differing} differing " +
            $"({parseOnlyExcluded} excluded as [Parse]/[Pipeline], {fixtures.Count} total).");

        // The one invariant this whole guard exists to hold: 0 differing. A semantic diagnostic that
        // leaked into the schema channel would land here as a differing count > 0.
        Assert.True(
            byteIdentical == 33 && wordingGap == 13 && differing == 0 && parseOnlyExcluded == 9,
            $"Schema-channel agreement drifted from the recorded baseline 33 byte-identical / 13 " +
            $"wording-gap / 0 differing / 9 [Parse]-excluded. Measured {byteIdentical}/{wordingGap}/" +
            $"{differing}/{parseOnlyExcluded} over {fixtures.Count} fixtures. Most likely cause: a " +
            $"semantic diagnostic (US-S2-03) leaked into the schema errors array, breaking " +
            $"channel separation.{differingDetail}");
    }

    /// <summary>
    /// The sibling <c>vouchfx</c> engine checkout: <c>VOUCHFX_ENGINE_REPO</c> if set and present,
    /// else <c>&lt;repoRoot&gt;/../vouchfx</c>. Returns <see langword="null"/> when neither exists —
    /// a clean skip, since the corpus is a maintainer-local resource like the pinned CLI itself.
    /// </summary>
    private static string? ResolveEngineRepoRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("VOUCHFX_ENGINE_REPO");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var sibling = Path.GetFullPath(Path.Combine(RepoLayout.ResolveRepoRootPath(), "..", "vouchfx"));
        return Directory.Exists(sibling) ? sibling : null;
    }

    /// <summary>
    /// Extracts the engine's rejected corpus (<c>.e2e.yaml</c> fixtures) AT <paramref name="commitSha"/>
    /// — not the checkout's working-tree HEAD — via <c>git show</c>, returning each fixture's name and
    /// its verbatim blob bytes. Returns an empty list when the commit is not in the checkout.
    /// </summary>
    private static async Task<List<(string Name, byte[] Content)>> ExtractRejectedCorpusAtPinAsync(
        string engineRepo, string commitSha, CancellationToken cancellationToken)
    {
        const string corpusDir = "tests/Vouchfx.Engine.Compilation.Tests/Corpus/Rejected";

        var listing = await RunGitTextAsync(
            engineRepo,
            new[] { "ls-tree", "-r", "--name-only", commitSha, "--", corpusDir },
            cancellationToken);
        if (listing is null)
        {
            return new List<(string, byte[])>();
        }

        var fixtures = new List<(string Name, byte[] Content)>();
        foreach (var path in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!path.EndsWith(".yaml", StringComparison.Ordinal))
            {
                continue;
            }

            var bytes = await RunGitBytesAsync(
                engineRepo, new[] { "show", $"{commitSha}:{path}" }, cancellationToken);
            if (bytes is not null)
            {
                fixtures.Add((path[(path.LastIndexOf('/') + 1)..], bytes));
            }
        }

        return fixtures;
    }

    /// <summary>Runs <c>git</c> in <paramref name="repo"/>, returning stdout as text, or null on non-zero exit.</summary>
    private static async Task<string?> RunGitTextAsync(string repo, string[] args, CancellationToken cancellationToken)
    {
        using var process = StartGit(repo, args);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? await stdoutTask : null;
    }

    /// <summary>Runs <c>git</c> in <paramref name="repo"/>, returning stdout as raw bytes, or null on non-zero exit.</summary>
    private static async Task<byte[]?> RunGitBytesAsync(string repo, string[] args, CancellationToken cancellationToken)
    {
        using var process = StartGit(repo, args);
        using var buffer = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(copyTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? buffer.ToArray() : null;
    }

    private static Process StartGit(string repo, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repo);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git to extract the engine corpus.");
    }

    /// <summary>One side's findings: the messages, and the source lines they were reported at.</summary>
    private sealed record Findings(List<string> Messages, List<long?> Lines)
    {
        public int Count => Messages.Count;
    }

    /// <summary>
    /// The leading <c>[keyword]</c> tag of each message, sorted — the finding's KIND, stripped of
    /// the wording that is allowed to differ. Both sides emit this tag on every finding.
    /// </summary>
    private static List<string> KeywordTags(IEnumerable<string> messages) =>
        messages
            .Select(m => m.StartsWith('[') && m.IndexOf(']', StringComparison.Ordinal) is var end and > 0
                ? m[..(end + 1)]
                : m)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Writes <paramref name="yaml"/> to a temp file and runs it through both validators. Returns
    /// <c>(null, null)</c> when no installed CLI matches ENGINE_PIN, which callers treat as a skip.
    /// </summary>
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
            var errors = SuiteValidator.ValidateFile(suitePath).Errors.Where(e => e.Code == "VFX-D-1101").ToList();

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
        // Resolved through the same CWE-427-hardened resolver production uses, never a bare
        // "vouchfx": a bare command name lets the OS process loader search this process's own
        // current working directory BEFORE PATH (see VouchfxCliProcessRunner's remarks), so the
        // binary spawned here could differ from the one CompareAsync's CliPinVerifier gate just
        // verified — and this comparison's entire premise is that it runs the PINNED binary.
        var startInfo = new ProcessStartInfo
        {
            FileName = VouchfxCliPathResolver.ResolveAbsolutePath()
                ?? throw new InvalidOperationException(
                    "vouchfx was not resolvable on PATH even though the CliPinVerifier gate passed."),
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
            const string prefix = "[Schema] ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // The `(line N)` group is OPTIONAL. The CLI omits it for a finding it cannot locate —
            // a missing top-level `steps` section, for instance — and this validator reports that
            // same finding with a null Line. Requiring the group silently dropped those from the
            // CLI side only, which would surface as a phantom divergence.
            var rest = line[prefix.Length..];
            const string lineMarker = "(line ";
            if (rest.StartsWith(lineMarker, StringComparison.Ordinal) &&
                rest.IndexOf(')', lineMarker.Length) is var close and >= 0)
            {
                lines.Add(long.TryParse(
                    rest[lineMarker.Length..close],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var lineNumber)
                    ? lineNumber
                    : null);
                messages.Add(rest[(close + 1)..].TrimStart());
            }
            else
            {
                lines.Add(null);
                messages.Add(rest);
            }
        }

        return new Findings(
            messages.OrderBy(m => m, StringComparer.Ordinal).ToList(),
            lines.OrderBy(l => l).ToList());
    }
}
