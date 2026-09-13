using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Planning;
using Vouchfx.Mcp.Tools;

namespace Vouchfx.Mcp.Tests.Planning;

/// <summary>
/// Covers <see cref="PlanCoverageResponseBudget"/> (issue #41): the measured response budget that
/// keeps <c>plan_coverage</c>'s reply agent-readable on a repository large enough to produce hundreds
/// of findings, and the visible counters that say what it left out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every size assertion here is MEASURED</b> — the serialised byte count of a real candidate,
/// through the very options <c>StructuredToolResult</c> uses — never a hand-derived estimate. Where a
/// literal byte count would be machine-dependent (the <c>meta</c> stamp embeds <c>workspaceRoot</c>),
/// the assertion is on the relationship rather than the number, for exactly the reason
/// <c>ExplainRunOrchestratorTests</c> gives for the same choice.
/// </para>
/// </remarks>
public class PlanCoverageResponseBudgetTests
{
    /// <summary>
    /// The options the real wire uses. Deliberately NOT a fresh
    /// <c>new(JsonSerializerDefaults.Web)</c>: the production budget measures through
    /// <c>StructuredToolResult.Options</c>, so the test must measure through the same chain or it
    /// would be pinning a different number than the one enforced.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = StructuredToolResult.Options;

    /// <summary>
    /// The same options with a LOOSER encoder — the hypothetical the SDK's own frame writer might use.
    /// Exists only for <see cref="ARelaxedEncoderOnlyShrinksTheTextCopy"/>, which checks the direction
    /// the budget's "unobservable residue can only shrink" claim depends on.
    /// </summary>
    private static readonly JsonSerializerOptions RelaxedWireOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The engine-list positions the four non-flood findings occupy in
    /// <see cref="Truncation_KeepsTheMostActionableKinds_AndEmitsThemInEngineOrder"/> — ASCENDING,
    /// which is the point: band order there is their exact reverse.
    /// </summary>
    private static readonly int[] ExpectedEngineIndicesOfTheFourKeptFindings = [20, 40, 60, 80];

    /// <summary>
    /// A <c>workspaceRoot</c> long enough to represent a real deployment rather than whatever this
    /// machine's checkout happens to be: 95 characters, and a Windows path, so its ten separators
    /// each cost more than a plain character does. Longer than the 73-character root the peer review measured
    /// the earlier model's 106-byte cap breach against, so every envelope assertion here is at least
    /// as demanding as that measurement was, on any machine.
    /// </summary>
    private const string LongWorkspaceRoot =
        @"C:\Users\a-fairly-long-account-name\source\repos\acme-platform\services\orders\tests\e2e\suites";

    // ── The scaling case the issue names: a 50-suite x 10-step repo with no run history ─────────

