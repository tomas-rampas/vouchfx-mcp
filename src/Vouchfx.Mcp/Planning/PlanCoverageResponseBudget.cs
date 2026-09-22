// Vouchfx.Mcp.Planning — PlanCoverageResponseBudget (issue #41).
//
// plan_coverage was the only agent-facing tool whose response had no size budget, and its output is
// the one that scales with REPOSITORY size rather than with the analysed run: an ordinary 50-suite x
// 10-step repo with no run history produces one `suite-never-run` per suite plus one
// `step-never-exercised` per step — ~550 findings, ~200 KB — before the wire envelope doubles it.
// This type is that budget, built to the same discipline as Diagnosis/ExplainRunOrchestrator's:
// fixed deterministic tiers, MEASURED by actually serialising a candidate, every bound it applies
// visible on the wire as a counter.

using System.Text.Json;
using Vouchfx.Mcp.Tools;

namespace Vouchfx.Mcp.Planning;

/// <summary>
/// Fits a <see cref="PlanCoverageResult"/> into <c>plan_coverage</c>'s response budget by a fixed,
/// deterministic ladder of increasingly aggressive list caps, reporting every item it drops as a
/// visible counter on the result it returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>What scales, and therefore what is bounded.</b> The findings array is the dominant term (one
/// entry per unexercised step, so it scales with suites x steps) and the six inventory arrays are the
/// secondary one (each scales with the repository — see <see cref="PlanCoverageInventory"/>'s own
/// remarks for the per-array reasoning, including why <c>stepTypes</c> is bounded despite looking
/// catalogue-capped). Everything else on the report is a fixed-shape scalar: a schema version, four
/// thresholds, five counts, two timestamps, and <see cref="PlanCoverageResult.EngineVersion"/> — the
/// one variable-length scalar, and the one thing <see cref="BuildMinimal"/> exists to drop.
/// </para>
/// <para>
/// <b>Selection is prioritised; EMISSION ORDER IS NOT CHANGED.</b> When findings must be dropped,
/// which ones survive is decided by <see cref="FindingPriority"/>'s fixed kind→band table (ties
/// broken by the engine's own index, so the choice is total and deterministic) — but the survivors
/// are then re-emitted in the engine's original order. That split is deliberate: EDGE-005's "the
/// engine's own deterministic order" is a documented property of the relayed array and re-sorting it
/// would silently break it for every caller, including those whose response was never truncated at
/// all. Prioritising the SELECTION costs nothing and keeps the relay contract intact.
/// </para>
/// <para>
/// <b>The band order, root-cause-first.</b> Band 0 is <c>suite-identity-ambiguous</c>: it says the
/// analysis could not attribute history to a suite unambiguously, which makes every OTHER finding's
/// suite attribution suspect — a host that loses it acts on data it has no reason to trust. Band 1 is
/// observed instability (<c>step-fragile</c>, <c>step-flaky</c>, <c>step-inconclusive-prone</c>):
/// evidence-backed facts about steps that DO run, and the scarcest findings in any real report. Band
/// 2 is <c>step-stale</c>, the same class with weaker evidence. Band 3 is the declared-but-unverified
/// seams (<c>dependency-missing-step-type</c>, <c>dependency-not-asserted</c>,
/// <c>service-missing-http-step</c>), each carrying a <c>scaffold_suite</c> hand-off hint a host can
/// act on directly. Band 5 is <c>suite-never-run</c> and band 6 is <c>step-never-exercised</c>, in
/// that order because the first is the ROOT CAUSE of every one of the second it contains: in a repo
/// with no history at all those two kinds ARE the flood, and one suite-level finding tells a host
/// more than ten of its own step-level children.
/// </para>
/// <para>
/// <b>Why a ladder rather than a cursor.</b> <c>Run/OpaqueCursor</c> is this server's paging
/// mechanism and <c>get_run_events</c>/<c>list_runs</c> both use it, so paging the findings was the
/// obvious alternative and was rejected: those two tools page over a PERSISTED artefact (an events
/// file on disk, the run registry), so page two re-reads what page one read. A plan report is not
/// persisted anywhere — it exists only as the stdout of one <c>vouchfx plan</c> invocation — so every
/// page would have to re-spawn the engine and re-analyse the whole repository, and two pages could
/// legitimately disagree because the suites changed between them. The ladder answers in ONE call with
/// ONE analysis, and the counters say what was left out.
/// </para>
/// <para>
/// <b>An unrecognised kind sorts at band 4 — ahead of the two flood kinds, not behind them.</b> The
/// kind vocabulary belongs to the engine, and a future engine may add one this build has never seen.
/// Sorting an unknown kind last would make exactly the new, rare, unexamined finding the first thing
/// dropped; sorting it ahead of the two kinds MEASURED to dominate real reports (see this file's
/// header) keeps it visible without claiming to know what it means. The same fallback covers a
/// spelling this build has wrong: the ranking degrades, the response does not.
/// </para>
/// </remarks>
internal static class PlanCoverageResponseBudget
{
    /// <summary>
    /// The intended cap on the <c>plan_coverage</c> response's serialised wire size (UTF-8 JSON
    /// bytes of the full <c>CallToolResult</c> envelope) — the same 64&#160;KB
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c> names, chosen for the same reason: a
    /// response an agent can read in one context window regardless of how large the analysed
    /// repository was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This constant bounds the ENVELOPE, and that claim is modelled rather than asserted
    /// directly</b> — see <see cref="Fits"/>, which measures both wire copies exactly rather than
    /// modelling either, and <see cref="EnvelopeOverheadAllowance"/> for the one term that stays
    /// modelled. The distinction matters because the sibling
    /// budget's equivalent constant does NOT bound its envelope and says so:
    /// <c>ExplainRunOrchestrator</c> halves its cap on a doubling model that measurement showed to be
    /// 2.213, so a diagnosis that exactly fills its halved budget produces a 71,335&#160;B envelope
    /// against a 65,536&#160;B cap. This type does not inherit that error.
    /// </para>
    /// </remarks>
    public const int MaxPlanCoverageResponseBytes = 64 * 1024;

