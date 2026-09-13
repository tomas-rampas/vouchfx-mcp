using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Tools;

/// <summary>
/// Covers the WIRE bounds vouchfx-mcp#78 put on <c>run_suite</c>'s pre-flight-failure payload: the
/// per-suite finding cap, the measured response-size fit, and the rule that every bound one of them
/// applies is visible as a COUNT rather than inferable from a list length.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="RunSuiteTool.BuildInvalidSuitesPayload"/> — the production path minus the
/// <c>CallToolResult</c> wrapper — against synthetic failures, because a real pre-flight cannot be
/// made to produce twenty-five suites of long findings on demand and the bound has to hold anyway.
/// The end-to-end shape (which fields appear, and with what values, on a real call) is asserted on the
/// wire in <c>RealRunSuiteMcpTests</c>; these tests assert the arithmetic those cannot reach.
/// </para>
/// <para>
/// <b>"Worst case" below means the worst case the ORCHESTRATOR can hand this method</b> — every entry
/// it will collect, each carrying more findings than one entry may report — with each finding's
/// message an ARBITRARY 400 characters. It is not a maximum the shape imposes: nothing caps a
/// <see cref="SuiteValidationError.Message"/>, so a longer one is always constructible. That is
/// exactly why the fit is a measurement rather than an arithmetic bound, and why these tests assert
/// the measured outcome rather than a computed size.
/// </para>
/// </remarks>
public class RunSuiteInvalidPayloadBoundsTests
{
    /// <summary>The same probe shape the production fit uses, so a figure measured here is comparable.</summary>
    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The worst case the orchestrator can hand this method — every entry it will collect, each
    /// carrying more findings than one entry may report, each finding an arbitrary 400 characters —
    /// is shed until it fits, and says what it shed.
    /// </summary>
    [Fact]
    public void TheWorstCasePayload_IsShedUntilItFitsTheBudget_AndSaysWhatItShed()
    {
        var outcome = new RunSuiteOutcome.SuiteInvalid(
            BuildFailures(RunSuiteOrchestrator.MaxReportedInvalidSuites, errorsPerSuite: 20, messageChars: 400),
            OmittedInvalidSuiteCount: 0);

        var payload = RunSuiteTool.BuildInvalidSuitesPayload(outcome);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SizeProbeOptions).Length;

        Assert.True(
            bytes <= RunSuiteTool.EffectivePreflightBudgetBytes,
            $"The pre-flight-failure payload serialised to {bytes} B, past the "
            + $"{RunSuiteTool.EffectivePreflightBudgetBytes} B budget. RunSuiteTool.FitWithinBudget is "
            + "supposed to make that impossible — it sheds entries until the measurement says it fits.");