    /// <summary>
    /// The headline case. A repository with no history produces one <c>suite-never-run</c> per suite
    /// and one <c>step-never-exercised</c> per step: 50 + 500 = 550 findings, which is what made
    /// <c>plan_coverage</c> the one tool whose reply scaled with the repository rather than with the
    /// run. The bounded reply must fit the budget, and <c>omittedFindingCount</c> must be the REAL
    /// remainder — measured against the returned list's own length, never a number this test asserts
    /// independently.
    /// </summary>
    [Fact]
    public void FiveHundredFindingReport_FitsTheBudget_AndCountsTheRealRemainder()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 50, stepsPerSuite: 10);
        Assert.Equal(550, report.Findings.Count);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        var payloadBytes = MeasuredResponseBytes(bounded);
        Assert.True(
            payloadBytes <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes,
            $"Expected the bounded payload within {PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes} bytes, got {payloadBytes}.");

        // The counter is the real remainder of the real list, not a constant.
        Assert.Equal(report.Findings.Count - bounded.Findings.Count, bounded.OmittedFindingCount);
        Assert.True(bounded.OmittedFindingCount > 0, "550 findings cannot all fit; something was expected to be omitted.");
        Assert.True(bounded.ResponseTruncated);

        // And the whole wire envelope — the thing a host's context window actually pays for — stays
        // under the public cap, measured against a deployment-length workspaceRoot rather than this
        // machine's. That is the claim the measured two-copy fit plus the allowance exists to make true.
        var envelopeBytes = EnvelopeByteCountWithMeta(bounded, LongWorkspaceRoot);
        Assert.True(
            envelopeBytes <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes,
            $"Expected the CallToolResult envelope within {PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes} bytes, got {envelopeBytes}.");
    }

    /// <summary>
    /// <b>The tripwire for the whole budget.</b> Drives a payload built from the densest escape
    /// content that exists — a <c>detail</c> of nothing but backslashes, every one of which costs two
    /// bytes in the structured copy and four in the escaped text copy — sized to FILL
    /// <see cref="PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes"/> exactly, under a deliberately
    /// long <c>workspaceRoot</c>. If the envelope fits the cap here, it fits for any report the engine
    /// can produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this fixture and not a realistic one.</b> A budget validated only against realistic
    /// content is validated against the easy case. Successive peer reviews measured two real cap
    /// breaches that realistic content never showed: a 106&#160;B one from <c>meta</c> being escaped
    /// and duplicated outside the budget's arithmetic, and a larger one from structural quotes. Both
    /// are closed now by MEASURING both wire copies rather than modelling the second.
    /// </para>
    /// <para>
    /// <b>This fixture covers ONE axis — escape-dense string CONTENT — and its empty inventory is
    /// deliberate scoping, not an oversight.</b> Emptying the inventory concentrates the whole payload
    /// into one enormous <c>detail</c>, which is what makes the backslash density extreme. The
    /// orthogonal axis — structural quote density, which needs all seven arrays POPULATED with short
    /// values — is covered by
    /// <see cref="QuoteDenseContentAcrossEveryArray_StaysUnderTheCap"/>, and it is the axis that
    /// actually broke the last model. Neither fixture subsumes the other.
    /// </para>
    /// <para>
    /// <b>The size is SEARCHED, not hardcoded</b> — the fixture grows until one more character would
    /// push the applied payload past the budget, so it re-derives itself if any constant here moves.
    /// A hardcoded length would silently stop filling the budget the first time a tier or a model
    /// field changed, and a tripwire that no longer touches the boundary is not a tripwire.
    /// </para>
    /// <para>
    /// <b>The <c>workspaceRoot</c> is supplied rather than taken from this machine.</b>
    /// <c>StructuredToolResult</c> caches the process's real <c>meta</c> at static-init, and this
    /// assertion must not be weaker on a machine that happens to have a short install path — so the
    /// envelope is assembled exactly the way <c>StructuredToolResult.Success</c> assembles it
    /// (<see cref="EnvelopeByteCountWithMeta"/>) with a root long enough to represent a real
    /// deployment. See that helper for the one thing this construction does NOT reproduce.
    /// </para>
    /// </remarks>
    [Fact]
    public void WorstCaseEscapeContent_FillsTheBudget_AndTheEnvelopeStaysUnderTheCap()
    {
        var backslashCount = LargestBackslashDetailThatFitsTheBudget();
        var report = SingleWorstCaseEscapeFindingReport(backslashCount);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        // Anti-vacuity: the search must have produced a payload that genuinely FILLS the budget with
        // the oversized finding still present. A fixture that collapsed to the floor, or one that
        // used a tenth of the budget, would pass the cap assertion below while testing nothing.
        Assert.Single(bounded.Findings);
        var payloadBytes = MeasuredResponseBytes(bounded);
        Assert.True(
            payloadBytes > PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes - 16,
            $"The fixture must fill the {PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes}-byte budget to be a worst case; it only reached {payloadBytes}.");
        Assert.True(payloadBytes <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes);

        AssertIndependentlyBuiltEnvelopeFitsTheCap(bounded, "all-backslash detail");
    }

    /// <summary>
    /// <b>The second worst-case axis: STRUCTURAL quotes.</b> Every property name and string value in
    /// the payload is delimited by a raw <c>"</c>, which the text copy escapes to the six-byte
    /// <c>\uXXXX</c> form — <b>1 byte in, 6 out</b>. A payload made of short identifiers across all
    /// seven bounded arrays is therefore ~34% quotes by byte, and costs far more in the text copy than
    /// any content-character analysis predicts.
    /// </summary>
    /// <remarks>
    /// <b>This fixture exists because a model missed it.</b> The budget's third revision derived a ×3
    /// ceiling from how the encoder escapes string CONTENT (a backslash being the worst content
    /// character at 3.000) and concluded no payload could exceed it. A peer review measured a
    /// quote-dense all-seven-arrays payload at roughly <b>68,150&#160;B</b> against the 65,536&#160;B
    /// cap — a real breach the model had declared impossible, because structural quotes are not
    /// content and were never in the enumeration. The budget now MEASURES both copies instead of
    /// modelling either, and this fixture is the axis that forced it.
    /// </remarks>
    [Fact]
    public void QuoteDenseContentAcrossEveryArray_StaysUnderTheCap()
    {
        var report = QuoteDenseReport();

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        // Anti-vacuity: this must exercise the populated shape, not a collapsed floor.
        Assert.NotEmpty(bounded.Findings);
        Assert.NotEmpty(bounded.Inventory.Suites);
        Assert.NotEmpty(bounded.Inventory.UnmappableDependencies);

        var measured = MeasuredResponseBytes(bounded);
        Assert.True(
            measured <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes,
            $"Expected the measured response within {PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes} bytes, got {measured}.");

        AssertIndependentlyBuiltEnvelopeFitsTheCap(bounded, "quote-dense all-array payload");
    }

    /// <summary>
    /// Builds the wire envelope INDEPENDENTLY of the production fit — real <c>CallToolResult</c>, real
    /// <c>meta</c> merge, deployment-length <c>workspaceRoot</c> — and asserts it fits the cap. This is
    /// the check the production measurement is supposed to imply; keeping it separate is what makes it
    /// evidence rather than a restatement.
    /// </summary>
    private static void AssertIndependentlyBuiltEnvelopeFitsTheCap(PlanCoverageResult bounded, string fixtureName)
    {
        var payloadBytes = PayloadByteCount(bounded);
        var envelopeBytes = EnvelopeByteCountWithMeta(bounded, LongWorkspaceRoot);
        var ratio = (double)envelopeBytes / payloadBytes;

        // The payload is still carried TWICE at ModelContextProtocol 2.2.0 — the premise the whole
        // budget rests on. A ratio at or below 2.0 would mean the SDK stopped duplicating it, which is
        // a contract change this budget must be re-derived for, not quietly benefit from.
        Assert.True(
            ratio > 2.0,
            $"[{fixtureName}] expected the payload to still be carried twice (ratio > 2.0), measured {ratio:F3}.");

        // No ratio CEILING is asserted any more, deliberately: the budget no longer models one. The
        // ratio is reported in the failure message as evidence, and the cap is what is enforced.
        Assert.True(
            envelopeBytes <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes,
            $"[{fixtureName}] expected the envelope within {PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes} bytes, "
            + $"got {envelopeBytes} (payload {payloadBytes}, ratio {ratio:F3}, workspaceRoot {LongWorkspaceRoot.Length} chars).");
    }

    /// <summary>
    /// The ORDINARY case, recorded separately from the worst case above so the two are never
    /// confused: on a realistic <c>step-never-exercised</c> report the envelope-to-payload ratio is
    /// far below what the two adversarial fixtures measure.
    /// </summary>
    /// <remarks>
    /// This measurement is EVIDENCE that the doubling still happens at 2.2.0, and nothing more. It is
    /// deliberately not the number any constant is derived from — a model built on it breaches the cap
    /// on escape-dense content, which is the mistake
    /// <see cref="WorstCaseEscapeContent_FillsTheBudget_AndTheEnvelopeStaysUnderTheCap"/> exists to
    /// prevent. The bound below is loose for the same reason it is loose in
    /// <c>ExplainRunOrchestratorTests</c>: <c>meta</c> embeds <c>workspaceRoot</c>, whose length
    /// differs per machine, so only the relationship is machine-independent.
    /// </remarks>
    [Fact]
    public void RealisticReport_IsStillCarriedTwice_AtTheOrdinaryRatio()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 50, stepsPerSuite: 10);
        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        var payloadBytes = PayloadByteCount(bounded);
        var envelopeBytes = EnvelopeByteCountWithMeta(bounded, LongWorkspaceRoot);
        var ratio = (double)envelopeBytes / payloadBytes;

        Assert.True(ratio > 2.0, $"Expected the payload to still be carried twice (ratio > 2.0), measured {ratio:F3}.");

        // An ORDINARY report sits well under the worst cases the two adversarial fixtures drive, which
        // is the only reason this number is recorded: it shows the budget is not tuned to the typical
        // case. No ceiling is asserted — the budget no longer models one.
        Assert.True(ratio < 3.0, $"Expected the ORDINARY ratio to sit below the adversarial fixtures' cost, got {ratio:F3}.");
        Assert.True(
            envelopeBytes <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes,
            $"Expected the envelope within {PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes} bytes, got {envelopeBytes}.");
    }

    /// <summary>
    /// The allowance's own arithmetic, checked directly rather than only through the envelope:
    /// <c>meta + escaped(meta) + wrapper ≤ EnvelopeOverheadAllowance</c> — the inequality
    /// <see cref="PlanCoverageResponseBudget.EnvelopeOverheadAllowance"/>'s remarks state, and the one
    /// a new <c>meta</c> field or a longer install path would break first.
    /// </summary>
    /// <remarks>
    /// Both <c>meta</c> terms, because the production fit measures the PAYLOAD's two copies and
    /// <c>meta</c> rides in both of them. Escaping is per-character, so the two halves are separable
    /// and this inequality is exactly the residue the fit does not account for.
    /// </remarks>
    [Fact]
    public void EnvelopeOverheadAllowance_CoversBothMetaCopiesPlusTheWrapper()
    {
        var metaChunkBytes = MetaChunkByteCount(LongWorkspaceRoot);
        var escapedMetaBytes = EscapedMetaChunkByteCount(LongWorkspaceRoot);
        var wrapperBytes = WrapperByteCount();

        var required = metaChunkBytes + escapedMetaBytes + wrapperBytes;

        Assert.True(
            required <= PlanCoverageResponseBudget.EnvelopeOverheadAllowance,
            $"meta ({metaChunkBytes} B) + escaped meta ({escapedMetaBytes} B) + wrapper ({wrapperBytes} B) "
            + $"= {required} B exceeds the {PlanCoverageResponseBudget.EnvelopeOverheadAllowance} B allowance, "
            + $"measured against a {LongWorkspaceRoot.Length}-character workspaceRoot. Raise the allowance "
            + "rather than shortening the fixture.");
    }

    /// <summary>
    /// The direction claim the budget's remarks rest on: the MCP SDK writes the final frame under its
    /// own encoder, which this server cannot observe — but a LOOSER encoder can only make the text
    /// copy SMALLER, so the cap stays safe if the SDK escapes less than this measurement assumes.
    /// </summary>
    /// <remarks>
    /// Verified rather than asserted from intuition: <c>JavaScriptEncoder.Default</c> writes a quote
    /// as the six-byte <c>\uXXXX</c> form while <c>UnsafeRelaxedJsonEscaping</c> writes the
    /// two-character form, so on a quote-dense payload the relaxed copy is strictly shorter. If that
    /// direction ever inverted, the budget would be measuring the CHEAP case and the cap claim would
    /// need re-deriving — which is why this is a test and not a sentence.
    /// </remarks>
    [Fact]
    public void ARelaxedEncoderOnlyShrinksTheTextCopy()
    {
        var report = QuoteDenseReport();
        var payloadJson = JsonSerializer.Serialize(report, typeof(PlanCoverageResult), WireOptions);

        var underDefault = JsonSerializer.SerializeToUtf8Bytes(payloadJson, typeof(string), WireOptions).Length;

        var underRelaxed = JsonSerializer.SerializeToUtf8Bytes(payloadJson, typeof(string), RelaxedWireOptions).Length;

        Assert.True(
            underRelaxed < underDefault,
            $"Expected a relaxed encoder to shrink the text copy, measured {underRelaxed} B relaxed vs "
            + $"{underDefault} B default. If this inverts, the budget measures the cheap case and the "
            + "cap claim must be re-derived against the expensive one.");
    }

    // ── Pass-through: nothing omitted means no bound was applied, and the counters say so ───────

    /// <summary>
    /// A small report is relayed untouched: same findings, same ORDER, and every counter zero.
    /// </summary>
    /// <remarks>
    /// <b>Zero, not absent.</b> The counters are plain <c>int</c>s that always serialise, which is the
    /// shape <c>diagnose_run</c>'s own <c>omittedProposalCount</c>/<c>omittedSpecEditProposalCount</c>
    /// already have — deliberately matched rather than invented, so a host reads one convention across
    /// the server. An omitted property and a zero would be two ways to say the same thing, and the
    /// existing one wins.
    /// </remarks>
    [Fact]
    public void SmallReport_IsRelayedUntouched_WithEveryCounterZero()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 2, stepsPerSuite: 2);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        Assert.Equal(report.Findings.Count, bounded.Findings.Count);
        Assert.Equal(
            report.Findings.Select(f => f.Kind + "|" + f.StepId),
            bounded.Findings.Select(f => f.Kind + "|" + f.StepId));
        Assert.Equal(0, bounded.OmittedFindingCount);
        Assert.False(bounded.ResponseTruncated);

        AssertEveryInventoryCounterIsZero(bounded);

        // Zero, not absent — pinned on the serialised form, which is what a host actually sees.
        var json = JsonSerializer.SerializeToElement(bounded, typeof(PlanCoverageResult), WireOptions);
        Assert.Equal(0, json.GetProperty("omittedFindingCount").GetInt32());
        Assert.False(json.GetProperty("responseTruncated").GetBoolean());
        Assert.Equal(0, json.GetProperty("inventory").GetProperty("omittedSuiteCount").GetInt32());
    }

    /// <summary>A report with no findings at all is the same pass-through, with an empty list rather than a null one.</summary>
    [Fact]
    public void ZeroFindingReport_StaysEmpty_WithEveryCounterZero()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 0, stepsPerSuite: 0);
        Assert.Empty(report.Findings);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        Assert.Empty(bounded.Findings);
        Assert.Equal(0, bounded.OmittedFindingCount);
        Assert.False(bounded.ResponseTruncated);
        AssertEveryInventoryCounterIsZero(bounded);
    }

    // ── Truncation order: prioritised SELECTION, engine EMISSION order ──────────────────────────

    /// <summary>
    /// When findings must be dropped, the scarce, evidence-backed, root-cause kinds survive and the
    /// two flood kinds are shed first — and the survivors are still emitted in the engine's own order
    /// (EDGE-005), not re-sorted into band order.
    /// </summary>
    [Fact]
    public void Truncation_KeepsTheMostActionableKinds_AndEmitsThemInEngineOrder()
    {
        // Interleaved so an "engine order" emission and a "band order" emission cannot coincide.
        var findings = new List<PlanCoverageFinding>();
        for (var i = 0; i < 100; i++)
        {
            findings.Add(Finding("step-never-exercised", $"flood-{i:D3}"));
        }

        // Placed so BAND order and ENGINE order are exact reverses of one another: the lowest band
        // (suite-identity-ambiguous) sits LAST in the engine's list. An emission that re-sorted by
        // band would come out in the opposite order to the one asserted below.
        findings.Insert(20, Finding("suite-never-run", "suite-1"));
        findings.Insert(40, Finding("dependency-not-asserted", "dep-1"));
        findings.Insert(60, Finding("step-flaky", "flaky-1"));
        findings.Insert(80, Finding("suite-identity-ambiguous", "ambiguous-1"));

        var report = ReportWith(findings);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: 4);

        Assert.Equal(4, bounded.Findings.Count);
        Assert.Equal(findings.Count - 4, bounded.OmittedFindingCount);

        // The four bands ahead of the flood, all kept.
        Assert.Contains(bounded.Findings, f => f.StepId == "ambiguous-1");
        Assert.Contains(bounded.Findings, f => f.StepId == "flaky-1");
        Assert.Contains(bounded.Findings, f => f.StepId == "dep-1");
        Assert.Contains(bounded.Findings, f => f.StepId == "suite-1");
        Assert.DoesNotContain(bounded.Findings, f => f.Kind == "step-never-exercised");

        // EMISSION order is the engine's own index order, NOT the band order that chose them. Band
        // order here is the exact reverse, so an implementation that emitted in band order would
        // produce a strictly DESCENDING index sequence and fail this.
        var engineIndices = bounded.Findings
            .Select(kept => findings.FindIndex(f => ReferenceEquals(f, kept)))
            .ToArray();
        Assert.Equal(ExpectedEngineIndicesOfTheFourKeptFindings, engineIndices);
    }

    /// <summary>
    /// A kind this build does not recognise outranks the two flood kinds rather than sorting last —
    /// so a future engine's new finding is not the first thing a truncated response loses.
    /// </summary>
    [Fact]
    public void Truncation_KeepsAnUnrecognisedKind_AheadOfTheFloodKinds()
    {
        var findings = new List<PlanCoverageFinding>();
        for (var i = 0; i < 50; i++)
        {
            findings.Add(Finding("step-never-exercised", $"flood-{i:D3}"));
        }

        findings.Add(Finding("suite-never-run", "suite-1"));
        findings.Add(Finding("a-kind-from-a-future-engine", "future-1"));

        var bounded = PlanCoverageResponseBudget.Apply(ReportWith(findings), maxFindings: 1);

        var kept = Assert.Single(bounded.Findings);
        Assert.Equal("future-1", kept.StepId);
    }

    /// <summary>
    /// The same input truncates to the same output every time — the property that makes a bounded
    /// response reviewable at all. Deliberately a repeat-call assertion rather than a golden: a golden
    /// pins the CURRENT band table, this pins that the table is applied deterministically.
    /// </summary>
    [Fact]
    public void Truncation_IsDeterministicAcrossRepeatedCalls()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 50, stepsPerSuite: 10);

        var first = PlanCoverageResponseBudget.Apply(report, maxFindings: null);
        var second = PlanCoverageResponseBudget.Apply(report, maxFindings: null);
        var third = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        var firstJson = JsonSerializer.Serialize(first, typeof(PlanCoverageResult), WireOptions);
        Assert.Equal(firstJson, JsonSerializer.Serialize(second, typeof(PlanCoverageResult), WireOptions));
        Assert.Equal(firstJson, JsonSerializer.Serialize(third, typeof(PlanCoverageResult), WireOptions));
    }

    // ── maxFindings: lowers, never raises ───────────────────────────────────────────────────────

    [Fact]
    public void MaxFindings_LowersTheCountBelowWhatTheLadderWouldHaveReturned()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 50, stepsPerSuite: 10);

        var unbounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);
        var requested = PlanCoverageResponseBudget.Apply(report, maxFindings: 3);

        Assert.True(unbounded.Findings.Count > 3, "The ladder was expected to allow more than 3 findings here.");
        Assert.Equal(3, requested.Findings.Count);
        Assert.Equal(report.Findings.Count - 3, requested.OmittedFindingCount);
        Assert.True(requested.ResponseTruncated);
    }

    /// <summary>
    /// The ceiling case: asking for the maximum the argument accepts does NOT lift the ladder's own
    /// cap. <c>maxFindings</c> is a request for fewer, and this is the assertion that makes "it can
    /// never raise the budget" a measured property rather than a sentence in a doc comment.
    /// </summary>
    [Fact]
    public void MaxFindings_AtItsCeiling_DoesNotRaiseTheLaddersOwnCap()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 50, stepsPerSuite: 10);

        var unbounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);
        var atCeiling = PlanCoverageResponseBudget.Apply(
            report, maxFindings: PlanCoverageResponseBudget.MaxRequestedFindings);

        Assert.Equal(unbounded.Findings.Count, atCeiling.Findings.Count);
        Assert.True(
            atCeiling.Findings.Count < PlanCoverageResponseBudget.MaxRequestedFindings,
            "The ladder, not the caller's ceiling, is what bounded this report.");
        Assert.True(MeasuredResponseBytes(atCeiling) <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes);
    }

    // NOTE: there is deliberately NO test here asserting "a response never carries more than 150
    // findings". One existed and was deleted: a minimal finding is 201 B, so the byte budget holds
    // any input to ~103 findings and the assertion could not fail for any input — raising Tiers[0]
    // to 200 would not have changed a single observable value. It was documentation wearing a test's
    // clothes. The ceiling is now guaranteed STRUCTURALLY instead, by Tiers[0] being written as
    // (MaxRequestedFindings, MaxRequestedFindings), and the boundary that genuinely can move is
    // pinned by the test below.

    /// <summary>
    /// Which of the two ceilings actually binds, MEASURED — because the answer turned out not to be
    /// the one the contract's wording implies, and a number nobody has measured is a number nobody
    /// should quote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="PlanCoverageFinding"/> has a floor size: the record's twelve properties all
    /// serialise even when null (<c>StructuredToolResult.Options</c> deliberately sets no
    /// <c>DefaultIgnoreCondition</c> — see its remarks for why that would reshape every tool's wire
    /// format), so the smallest possible finding is ~200&#160;B and 150 of them cannot fit
    /// <see cref="PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes"/>. MEASURED: 300 minimal findings
    /// come back as <b>75</b> — the ladder's SECOND rung, reached because the first (150) failed its
    /// byte measurement. So <b>the byte budget binds before the 150-finding rung ever does</b>: 150 is
    /// a true upper bound on every response, but it is not an attainable count, and the operative
    /// limit is always a lower rung.
    /// </para>
    /// <para>
    /// That is worth pinning rather than leaving implicit, and it qualifies the contract wording
    /// honestly: the published ceiling is true but LOOSE, and <c>Tiers[0]</c> is currently
    /// unreachable. A future change that shrank the finding record — or omitted its nulls — could make
    /// 150 the operative limit for the first time, and this test is where that shift becomes visible
    /// rather than silent.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheByteBudget_BindsBeforeTheFindingCeiling_OnMinimalFindings()
    {
        var findings = Enumerable.Range(0, 300).Select(_ => MinimalFinding()).ToList();

        var bounded = PlanCoverageResponseBudget.Apply(ReportWith(findings), maxFindings: null);

        Assert.True(
            bounded.Findings.Count < PlanCoverageResponseBudget.MaxRequestedFindings,
            $"Expected the byte budget to bind first, but {bounded.Findings.Count} findings came back "
            + $"against a {PlanCoverageResponseBudget.MaxRequestedFindings} ceiling. If the finding "
            + "record shrank, the ceiling is now the operative limit — update this test and the "
            + "contract wording together.");

        // The exact rung, pinned: 75 is Tiers[1], reached because Tiers[0] (150) failed its byte
        // measurement. Asserting the NUMBER rather than only the inequality is what makes a change to
        // the finding record's size — or to the ladder's rungs — show up here as a specific, readable
        // delta instead of passing silently at some other rung.
        Assert.Equal(75, bounded.Findings.Count);
        Assert.True(MeasuredResponseBytes(bounded) <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes);
    }

    /// <summary>A <c>maxFindings</c> larger than the report's own finding count omits nothing.</summary>
    [Fact]
    public void MaxFindings_AboveTheReportsOwnCount_OmitsNothing()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 2, stepsPerSuite: 2);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: 100);

        Assert.Equal(report.Findings.Count, bounded.Findings.Count);
        Assert.Equal(0, bounded.OmittedFindingCount);
        Assert.False(bounded.ResponseTruncated);
    }

    // ── Inventory: the arrays that scale with the repository are bounded too ────────────────────

    /// <summary>
    /// The inventory scales with the repository independently of the findings, so it carries its own
    /// six bounds and its own six counters. Driven by a report whose inventory alone would blow the
    /// budget even with zero findings.
    /// </summary>
    [Fact]
    public void LargeInventory_IsBounded_WithItsOwnVisibleCounters()
    {
        var inventory = new PlanCoverageInventory(
            Suites: Enumerable.Range(0, 400)
                .Select(i => new PlanCoverageSuiteEntry($"suites/area-{i:D4}/checkout.e2e.yaml", $"checkout-{i:D4}", $"checkout-{i:D4}", 10))
                .ToList(),
            Services: Enumerable.Range(0, 400).Select(i => $"service-{i:D4}").ToList(),
            Dependencies: Enumerable.Range(0, 400)
                .Select(i => new PlanCoverageDependencyEntry($"orders-db-{i:D4}", "postgres", $"suites/area-{i:D4}/checkout.e2e.yaml"))
                .ToList(),
            StepTypes: Enumerable.Range(0, 400).Select(i => $"db-assert.provider-{i:D4}").ToList(),
            RunCount: 0,
            FirstEventTs: null,
            LastEventTs: null,
            SkippedEventLines: 0,
            UnmatchedObservations: 0,
            UnanalysableSuites: Enumerable.Range(0, 400)
                .Select(i => new PlanCoverageUnanalysableSuite($"suites/broken-{i:D4}.e2e.yaml", "Suite failed to parse: unexpected token."))
                .ToList(),
            UnmappableDependencies: Enumerable.Range(0, 400)
                .Select(i => new PlanCoverageUnmappableDependency($"queue-{i:D4}", "rabbitmq", "No candidate asserting step type is registered for this kind.", $"suites/area-{i:D4}/checkout.e2e.yaml"))
                .ToList());

        var report = new PlanCoverageResult(
            SchemaVersion: 1,
            EngineVersion: "1.0.0-test",
            Thresholds: new PlanCoverageThresholds(30, 2, 2, 2),
            Inventory: inventory,
            Findings: []);

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        Assert.True(MeasuredResponseBytes(bounded) <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes);
        Assert.True(bounded.ResponseTruncated);

        // Every one of the six arrays was shortened, and every one says by how much — the house rule
        // that a bound applied must be visible in the output.
        var actual = bounded.Inventory;
        Assert.Equal(400 - actual.Suites.Count, actual.OmittedSuiteCount);
        Assert.Equal(400 - actual.Services.Count, actual.OmittedServiceCount);
        Assert.Equal(400 - actual.Dependencies.Count, actual.OmittedDependencyCount);
        Assert.Equal(400 - actual.StepTypes.Count, actual.OmittedStepTypeCount);
        Assert.Equal(400 - actual.UnanalysableSuites.Count, actual.OmittedUnanalysableSuiteCount);
        Assert.Equal(400 - actual.UnmappableDependencies.Count, actual.OmittedUnmappableDependencyCount);
        Assert.True(actual.OmittedSuiteCount > 0);

        // Kept entries are the engine's own, in the engine's own order — a prefix, never a re-sort.
        Assert.Equal(inventory.Suites.Take(actual.Suites.Count), actual.Suites);
    }

    // ── The floor, and the one thing below it ──────────────────────────────────────────────────

    /// <summary>
    /// The floor tier empties every list, leaving <c>engineVersion</c> as the only variable-length
    /// field this server does not author. An engine that stamped something enormous there would breach
    /// the budget with no list left to shed, which is the case the minimal shape exists for: it caps
    /// that field and the response still fits.
    /// </summary>
    [Fact]
    public void EnormousEngineVersion_FallsBelowTheFloor_AndStillFits()
    {
        var report = NoHistoryRepositoryReport(suiteCount: 50, stepsPerSuite: 10) with
        {
            EngineVersion = new string('v', 100_000),
        };

        var bounded = PlanCoverageResponseBudget.Apply(report, maxFindings: null);

        Assert.True(
            MeasuredResponseBytes(bounded) <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes,
            "The minimal shape must fit even when the engine's own version stamp is pathological.");
        Assert.Empty(bounded.Findings);
        Assert.Equal(550, bounded.OmittedFindingCount);
        Assert.True(bounded.ResponseTruncated);
        Assert.Equal(64, bounded.EngineVersion!.Length);

        // The counters still report the FULL source counts — "there were 550 and none are here".
        Assert.Equal(50, bounded.Inventory.OmittedSuiteCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static void AssertEveryInventoryCounterIsZero(PlanCoverageResult result)
    {
        var inventory = result.Inventory;
        Assert.Equal(0, inventory.OmittedSuiteCount);
        Assert.Equal(0, inventory.OmittedServiceCount);
        Assert.Equal(0, inventory.OmittedDependencyCount);
        Assert.Equal(0, inventory.OmittedStepTypeCount);
        Assert.Equal(0, inventory.OmittedUnanalysableSuiteCount);
        Assert.Equal(0, inventory.OmittedUnmappableDependencyCount);
    }

    /// <summary>
    /// The number the production fit enforces: both wire copies measured exactly, plus the overhead
    /// allowance. Delegates to the production helper rather than re-deriving it — a test that computed
    /// its own version of the budget would be pinning the test's arithmetic, not the server's.
    /// </summary>
    private static int MeasuredResponseBytes(PlanCoverageResult result) =>
        PlanCoverageResponseBudget.MeasuredResponseBytes(result);

    private static int PayloadByteCount(PlanCoverageResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, typeof(PlanCoverageResult), WireOptions).Length;

    /// <summary>
    /// The FULL wire envelope with a CALLER-SUPPLIED <c>workspaceRoot</c> in <c>meta</c>, assembled
    /// exactly the way <c>StructuredToolResult.Success</c> assembles it: payload object, <c>meta</c>
    /// appended as its last property, that merged object carried twice — once as the raw text of a
    /// <c>TextContentBlock</c> and once as <c>StructuredContent</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not just call <c>StructuredToolResult.Success</c>.</b> It stamps the process's own
    /// <c>meta</c>, cached at static-init from this machine's install path — so on a machine with a
    /// short path the cap assertions would be measuring a smaller <c>meta</c> than a real deployment
    /// carries, and the test would be weakest exactly where the reviewer measured the breach. The
    /// duplication and merge logic reproduced here is the same; only the root is controlled.
    /// </para>
    /// <para>
    /// <b>What this does NOT reproduce:</b> the MCP SDK's own JSON-RPC framing and writer options,
    /// which this server neither owns nor can observe. This is therefore a close MODEL of the wire
    /// rather than the wire itself — the same standing <c>ExplainRunOrchestratorTests</c>' envelope
    /// probe has, and the reason <c>EnvelopeOverheadAllowance</c> reserves headroom for the wrapper
    /// instead of claiming to have counted it exactly.
    /// </para>
    /// </remarks>
    private static int EnvelopeByteCountWithMeta(PlanCoverageResult result, string workspaceRoot) =>
        JsonSerializer.SerializeToUtf8Bytes(EnvelopeWithMeta(result, workspaceRoot), WireOptions).Length;

    private static CallToolResult EnvelopeWithMeta(PlanCoverageResult result, string workspaceRoot)
    {
        var merged = MergeMeta(result, workspaceRoot);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = merged.GetRawText() }],
            StructuredContent = merged,
        };
    }

    /// <summary>The payload object with <c>meta</c> appended last — <c>StructuredToolResult.SerialiseWithMeta</c>'s shape.</summary>
    private static JsonElement MergeMeta(PlanCoverageResult result, string workspaceRoot)
    {
        var payload = JsonSerializer.SerializeToElement(result, typeof(PlanCoverageResult), WireOptions);
        var meta = new ToolMeta(SchemaVersion: "1", ServerVersion: "0.1.0", WorkspaceRoot: workspaceRoot);

        // Configured FROM the production options rather than left at Utf8JsonWriter's defaults, for
        // the reason StructuredToolResult.SerialiseWithMeta gives for doing the same: the encoder and
        // indentation are what can change the bytes, and a measurement written under different ones
        // would not be measuring the wire. They agree today, so this is a no-op that stays correct if
        // Options ever gains a custom encoder — which is precisely the change the escape arithmetic in
        // PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes' remarks is conditional on.
        var writerOptions = new JsonWriterOptions
        {
            Encoder = WireOptions.Encoder,
            Indented = WireOptions.WriteIndented,
        };

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
        {
            writer.WriteStartObject();
            foreach (var property in payload.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WritePropertyName("meta");
            JsonSerializer.SerializeToElement(meta, typeof(ToolMeta), WireOptions).WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The bytes <c>meta</c> adds to the payload object: the difference between the merged object and
    /// the payload alone, i.e. the <c>,"meta":{…}</c> chunk, measured rather than assumed.
    /// </summary>
    private static int MetaChunkByteCount(string workspaceRoot)
    {
        var bare = EmptyReport();
        var payloadBytes = PayloadByteCount(bare);

        // Encoding.UTF8.GetByteCount, not string.Length: GetRawText() returns a string, whose Length
        // is UTF-16 CODE UNITS, and subtracting that from a UTF-8 BYTE count would silently mix units.
        // They coincide today (the encoder is ASCII-only, so every char is one byte) — which is
        // exactly why the mismatch would have gone unnoticed until an encoder change made both this
        // measurement and the budget it feeds wrong at the same time.
        var mergedBytes = Encoding.UTF8.GetByteCount(MergeMeta(bare, workspaceRoot).GetRawText());

        return mergedBytes - payloadBytes;
    }

    /// <summary>
    /// The <c>CallToolResult</c> wrapper's own bytes: the envelope around an EMPTY structured object,
    /// less the two copies of that object and its escaping. Measured the same way for the same reason.
    /// </summary>
    private static int WrapperByteCount()
    {
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var envelope = new CallToolResult
        {
            Content = [new TextContentBlock { Text = empty.GetRawText() }],
            StructuredContent = empty,
        };

        // "{}" appears twice: once raw as StructuredContent, once as the escaped text (which, for
        // "{}", escapes to itself). Subtracting both leaves the wrapper's own fields.
        return JsonSerializer.SerializeToUtf8Bytes(envelope, WireOptions).Length - (2 * 2);
    }

    /// <summary>
    /// The largest all-backslash <c>detail</c> whose applied payload still fits the budget — binary
    /// searched so the fixture re-derives itself whenever a constant moves. The upper bound is the
    /// budget itself: each backslash costs at least one payload byte, so no larger count can fit.
    /// </summary>
    private static int LargestBackslashDetailThatFitsTheBudget()
    {
        var low = 1;
        var high = PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes;

        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            var applied = PlanCoverageResponseBudget.Apply(SingleWorstCaseEscapeFindingReport(mid), maxFindings: null);

            // Both conditions matter: a candidate that collapsed to the floor "fits" trivially and
            // must not be treated as a success, or the search would converge on a fixture with no
            // finding in it at all.
            if (applied.Findings.Count == 1 && MeasuredResponseBytes(applied) <= PlanCoverageResponseBudget.MaxPlanCoverageResponseBytes)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// A report carrying exactly one finding whose <c>detail</c> is nothing but backslashes — the
    /// densest JSON escape content there is, and therefore the worst case for the doubled, escaped
    /// text copy the fit measures.
    /// </summary>
    private static PlanCoverageResult SingleWorstCaseEscapeFindingReport(int backslashCount) =>
        ReportWith([
            new PlanCoverageFinding(
                Kind: "step-never-exercised",
                Suite: "suites/checkout.e2e.yaml",
                StepId: "assert-order-row",
                Target: null,
                TargetKind: null,
                SuggestedTypes: ["db-assert.postgres"],
                SuggestedStepId: "assert-order-row",
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: new string('\\', backslashCount),
                RelatedSuites: []),
        ]);

    private static PlanCoverageResult EmptyReport() => ReportWith([]);

    /// <summary>
    /// The quote-dense worst case: all seven bounded arrays populated with SHORT identifiers, so the
    /// payload's bytes are dominated by structural <c>"</c> delimiters rather than by content. Short
    /// values maximise the quote-to-content ratio — every extra field boundary is another 1-in/6-out
    /// character in the text copy.
    /// </summary>
    private static PlanCoverageResult QuoteDenseReport()
    {
        const int Count = 400;

        var findings = Enumerable.Range(0, Count).Select(i => new PlanCoverageFinding(
            Kind: "step-flaky",
            Suite: $"s/{i:D3}.y",
            StepId: $"s{i:D3}",
            Target: $"t{i:D3}",
            TargetKind: "dependency",
            SuggestedTypes: ["a.b", "c.d"],
            SuggestedStepId: $"x{i:D3}",
            Ambiguous: false,
            AmbiguityReason: null,
            History: null,
            Detail: $"d{i:D3}",
            RelatedSuites: [$"r{i:D3}"])).ToList();

        var inventory = new PlanCoverageInventory(
            Suites: Enumerable.Range(0, Count)
                .Select(i => new PlanCoverageSuiteEntry($"s/{i:D3}.y", $"c{i:D3}", $"n{i:D3}", 3)).ToList(),
            Services: Enumerable.Range(0, Count).Select(i => $"v{i:D3}").ToList(),
            Dependencies: Enumerable.Range(0, Count)
                .Select(i => new PlanCoverageDependencyEntry($"d{i:D3}", "pg", $"s/{i:D3}.y")).ToList(),
            StepTypes: Enumerable.Range(0, Count).Select(i => $"a.p{i:D3}").ToList(),
            RunCount: 0,
            FirstEventTs: null,
            LastEventTs: null,
            SkippedEventLines: 0,
            UnmatchedObservations: 0,
            UnanalysableSuites: Enumerable.Range(0, Count)
                .Select(i => new PlanCoverageUnanalysableSuite($"b/{i:D3}.y", $"e{i:D3}")).ToList(),
            UnmappableDependencies: Enumerable.Range(0, Count)
                .Select(i => new PlanCoverageUnmappableDependency($"q{i:D3}", "mq", $"w{i:D3}", $"s/{i:D3}.y")).ToList());

        return new PlanCoverageResult(
            SchemaVersion: 1,
            EngineVersion: "1.0.0-test",
            Thresholds: new PlanCoverageThresholds(30, 2, 2, 2),
            Inventory: inventory,
            Findings: findings);
    }

    /// <summary>The bytes the <c>meta</c> chunk costs in the ESCAPED text copy — the second of the two copies it rides in.</summary>
    private static int EscapedMetaChunkByteCount(string workspaceRoot)
    {
        var bare = EmptyReport();

        var payloadEscaped = JsonSerializer.SerializeToUtf8Bytes(
            JsonSerializer.Serialize(bare, typeof(PlanCoverageResult), WireOptions), typeof(string), WireOptions).Length;
        var mergedEscaped = JsonSerializer.SerializeToUtf8Bytes(
            MergeMeta(bare, workspaceRoot).GetRawText(), typeof(string), WireOptions).Length;

        return mergedEscaped - payloadEscaped;
    }

    /// <summary>
    /// The smallest finding the wire shape permits: every optional field null, every list empty, a
    /// one-character <c>kind</c> and an empty <c>detail</c>. Still ~200&#160;B, because all twelve
    /// properties serialise regardless — which is the fact
    /// <see cref="TheByteBudget_BindsBeforeTheFindingCeiling_OnMinimalFindings"/> exists to pin.
    /// </summary>
    private static PlanCoverageFinding MinimalFinding() =>
        new(
            Kind: "x",
            Suite: null,
            StepId: null,
            Target: null,
            TargetKind: null,
            SuggestedTypes: [],
            SuggestedStepId: null,
            Ambiguous: false,
            AmbiguityReason: null,
            History: null,
            Detail: string.Empty,
            RelatedSuites: []);

    /// <summary>
    /// The shape issue #41 names: a repository with no run history, where the engine emits one
    /// <c>suite-never-run</c> per suite and one <c>step-never-exercised</c> per step. The kinds, field
    /// population and <c>detail</c> phrasing are MEASURED from the pinned engine's own
    /// <c>vouchfx plan --json</c> output at v1.0.0-rc.5, not invented, so the per-finding byte cost
    /// this drives the budget with is the real one.
    /// </summary>
    private static PlanCoverageResult NoHistoryRepositoryReport(int suiteCount, int stepsPerSuite)
    {
        var suites = new List<PlanCoverageSuiteEntry>();
        var findings = new List<PlanCoverageFinding>();

        for (var suite = 0; suite < suiteCount; suite++)
        {
            var path = $"suites/area-{suite:D2}/checkout.e2e.yaml";
            suites.Add(new PlanCoverageSuiteEntry(path, $"checkout-flow-{suite:D2}", $"checkout-flow-{suite:D2}", stepsPerSuite));

            findings.Add(new PlanCoverageFinding(
                Kind: "suite-never-run",
                Suite: path,
                StepId: null,
                Target: null,
                TargetKind: null,
                SuggestedTypes: [],
                SuggestedStepId: null,
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: $"Suite '{path}' never appears in the analysed event history.",
                RelatedSuites: []));

            for (var step = 0; step < stepsPerSuite; step++)
            {
                var stepId = $"assert-order-row-{step:D2}";
                findings.Add(new PlanCoverageFinding(
                    Kind: "step-never-exercised",
                    Suite: path,
                    StepId: stepId,
                    Target: null,
                    TargetKind: null,
                    SuggestedTypes: ["db-assert.postgres"],
                    SuggestedStepId: stepId,
                    Ambiguous: false,
                    AmbiguityReason: null,
                    History: null,
                    Detail: $"Step '{stepId}' has no step event attributed to it.",
                    RelatedSuites: []));
            }
        }

        return new PlanCoverageResult(
            SchemaVersion: 1,
            EngineVersion: "1.0.0-rc.5+cc5e8efa9c84f59e1135568456f7c156261f6263",
            Thresholds: new PlanCoverageThresholds(30, 2, 2, 2),
            Inventory: new PlanCoverageInventory(
                Suites: suites,
                Services: [],
                Dependencies: [],
                StepTypes: ["db-assert.postgres", "http.rest"],
                RunCount: 0,
                FirstEventTs: null,
                LastEventTs: null,
                SkippedEventLines: 0,
                UnmatchedObservations: 0,
                UnanalysableSuites: [],
                UnmappableDependencies: []),
            Findings: findings);
    }

    private static PlanCoverageResult ReportWith(IReadOnlyList<PlanCoverageFinding> findings) =>
        new(
            SchemaVersion: 1,
            EngineVersion: "1.0.0-test",
            Thresholds: new PlanCoverageThresholds(30, 2, 2, 2),
            Inventory: new PlanCoverageInventory(
                Suites: [],
                Services: [],
                Dependencies: [],
                StepTypes: [],
                RunCount: 0,
                FirstEventTs: null,
                LastEventTs: null,
                SkippedEventLines: 0,
                UnmatchedObservations: 0,
                UnanalysableSuites: [],
                UnmappableDependencies: []),
            Findings: findings);

    private static PlanCoverageFinding Finding(string kind, string stepId) =>
        new(
            Kind: kind,
            Suite: "suites/checkout.e2e.yaml",
            StepId: stepId,
            Target: null,
            TargetKind: null,
            SuggestedTypes: [],
            SuggestedStepId: null,
            Ambiguous: false,
            AmbiguityReason: null,
            History: null,
            Detail: $"Synthetic '{kind}' finding for the truncation-order tests.",
            RelatedSuites: []);
}