    /// <summary>
    /// Bytes reserved out of <see cref="MaxPlanCoverageResponseBytes"/> for the parts of the envelope
    /// <see cref="Fits"/> does NOT measure: the <c>meta</c> stamp — which is merged into the payload
    /// object before either copy is written, and so is carried once raw and once escaped, exactly like
    /// the payload — and the <c>CallToolResult</c> wrapper's own fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The arithmetic this number has to satisfy</b> is
    /// <c>meta + escaped(meta) + wrapper ≤ </c> this value. Both <c>meta</c> terms, because the fit
    /// below measures the payload's two copies and <c>meta</c> rides in both of them; escaping is a
    /// per-character transformation, so <c>escaped(payload + meta)</c> is exactly
    /// <c>escaped(payload) + escaped(meta)</c> and the two halves can be budgeted separately.
    /// </para>
    /// <para>
    /// <b>MEASURED at ModelContextProtocol 2.2.0</b>, by
    /// <c>PlanCoverageResponseBudgetTests.EnvelopeOverheadAllowance_CoversBothMetaCopiesPlusTheWrapper</c>,
    /// against a 95-character <c>workspaceRoot</c> representing a real deployment path:
    /// <c>meta 177&#160;B + escaped meta 267&#160;B + wrapper 106&#160;B = <b>550&#160;B</b></c>,
    /// inside this 1024 with <b>474&#160;B spare</b>. (The escaped copy costs more than the raw one
    /// because <c>meta</c> is itself quote-dense — a small object of short string fields — which is
    /// the same effect that broke the ×3 payload model; see <see cref="Fits"/>.)
    /// </para>
    /// <para>
    /// <b>What that test does and does NOT guard.</b> It re-measures the inequality on every run, so a
    /// new <c>meta</c> FIELD fails there rather than on a host's wire. It does NOT read the host's own
    /// <c>workspaceRoot</c> — it pins a fixed 95-character fixture root — so a long install path is
    /// covered by ARITHMETIC rather than by the gate. Slope against the 474&#160;B spare: a plain
    /// character costs <b>2&#160;B</b> (MEASURED: one in the raw <c>meta</c> chunk, one in the escaped
    /// copy) and a path separator costs <b>6&#160;B</b> (derived: escaped to two bytes in the chunk,
    /// each doubled again in the escaped copy). So the spare absorbs at least another <b>79</b>
    /// characters even if every one were a separator — a root of ~174 characters — and around 290 for
    /// a realistic Windows path at roughly one separator per ten. A deployment root beyond that needs
    /// the allowance raised, and nothing in the suite would say so, which is why the arithmetic is
    /// recorded here rather than left implicit.
    /// </para>
    /// <para>
    /// <b>Rounded to a power of two rather than to the measurement</b>, deliberately: this term exists
    /// to be comfortably over-provisioned, it is paid once per response regardless of size, and it is
    /// cheap — a few hundred bytes of headroom against a 64&#160;KB cap costs well under one finding.
    /// </para>
    /// </remarks>
    internal const int EnvelopeOverheadAllowance = 1024;