        // Whatever it shed is COUNTED: entries reported plus entries omitted still equals every
        // invalid suite the orchestrator handed over. A bound that shrinks a list silently is the
        // failure this assertion exists to prevent.
        Assert.Equal(
            RunSuiteOrchestrator.MaxReportedInvalidSuites,
            payload.InvalidSuites.Count + payload.OmittedInvalidSuiteCount);
        Assert.True(payload.OmittedInvalidSuiteCount > 0, "This worst case is supposed to exercise the shed.");
        Assert.NotEmpty(payload.InvalidSuites);
    }

    /// <summary>
    /// Each entry's own finding list is capped, and the remainder counted — the same visible-bounds
    /// rule, one level down.
    /// </summary>
    [Fact]
    public void AnEntryWithMoreFindingsThanTheCap_ReportsTheCapAndCountsTheRest()
    {
        const int findings = RunSuiteTool.MaxErrorsPerInvalidSuite + 7;

        var outcome = new RunSuiteOutcome.SuiteInvalid(
            BuildFailures(suiteCount: 1, errorsPerSuite: findings, messageChars: 20),
            OmittedInvalidSuiteCount: 0);

        var payload = RunSuiteTool.BuildInvalidSuitesPayload(outcome);

        var entry = Assert.Single(payload.InvalidSuites);
        Assert.Equal(RunSuiteTool.MaxErrorsPerInvalidSuite, entry.Errors.Count);
        Assert.Equal(7, entry.OmittedErrorCount);

        // The findings kept are the FIRST ones, in pipeline order — a caller repairing top-down gets
        // a stable prefix rather than a sample that shifts between calls.
        Assert.Equal("suite-000-error-00", entry.Errors[0].InstancePath);

        // The pre-#78 `validation` field is deliberately the UNCAPPED copy of the same suite's
        // findings, so a host that already read it sees exactly what it always did.
        Assert.Equal(findings, payload.Validation.Errors.Count);
    }

    /// <summary>
    /// An ordinary payload — a handful of suites with a handful of findings each — is passed through
    /// untouched: the bounds must not shrink an answer that already fits.
    /// </summary>
    [Fact]
    public void AnOrdinaryPayload_IsNotShedAtAll()
    {
        var outcome = new RunSuiteOutcome.SuiteInvalid(
            BuildFailures(suiteCount: 3, errorsPerSuite: 2, messageChars: 60),
            OmittedInvalidSuiteCount: 0);

        var payload = RunSuiteTool.BuildInvalidSuitesPayload(outcome);

        Assert.Equal(3, payload.InvalidSuites.Count);
        Assert.Equal(0, payload.OmittedInvalidSuiteCount);
        Assert.All(payload.InvalidSuites, entry =>
        {
            Assert.Equal(2, entry.Errors.Count);
            Assert.Equal(0, entry.OmittedErrorCount);
        });
    }

    /// <summary>
    /// The orchestrator's OWN omission count survives the fit: entries dropped at the collection cap
    /// and entries shed for size land in the one caller-facing number, because a host does not care
    /// which bound bit.
    /// </summary>
    [Fact]
    public void OmissionsFromBothBounds_AreReportedInOneCount()
    {
        const int omittedByTheOrchestrator = 11;

        var outcome = new RunSuiteOutcome.SuiteInvalid(
            BuildFailures(RunSuiteOrchestrator.MaxReportedInvalidSuites, errorsPerSuite: 20, messageChars: 400),
            omittedByTheOrchestrator);

        var payload = RunSuiteTool.BuildInvalidSuitesPayload(outcome);

        Assert.Equal(
            RunSuiteOrchestrator.MaxReportedInvalidSuites + omittedByTheOrchestrator,
            payload.InvalidSuites.Count + payload.OmittedInvalidSuiteCount);
    }

    /// <summary>
    /// The degenerate end of the fit: a SINGLE failure whose own uncapped <c>validation</c> field is
    /// larger than the whole budget. Every entry is shed, the count reports all of them, and what
    /// remains of the additive portion is a small constant.
    /// </summary>
    /// <remarks>
    /// This is the case <c>RunSuiteTool.FitWithinBudget</c>'s remarks describe in prose, measured
    /// here rather than asserted there: the payload as a whole CAN still exceed the budget — the
    /// pre-#78 <c>validation</c> field alone does it — but the fields the story ADDED collapse to
    /// <c>"invalidSuites":[]</c> plus two integers, so they can never be what carries a response over
    /// the class. The measured constant is printed into the failure message so a future edit that
    /// grows it is visible rather than merely red.
    /// </remarks>
    [Fact]
    public void AFailureWhoseOwnValidationExceedsTheBudget_ShedsEveryEntryAndCountsThemAll()
    {
        // One suite, findings far past the budget on their own — MaxErrorsPerInvalidSuite caps what
        // an ENTRY carries, but `validation` relays all of them, which is the whole point here.
        var outcome = new RunSuiteOutcome.SuiteInvalid(
            BuildFailures(suiteCount: 1, errorsPerSuite: 200, messageChars: 1_000),
            OmittedInvalidSuiteCount: 0);

        var payload = RunSuiteTool.BuildInvalidSuitesPayload(outcome);

        Assert.Empty(payload.InvalidSuites);
        Assert.Equal(1, payload.OmittedInvalidSuiteCount);

        // What the story's own fields contribute once everything has been shed: the whole payload,
        // measured with the two PRE-EXISTING content fields emptied, minus the envelope those two
        // occupy even when empty. Content-independent by construction — nothing is left but three
        // property names, an empty array and two integers.
        var shed = payload with { Path = string.Empty, Validation = new ValidateSuiteResult(false, []) };
        var withoutAdditiveContent = JsonSerializer.SerializeToUtf8Bytes(
            new { code = shed.Code, path = shed.Path, validation = shed.Validation }, SizeProbeOptions).Length;
        var additiveBytes = JsonSerializer.SerializeToUtf8Bytes(shed, SizeProbeOptions).Length - withoutAdditiveContent;

        Assert.True(
            additiveBytes is > 0 and < 128,
            $"After a full shed the fields vouchfx-mcp#78 adds measured {additiveBytes} B. "
            + "RunSuiteTool.FitWithinBudget's remarks describe that as a constant on the order of "
            + "eighty bytes — neither zero (the counters and the empty array are still written) nor "
            + "content-dependent.");
    }

    /// <summary>
    /// vouchfx-mcp#78's classification rule: entries are DETERMINED-invalid suites only. A later
    /// failure whose validity was never determined is counted, never described.
    /// </summary>
    /// <remarks>
    /// The two counters are asserted to be independent, which is the point of their being two: the
    /// undetermined suite must not inflate <c>omittedInvalidSuiteCount</c> (which would claim this
    /// server knows the suite is invalid) and must not be silently dropped (which would claim it had
    /// nothing to report).
    /// </remarks>
    [Fact]
    public void AFailureWhoseValidityWasNeverDetermined_IsCountedRatherThanDescribed()
    {
        var determined = BuildFailures(suiteCount: 1, errorsPerSuite: 2, messageChars: 30)[0];
        var undetermined = new PreflightSuiteFailure(
            "/workspace/e2e/gone.e2e.yaml",
            new ValidateSuiteResult(
                false,
                [new SuiteValidationError(
                    VfxCodeCatalogue.SuiteFileNotFound, null, "The suite file does not exist.", null, null)]));

        var payload = RunSuiteTool.BuildInvalidSuitesPayload(
            new RunSuiteOutcome.SuiteInvalid([determined, undetermined], OmittedInvalidSuiteCount: 0));

        var entry = Assert.Single(payload.InvalidSuites);
        Assert.EndsWith("suite-000.e2e.yaml", entry.Path, StringComparison.Ordinal);
        Assert.Equal(1, payload.UndeterminedSuiteCount);
        Assert.Equal(0, payload.OmittedInvalidSuiteCount);

        // No trace of the undetermined suite's own code reaches the wire — it may be one this server
        // does not catalogue at all, which ValidationOutcomeRenderer fails CLOSED on.
        Assert.DoesNotContain(
            VfxCodeCatalogue.SuiteFileNotFound,
            payload.InvalidSuites.SelectMany(report => report.Errors).Select(error => error.Code));
    }

    /// <summary>
    /// The published catalogue page states these two bounds as bare numbers; this ties that prose to
    /// the constants so a future re-tune cannot leave the page quietly wrong.
    /// </summary>
    [Fact]
    public void TheVfxD1100PageQuotesTheRealBounds()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot.FullName, "docs", "errors", "VFX-D-1100.md"));

        Assert.Contains(
            $"at most {RunSuiteOrchestrator.MaxReportedInvalidSuites} suites and "
            + $"{RunSuiteTool.MaxErrorsPerInvalidSuite} findings per suite",
            page,
            StringComparison.Ordinal);
    }

    /// <summary>Mirrors <c>ErrorCatalogueFilesystemParityTests.RepoRoot</c> exactly — see its remarks.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");

            return testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");
        }
    }

    private static PreflightSuiteFailure[] BuildFailures(int suiteCount, int errorsPerSuite, int messageChars)
    {
        var failures = new PreflightSuiteFailure[suiteCount];

        for (var suite = 0; suite < suiteCount; suite++)
        {
            var errors = new SuiteValidationError[errorsPerSuite];
            for (var error = 0; error < errorsPerSuite; error++)
            {
                errors[error] = new SuiteValidationError(
                    VfxCodeCatalogue.SchemaViolation,
                    $"suite-{suite:D3}-error-{error:D2}",
                    new string('m', messageChars),
                    Line: error + 1,
                    Column: 1);
            }

            failures[suite] = new PreflightSuiteFailure(
                $"/workspace/e2e/suite-{suite:D3}.e2e.yaml",
                new ValidateSuiteResult(false, errors));
        }

        return failures;
    }
}