    /// <summary>
    /// Whether <paramref name="candidate"/> fits the response budget: its two wire copies, MEASURED
    /// exactly, plus <see cref="EnvelopeOverheadAllowance"/>, within
    /// <see cref="MaxPlanCoverageResponseBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This measures rather than models, and that is the whole point.</b> Three successive
    /// revisions of this budget tried to MODEL the cost of the escaped second copy as a multiplier on
    /// the first (×2, then ×2.5, then ×3), and each was measured wrong in turn. The last and most
    /// instructive miss: the ×3 ceiling was derived from how the encoder escapes the CONTENT of
    /// strings, and missed the payload's own STRUCTURAL quotes — every property-name and string-value
    /// delimiter is a raw <c>"</c> in the structured copy, and the text copy escapes each one to the
    /// six-byte <c>\uXXXX</c> form: <b>1 byte in, 6 out</b>, a ratio of 7. Those quotes are ~34% of a
    /// payload made of short identifiers (an inventory with all seven arrays populated), which models
    /// to roughly <b>68,150&#160;B</b> against a 65,536&#160;B cap — a real breach that every
    /// character-class model had declared impossible.
    /// </para>
    /// <para>
    /// So there is no escape model here any more. <see cref="EscapedPayloadByteCount"/> performs the
    /// ACTUAL transformation the wire performs — serialising the payload's JSON text as a JSON string
    /// under the same options — and counts the bytes. No character class can be overlooked because
    /// none is enumerated.
    /// </para>
    /// <para>
    /// <b>MEASURED at the two worst-case boundaries</b> (both re-measured on every run, under a
    /// 95-character <c>workspaceRoot</c>): the all-backslash fixture fills the budget at payload
    /// 21,641&#160;B → envelope <b>65,056&#160;B</b>, 480&#160;B inside the cap, ratio 3.006; the
    /// quote-dense all-seven-arrays fixture lands at payload 14,883&#160;B → envelope
    /// <b>45,306&#160;B</b>, ratio <b>3.044</b>. That second ratio is the whole argument in one
    /// number: it EXCEEDS 3, so the previous ×3 model would have accepted a payload whose envelope
    /// breached the cap. An ordinary 550-finding report measures 59,109&#160;B.
    /// </para>
    /// <para>
    /// <b>The residue this does not measure</b> is four terms, all covered by
    /// <see cref="EnvelopeOverheadAllowance"/>: the <c>meta</c> stamp and the <c>CallToolResult</c>
    /// wrapper (both measured — see that constant), the JSON-RPC frame the response is carried in
    /// (<c>jsonrpc</c>/<c>id</c>/<c>result</c>, ~35–45&#160;B, comfortably inside the 474&#160;B
    /// spare), and the MCP SDK's own writer options, which this server neither owns nor configures.
    /// </para>
    /// <para>
    /// <b>That last term is EXACT rather than merely conservative, and it was measured.</b> Grepping
    /// the metadata of all three pinned <c>ModelContextProtocol</c> 2.2.0 assemblies
    /// (<c>ModelContextProtocol</c>, <c>.Core</c>, <c>.AspNetCore</c>) finds <b>zero</b>
    /// <c>set_Encoder</c> memberrefs and <b>zero</b> <c>UnsafeRelaxedJsonEscaping</c> references —
    /// assigning a non-default encoder to <c>JsonSerializerOptions</c> or <c>JsonWriterOptions</c>
    /// requires that setter, including via object-initializer syntax, so its total absence means the
    /// SDK's frame writer uses the same <c>JavaScriptEncoder.Default</c> this measures with. (A single
    /// <c>JavaScriptEncoder</c> TYPEREF does appear in <c>.Core</c>; without the setter it cannot
    /// change what the writer emits.) The tripwire is <c>set_Encoder</c> appearing in a future SDK
    /// release.
    /// </para>
    /// <para>
    /// Even if it did, the direction is safe: re-escaping the same payload under
    /// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> produces a STRICTLY SMALLER text copy (a
    /// quote costs 2 bytes rather than 6), so a looser SDK encoder only shrinks the real envelope.
    /// That direction is asserted by
    /// <c>PlanCoverageResponseBudgetTests.ARelaxedEncoderOnlyShrinksTheTextCopy</c>, not assumed.
    /// </para>
    /// </remarks>
    private static bool Fits(PlanCoverageResult candidate) =>
        MeasuredResponseBytes(candidate) <= MaxPlanCoverageResponseBytes;

    /// <summary>
    /// The measured wire cost of <paramref name="candidate"/>: structured copy + escaped text copy +
    /// <see cref="EnvelopeOverheadAllowance"/>. <see langword="internal"/> so tests can assert the
    /// number this type actually enforces rather than re-deriving it.
    /// </summary>
    internal static int MeasuredResponseBytes(PlanCoverageResult candidate) =>
        PayloadByteCount(candidate) + EscapedPayloadByteCount(candidate) + EnvelopeOverheadAllowance;

    /// <summary>
    /// The largest value a caller may pass for <c>maxFindings</c>, and — because it is the richest
    /// tier's own cap — the absolute ceiling on how many findings ANY <c>plan_coverage</c> response
    /// can carry. A request above it is REFUSED rather than clamped (the house rule
    /// <c>get_run_artifacts</c>' <c>tailLines</c> states).
    /// </summary>
    /// <remarks>
    /// <b>150 is an INTENTIONAL hard cap, not merely the largest rung that happens to exist.</b> The
    /// two roles this number plays are deliberately the same number: no caller may ask for more than
    /// 150, and no report — however small its findings, however generous the byte budget — is ever
    /// served more than 150. That is what makes "<c>maxFindings</c> can never RAISE the budget"
    /// structural rather than a coincidence of the current tier values, and it is a stated part of the
    /// tool's contract (see the tool description and <c>docs/tools-and-resources.md</c>): a host can
    /// rely on 150 as the ceiling when sizing its own handling, and a report with more findings than
    /// that always says so through <see cref="PlanCoverageResult.OmittedFindingCount"/>.
    /// <para>
    /// <b>The two roles are ONE number by construction:</b> <see cref="Tiers"/>[0] is written as
    /// <c>(MaxRequestedFindings, MaxRequestedFindings)</c> rather than as a repeated literal, so the
    /// richest rung and the declared ceiling cannot drift apart. That coupling is source-level and
    /// therefore needs no test — editing this constant moves both, and editing the rung is not
    /// possible without editing this constant. Changing it is a CONTRACT change (four published
    /// statements quote it), not a tuning change.
    /// </para>
    /// <para>
    /// <b>True but LOOSE, measured.</b> 150 is an upper bound nothing can exceed; it is not an
    /// attainable count. A <see cref="PlanCoverageFinding"/> serialises all twelve properties even when
    /// null, so its floor is ~200&#160;B and 150 of them cannot fit the
    /// measured budget — feeding 300 minimal findings returns <b>75</b>, the
    /// ladder's second rung, because the first fails its byte measurement
    /// (<c>PlanCoverageResponseBudgetTests.TheByteBudget_BindsBeforeTheFindingCeiling_OnMinimalFindings</c>).
    /// The byte budget is therefore always the operative limit today, and this rung only becomes
    /// reachable if the finding record shrinks.
    /// </para>
    /// </remarks>
    internal const int MaxRequestedFindings = 150;

    /// <summary>
    /// The fixed detail tiers <see cref="Apply"/> tries in order, each strictly smaller than the
    /// last: (max findings carried, max items carried in EACH of the six inventory arrays). The final
    /// tier carries no list items at all — see <see cref="Apply"/> for why its size is still measured
    /// rather than assumed.
    /// </summary>
    /// <remarks>
    /// Six tiers, so six serialisations in the worst case — a bounded, fixed number of attempts,
    /// never a trim-and-recheck loop. The first rung's findings cap is
    /// <see cref="MaxRequestedFindings"/> and is the tool's DECLARED ceiling — see that constant's
    /// remarks. It is deliberately larger than the number the measured budget
    /// admits at the ~378&#160;B/finding measured for a <c>step-never-exercised</c> entry at the
    /// current pin, because a report whose findings are SHORTER than that (null
    /// <c>suite</c>/<c>stepId</c>, no suggestions) genuinely fits 150 of them, and the ladder must not
    /// refuse to try a rung just because one finding shape cannot reach it. The intermediate rungs
    /// (75/50/30/10) exist because the drop from 75 to 10 in one step would shed far more evidence
    /// than the budget requires.
    /// </remarks>
    private static readonly (int MaxFindings, int MaxInventoryItems)[] Tiers =
    [
        // The literal 150 is written ONCE, in MaxRequestedFindings — the constant the tool
        // description, the maxFindings parameter description, docs/tools-and-resources.md and the
        // README all quote. Referencing it here rather than repeating it makes "the declared ceiling
        // and the richest rung are the same number" true BY CONSTRUCTION: the coupling is
        // source-level, so it cannot drift and needs no test to police it. (An earlier revision wrote
        // the literal twice and added a behavioural test to tie them together; that test could never
        // fail — the byte budget caps any input well below 150 — so it was documentation wearing a
        // test's clothes. Structure replaced it.)
        //
        // BOTH slots derive from it, also deliberately: the richest rung has no reason to treat the
        // inventory arrays more or less generously than the findings array, and one symbol here means
        // a future change to the ceiling cannot accidentally move only half of this rung.
        (MaxRequestedFindings, MaxRequestedFindings),
        (75, 75),
        (50, 50),
        (30, 30),
        (10, 10),
        (0, 0),
    ];

    /// <summary>
    /// The fixed kind→band table the SELECTION order is derived from — lower band survives longer.
    /// See this type's remarks for why each kind sits where it does; the bands are named there rather
    /// than duplicated here so there is one place to keep truthful.
    /// </summary>
    private static readonly Dictionary<string, int> FindingPriorityByKind = new(StringComparer.Ordinal)
    {
        ["suite-identity-ambiguous"] = 0,
        ["step-fragile"] = 1,
        ["step-flaky"] = 1,
        ["step-inconclusive-prone"] = 1,
        ["step-stale"] = 2,
        ["dependency-missing-step-type"] = 3,
        ["dependency-not-asserted"] = 3,
        ["service-missing-http-step"] = 3,
        ["suite-never-run"] = 5,
        ["step-never-exercised"] = 6,
    };

    /// <summary>The band a kind this build does not recognise sorts into — see this type's remarks.</summary>
    private const int UnrecognisedKindPriority = 4;

    /// <summary>
    /// Every finding kind <see cref="FindingPriorityByKind"/> ranks explicitly. <see langword="internal"/>
    /// purely so <c>PlanCoverageFindingKindParityTests</c> can check this set against the kind list
    /// published in <c>docs/tools-and-resources.md</c> in BOTH directions.
    /// </summary>
    /// <remarks>
    /// <b>Why a parity gate rather than trust.</b> The vocabulary belongs to the engine, and an
    /// upstream RENAME degrades silently here: the old spelling simply stops matching and the kind
    /// falls to <see cref="UnrecognisedKindPriority"/>. That is a safe failure — nothing crashes, and
    /// band 4 still keeps it ahead of the floods — but a <c>step-flaky</c> quietly demoted out of band
    /// 1 is exactly the kind of drift no test would otherwise notice. Tying this table to the
    /// documented list makes the rename fail loudly at the same moment someone updates the docs,
    /// mirroring <c>ErrorCatalogueFilesystemParityTests</c>' bidirectional code/page gate.
    /// </remarks>
    internal static IReadOnlyCollection<string> RankedFindingKinds => FindingPriorityByKind.Keys;

    /// <summary>
    /// The cap <see cref="BuildMinimal"/> is reached at, in characters, for the one variable-length
    /// scalar the floor tier would otherwise still carry.
    /// </summary>
    private const int MaxMinimalEngineVersionChars = 64;

    /// <summary>
    /// Returns <paramref name="report"/> bounded to <see cref="MaxPlanCoverageResponseBytes"/>, with every
    /// dropped item reported by a counter on the returned result.
    /// </summary>
    /// <param name="report">The report as the engine produced it, deserialised and otherwise untouched.</param>
    /// <param name="maxFindings">
    /// The caller's own findings request, already validated to lie in
    /// <c>1..</c><see cref="MaxRequestedFindings"/>, or <see langword="null"/> for "as many as the
    /// ladder allows". It is applied as a MINIMUM against the chosen tier's own cap, so it can only
    /// ever lower the count, never raise it.
    /// </param>
    /// <remarks>
    /// Tries <see cref="Tiers"/> in order and returns the first candidate whose MEASURED serialised
    /// size fits. The floor tier's size is measured too rather than assumed to fit "by construction":
    /// with every list emptied the only variable-length field left is
    /// <see cref="PlanCoverageResult.EngineVersion"/>, which this server does not author, so an engine
    /// that stamped something enormous there would otherwise silently breach the budget. If even the
    /// floor does not fit, <see cref="BuildMinimal"/> drops that field too, leaving a shape whose
    /// worst case is a few hundred bytes of fixed-width scalars — verifiable by arithmetic rather
    /// than by yet another measure-and-fall-back layer.
    /// </remarks>
    public static PlanCoverageResult Apply(PlanCoverageResult report, int? maxFindings)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Computed ONCE, outside the tier loop: the priority order over the engine's findings does
        // not change per tier, only how much of it is taken. Sorting per tier would be six sorts of
        // the same list for the same answer.
        var selectionOrder = BuildSelectionOrder(report.Findings);

        for (var tierIndex = 0; tierIndex < Tiers.Length; tierIndex++)
        {
            var candidate = BuildAtTier(report, selectionOrder, Tiers[tierIndex], maxFindings);

            if (Fits(candidate))
            {
                return candidate;
            }
        }

        return BuildMinimal(report);
    }

    /// <summary>
    /// The engine's finding indices, ordered by (band, engine index) — the total, deterministic
    /// SELECTION order. <see cref="BuildAtTier"/> takes a prefix of this and then restores the
    /// engine's own order for emission.
    /// </summary>
    /// <remarks>
    /// <c>OrderBy</c> is a STABLE sort in LINQ-to-objects, so the explicit <c>ThenBy</c> on the index
    /// is belt-and-braces rather than load-bearing — kept because "ties broken by the engine's own
    /// index" is a contract this type states publicly, and a contract that relies on an unstated
    /// property of the sort implementation is one edit away from being false.
    /// </remarks>
    private static int[] BuildSelectionOrder(IReadOnlyList<PlanCoverageFinding> findings) =>
        Enumerable.Range(0, findings.Count)
            .OrderBy(index => FindingPriority(findings[index].Kind))
            .ThenBy(index => index)
            .ToArray();

    private static int FindingPriority(string? kind) =>
        kind is not null && FindingPriorityByKind.TryGetValue(kind, out var priority)
            ? priority
            : UnrecognisedKindPriority;

    private static PlanCoverageResult BuildAtTier(
        PlanCoverageResult report,
        int[] selectionOrder,
        (int MaxFindings, int MaxInventoryItems) tier,
        int? maxFindings)
    {
        // The caller's request and the tier's cap meet HERE, as a minimum, which is what makes
        // "maxFindings can never raise the budget" structural rather than a rule to remember.
        var keptFindingCount = Math.Min(tier.MaxFindings, maxFindings ?? int.MaxValue);
        keptFindingCount = Math.Min(keptFindingCount, report.Findings.Count);

        // Selection by priority, EMISSION in the engine's own order (EDGE-005) — see this type's
        // remarks for why those are deliberately two different orders.
        var keptIndices = selectionOrder.Take(keptFindingCount).ToArray();
        Array.Sort(keptIndices);

        var findings = new List<PlanCoverageFinding>(keptIndices.Length);
        foreach (var index in keptIndices)
        {
            findings.Add(report.Findings[index]);
        }

        var inventory = BuildInventoryAtTier(report.Inventory, tier.MaxInventoryItems);
        var omittedFindingCount = report.Findings.Count - findings.Count;

        return report with
        {
            Findings = findings,
            Inventory = inventory,
            OmittedFindingCount = omittedFindingCount,
            // COMPUTED from the counters rather than hardcoded per tier — the same discipline
            // get_run_artifacts' `partial` follows. A tier that happened to drop nothing (a small
            // report at tier 0) must not claim truncation, and the floor tier must not have to
            // remember to claim it.
            ResponseTruncated = omittedFindingCount > 0 || HasOmissions(inventory),
        };
    }

    private static PlanCoverageInventory BuildInventoryAtTier(PlanCoverageInventory inventory, int maxItems) =>
        inventory with
        {
            Suites = Cap(inventory.Suites, maxItems),
            Services = Cap(inventory.Services, maxItems),
            Dependencies = Cap(inventory.Dependencies, maxItems),
            StepTypes = Cap(inventory.StepTypes, maxItems),
            UnanalysableSuites = Cap(inventory.UnanalysableSuites, maxItems),
            UnmappableDependencies = Cap(inventory.UnmappableDependencies, maxItems),
            OmittedSuiteCount = Omitted(inventory.Suites, maxItems),
            OmittedServiceCount = Omitted(inventory.Services, maxItems),
            OmittedDependencyCount = Omitted(inventory.Dependencies, maxItems),
            OmittedStepTypeCount = Omitted(inventory.StepTypes, maxItems),
            OmittedUnanalysableSuiteCount = Omitted(inventory.UnanalysableSuites, maxItems),
            OmittedUnmappableDependencyCount = Omitted(inventory.UnmappableDependencies, maxItems),
        };

    private static bool HasOmissions(PlanCoverageInventory inventory) =>
        inventory.OmittedSuiteCount > 0
        || inventory.OmittedServiceCount > 0
        || inventory.OmittedDependencyCount > 0
        || inventory.OmittedStepTypeCount > 0
        || inventory.OmittedUnanalysableSuiteCount > 0
        || inventory.OmittedUnmappableDependencyCount > 0;

    private static IReadOnlyList<T> Cap<T>(IReadOnlyList<T> items, int maxItems) =>
        items.Count <= maxItems ? items : items.Take(maxItems).ToList();

    private static int Omitted<T>(IReadOnlyList<T> items, int maxItems) => Math.Max(0, items.Count - maxItems);

    /// <summary>
    /// The genuine last resort, reached only if the floor tier's MEASURED size still exceeded
    /// the measured budget — every list emptied AND
    /// <see cref="PlanCoverageResult.EngineVersion"/> capped, leaving a shape built entirely from
    /// fixed-width scalars whose worst case is a few hundred bytes.
    /// </summary>
    /// <remarks>
    /// Capped rather than nulled: a truncated version string still tells a host which engine produced
    /// the report, and <see cref="PlanCoverageResult.ResponseTruncated"/> — unconditionally
    /// <see langword="true"/> here — is what says the response was cut. The counters report the FULL
    /// source counts, so "there were 550 findings and none of them are here" stays readable.
    /// <para>
    /// The cut is by UTF-16 CODE UNIT, so one landing mid-surrogate yields a U+FFFD replacement
    /// character rather than a fault — the same measured behaviour
    /// <c>WorkspaceSpecIndexModels</c> records for its own display cap, and acceptable for the same
    /// reason with more margin here: this path is reached only when an engine has stamped something
    /// pathological into a version field that every real engine fills with ASCII semver.
    /// </para>
    /// </remarks>
    private static PlanCoverageResult BuildMinimal(PlanCoverageResult report)
    {
        var engineVersion = report.EngineVersion is { Length: > MaxMinimalEngineVersionChars } version
            ? version[..MaxMinimalEngineVersionChars]
            : report.EngineVersion;

        return report with
        {
            EngineVersion = engineVersion,
            Findings = [],
            OmittedFindingCount = report.Findings.Count,
            ResponseTruncated = true,
            Inventory = report.Inventory with
            {
                Suites = [],
                Services = [],
                Dependencies = [],
                StepTypes = [],
                UnanalysableSuites = [],
                UnmappableDependencies = [],
                OmittedSuiteCount = report.Inventory.Suites.Count,
                OmittedServiceCount = report.Inventory.Services.Count,
                OmittedDependencyCount = report.Inventory.Dependencies.Count,
                OmittedStepTypeCount = report.Inventory.StepTypes.Count,
                OmittedUnanalysableSuiteCount = report.Inventory.UnanalysableSuites.Count,
                OmittedUnmappableDependencyCount = report.Inventory.UnmappableDependencies.Count,
            },
        };
    }

    /// <summary>
    /// Measures a candidate through <c>StructuredToolResult.Options</c> — the EXACT options the
    /// payload reaches the wire under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling budget measures through a separate <c>new(JsonSerializerDefaults.Web)</c> instance,
    /// which agrees byte-for-byte for these types (they carry explicit <c>[JsonPropertyName]</c>s and
    /// resolve through the default reflective resolver either way) but agrees by coincidence rather
    /// than by construction. Using the real options means a future change to the resolver chain or the
    /// encoder moves the measurement with the wire instead of leaving it behind.
    /// </para>
    /// <para>
    /// <b>It is still a close MODEL of the wire rather than the wire itself.</b> The payload is
    /// measured under <c>StructuredToolResult.Options</c> — the options this server serialises the
    /// payload with — but the final JSON-RPC frame is written by the MCP SDK under ITS own writer
    /// options, which this server neither owns nor can observe from here. The same distinction holds
    /// for <c>ExplainRunOrchestratorTests</c>' envelope probe, and it is why
    /// <see cref="EnvelopeOverheadAllowance"/> reserves headroom for the wrapper instead of pretending
    /// to have counted it exactly.
    /// </para>
    /// </remarks>
    internal static int PayloadByteCount(PlanCoverageResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, typeof(PlanCoverageResult), StructuredToolResult.Options).Length;

    /// <summary>
    /// The bytes the TEXT copy costs: <paramref name="result"/>'s JSON serialised, then that JSON text
    /// serialised AGAIN as a JSON string — which is precisely the transformation
    /// <c>StructuredToolResult.Success</c>'s <c>TextContentBlock.Text</c> undergoes on the wire,
    /// surrounding quotes included.
    /// </summary>
    /// <remarks>
    /// <b>Performed, not modelled.</b> Every earlier revision of this budget tried to predict this
    /// number from a per-character ratio and was measured wrong; running the actual transformation
    /// cannot miss a character class because it enumerates none. The cost is one extra serialisation
    /// per rung — at most six per call, each over an already-bounded candidate.
    /// </remarks>
    internal static int EscapedPayloadByteCount(PlanCoverageResult result)
    {
        var payloadJson = JsonSerializer.Serialize(result, typeof(PlanCoverageResult), StructuredToolResult.Options);
        return JsonSerializer.SerializeToUtf8Bytes(payloadJson, typeof(string), StructuredToolResult.Options).Length;
    }
}
