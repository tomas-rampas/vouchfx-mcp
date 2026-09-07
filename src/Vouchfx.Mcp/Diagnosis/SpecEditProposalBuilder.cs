using System.Globalization;

namespace Vouchfx.Mcp.Diagnosis;

/// <summary>
/// US-S4-03's spec-edit proposal builder: plan D2's Healer SUPERSET. Deterministic, template-based,
/// and scoped strictly to the four things a suite edit may legitimately fix for an
/// <c>EnvironmentError</c>/<c>Inconclusive</c> outcome — environment declarations, timeouts, match
/// keys, and capture paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>A superset, never a replacement.</b> <see cref="FailProposalBuilder.BuildProposals"/> keeps
/// producing exactly what it always produced for <c>Fail</c> steps; this type adds a SECOND,
/// separately-named list for the two outcome categories where editing the suite is actually the
/// right answer. Neither builder can produce the other's output.
/// </para>
/// <para>
/// <b>The Fail partition is STRUCTURAL, not conventional.</b> This builder only ever looks at
/// material whose <see cref="StepDiagnosis.Verdict"/> is <c>EnvironmentError</c>/<c>Inconclusive</c>
/// (plus environment-error RECORDS, which are not steps at all), and it keys every decision off
/// <see cref="VerdictReason.Kind"/>. Since US-S4-01's rule table assigns <c>assertion</c> ONLY to a
/// <c>Fail</c> step and never assigns any other kind to one, "a Fail step never yields a spec-edit
/// proposal" holds by construction here rather than by a rule someone must remember: there is no
/// input shape that reaches a proposal branch from a Fail step.
/// </para>
/// <para>
/// <b>No free-form string building — a small closed set of FRAGMENT TEMPLATES.</b> Every
/// <see cref="SpecEditProposal.SuggestedEdit"/> this type can emit is one of the constants below,
/// with engine-derived identifiers substituted. US-S4-05's regression guard has to enumerate what
/// this builder can construct (the derive-from-source pattern <c>SecretHygieneSourceGuardTests</c>
/// uses); that is tractable against six named constants and would not be against interpolation
/// scattered through the code. It also makes the "never an assertion-shaped key" property readable
/// straight off the templates: none of them carries <c>expect</c>, <c>assert</c>, or
/// <c>match.value</c>.
/// </para>
/// <para>
/// <b>YAML vocabulary comes from the vendored schema, not from invention.</b>
/// <c>environment.services.&lt;name&gt;.image</c>, <c>environment.dependencies.&lt;name&gt;.version</c>,
/// <c>environment.seed.&lt;name&gt;.sql</c>, a step's <c>timeout</c>/<c>verifyMode</c>, its
/// <c>match.key</c>/<c>match.headers</c>, and its <c>capture</c> map are all real fields of
/// <c>vendored/composed-schema.v1.json</c>. A fragment naming a key the schema does not have would
/// be advice that cannot be applied.
/// </para>
/// <para>
/// <b>Every decision reads <see cref="VerdictReason.Evidence"/>, never the hint SENTENCE.</b> The
/// timeout variant, the image reference, and the health window are all published as structured
/// facts by the classifier, from untrimmed data. An earlier version recovered them by splitting the
/// hint text apart and by inspecting the tier-trimmed attempt list; both were review-rejected, and
/// for the same reason: one made advisory prose load-bearing, the other let RESPONSE SIZE change
/// what a run was advised to do.
/// </para>
/// <para>
/// <b>Secret hygiene and bounds.</b> Fragments and rationales carry ONLY identifiers the engine
/// itself already emitted — an image reference, a resource name, a step id, a timeout figure. Every
/// one that lands INSIDE A FRAGMENT is run through <see cref="TextSanitiser.SanitiseForDisplay"/>
/// and capped to <see cref="VerdictReasonClassifier.MaxValueChars"/> at this boundary (see
/// <see cref="Identifier"/>); an earlier version of this comment claimed that capping had already
/// happened upstream, and it had not.
/// </para>
/// <para>
/// <b>The residual, stated: <see cref="SpecEditProposal.StepId"/> is deliberately RAW.</b> It is a
/// CORRELATION key — a host matches it against <c>notableSteps[].stepId</c> and
/// <see cref="FailProposal.StepId"/>, both of which carry the parser's uncapped value — so capping
/// it here would silently break the join that makes a proposal locatable. Ten proposals can
/// therefore still carry roughly 20&#160;KB of step ids. What the fragment-side capping bought is a
/// narrowing, not a fix: the worst case fell from about 42&#160;KB (each id spliced twice per
/// fragment, plus the field) to about 30&#160;KB. Closing it needs the shrink ladder to shed these
/// proposals like it sheds the others, which is US-S4-04's remit, not a second cap here.
/// </para>
/// <para>
/// Nothing here reads the process environment, resolves a <c>${secret:…}</c> reference, or
/// re-redacts engine text.
/// </para>
/// </remarks>
internal static class SpecEditProposalBuilder
{
    /// <summary>Maximum proposals returned — the same bound <see cref="FailProposalBuilder.MaxProposals"/> uses, for the same response-budget reason.</summary>
    public const int MaxProposals = 10;

    /// <summary>
    /// The ceiling a rationale is PROVEN to respect — not a cap this type applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here re-caps, and that is the point (a peer-review finding).</b> Every rationale is
    /// one bounded input — a <see cref="VerdictReason.Hint"/>, which its own type guarantees is at
    /// most <see cref="VerdictReasonClassifier.MaxHintChars"/> (300) characters — plus one fixed
    /// literal suffix from this file. Capping that composition AGAIN would risk STACKING truncation
    /// markers ("……") when the hint had already been truncated to its own bound, and would add a
    /// second, weaker bound over text that is already bounded by construction.
    /// </para>
    /// <para>
    /// <b>MEASURED worst case: 396 characters</b> — a maximal 300-character hint plus the longest
    /// suffix this file contains (the match rationale's "Values WERE observed…" clause), leaving 104
    /// characters of headroom under this ceiling. The figure is PINNED EXACTLY by
    /// <c>SpecEditProposalBuilderTests.EveryRationale_StaysWithinItsProvenCeilingWithoutBeingRecapped</c>,
    /// not computed here: two earlier versions of this remark stated hand-derived sums (491, then
    /// 486) that were both wrong, which is precisely why the number now lives in an assertion instead
    /// of in prose. A sibling test pins that a rationale built from an already-truncated hint carries
    /// exactly one marker.
    /// </para>
    /// </remarks>
    public const int MaxRationaleChars = 500;

    // ── The closed set of fragment templates (see this type's remarks) ──────────────────────────

    /// <summary>Header every fragment opens with — the review-only framing, restated per fragment because a fragment may be read alone.</summary>
    private const string FragmentHeader = "# Review-only suggestion — not applied, and not a diff against your file.";

    /// <summary>A service image that could not be pulled. <c>{0}</c>: the image reference, YAML-quoted.</summary>
    private const string EnvironmentServiceImageFragment = FragmentHeader + """

        # Check the tag exists in the registry and that credentials are configured for it.
        environment:
          services:
            <your-service-name>:
              image: {0}
        """;

    /// <summary>
    /// A resource that never became healthy. <c>{0}</c>: resource name for PROSE. <c>{1}</c>: the
    /// same name as a YAML-quoted key. <c>{2}</c>: the observed window, or a neutral phrase.
    /// <c>{3}</c>: the unnamed-resource note, or nothing.
    /// </summary>
    /// <remarks>
    /// <b>This fragment emits a <c>dependencies</c> target, but a health gate can fail on a SERVICE
    /// — so the prose now says BOTH, rather than pointing silently at one block.</b> An
    /// <c>environment-error</c> record carries only a resource NAME; nothing in the event stream says
    /// which block declared it, and the composed schema's only <c>healthCheck</c> key lives under
    /// <c>$defs/service</c> — so for a service-shaped failure the emitted YAML names the wrong
    /// section. Choosing correctly needs data this server is not given, and the real resolution is
    /// upstream ask <b>U1</b> (a topology relay that would say which block a resource came from).
    /// Until then this is the honest half of the fix: the fragment keeps the commoner target and the
    /// comment names the other one explicitly, so a reader can tell which applies. Reading the
    /// suite itself is NOT the alternative — <c>get_step_timeline</c> refuses to read a suite for
    /// <c>verifyMode</c> for the same reason.
    /// </remarks>
    private const string EnvironmentHealthFragment = FragmentHeader + """

        # '{0}' did not pass its health gate within {2}. Raise the dependency's own
        # startup allowance, or fix what keeps it unhealthy — check its container logs first.
        # If '{0}' is a SERVICE rather than a managed dependency, the knob is its own
        # healthCheck under environment.services instead — this run's events do not say which.
        environment:
          dependencies:
            {1}:
              version: "<a version known to start cleanly in this environment>"{3}
        """;

    /// <summary>
    /// Seeding failed against a dependency. <c>{0}</c>: seed target for PROSE. <c>{1}</c>: the same
    /// name as a YAML-quoted key. <c>{2}</c>: the unnamed-resource note, or nothing.
    /// </summary>
    private const string EnvironmentSeedFragment = FragmentHeader + """

        # Seeding '{0}' failed before any step ran. Check the SQL files apply
        # against a clean database, in this order.
        environment:
          seed:
            {1}:
              sql:
                - <path/to/schema.sql>
                - <path/to/data.sql>{2}
        """;

    /// <summary>A step whose wait expired. <c>{0}</c>: step id for PROSE. <c>{1}</c>: the same id, YAML-quoted.</summary>
    private const string TimeoutsFragment = FragmentHeader + """

        # Step '{0}' ran out of time. Either the wait is too short for this
        # environment, or the step should poll rather than check once.
        steps:
          - id: {1}
            verifyMode: RETRY
            timeout: <a duration longer than the current one, e.g. 60s>
        """;

    /// <summary>A step that observed values but matched none. <c>{0}</c>: step id for PROSE. <c>{1}</c>: the same id, YAML-quoted.</summary>
    private const string MatchFragment = FragmentHeader + """

        # Step '{0}' saw values and matched none, so the criteria are the likely
        # cause rather than the wait. Check the key (and any headers) name what the producer
        # actually emits — spelling and case are exact.
        steps:
          - id: {1}
            match:
              key: <the field name the producer really writes>
              headers:
                <header-name>: <expected header value>
        """;

    /// <summary>A step whose capture produced nothing. <c>{0}</c>: step id for PROSE. <c>{1}</c>: the same id, YAML-quoted.</summary>
    private const string CaptureFragment = FragmentHeader + """

        # Step '{0}' captured nothing. Check the extractor path against the
        # response body the step actually receives, and that the step producing it runs first.
        steps:
          - id: {1}
            capture:
              <variable-name>: "$.<path.to.the.value>"
        """;

    /// <summary>
    /// Builds the spec-edit proposals for an already-built, already-classified
    /// <paramref name="diagnosis"/>. Empty when nothing in it is both classified and editable.
    /// </summary>
    /// <remarks>
    /// <b>KEPT although only tests call it, and the distinction from the deleted
    /// <c>AnyAttemptCarriedAnObservation</c> is the reason.</b> That helper was a SECOND
    /// implementation of a fact the shipped path had stopped using — a test against it proved the
    /// helper worked, not the product. This is a one-line delegation TO the shipped path: every
    /// assertion made through it exercises exactly the code <c>diagnose_run</c> runs, and the only
    /// thing it drops is a counter those tests are not about. Inlining it would put
    /// <c>.Proposals</c> on twenty-eight call sites and make the ones that DO care about the counter
    /// harder to spot.
    /// </remarks>
    public static IReadOnlyList<SpecEditProposal> BuildProposals(Diagnosis diagnosis) =>
        BuildProposalsWithOmissions(diagnosis).Proposals;

    /// <summary>
    /// <see cref="BuildProposals"/> plus how many proposals the <see cref="MaxProposals"/> cap
    /// DECLINED — the number <c>diagnose_run</c> surfaces so the bound is visible to a host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ROOT CAUSE FIRST: environment-error records are proposed before steps.</b> The order was
    /// the other way round and a peer review found the consequence: one broken image can cascade
    /// into a dozen timed-out steps, whose two-proposal-per-step fan-out fills the cap and starves
    /// the ONE proposal that names the actual cause. Environment records are the root-cause surface,
    /// so they go first.
    /// </para>
    /// <para>
    /// <b>Stated plainly, because the reverse pressure is real:</b> ten classifying environment-error
    /// records fill the cap and starve ONE HUNDRED PERCENT of the step proposals. That is the
    /// deliberate priority, not a side effect — a run with ten distinct infrastructure failures has a
    /// topology problem, and advice about a step's match key is noise beside it. What makes it
    /// acceptable is that the starvation is VISIBLE:
    /// <see cref="DiagnoseRunResult.OmittedSpecEditProposalCount"/> reports every proposal declined,
    /// so a host is never left thinking the steps had nothing to say.
    /// </para>
    /// <para>
    /// <b>The full set is built and THEN truncated on a STEP BOUNDARY</b>, rather than checking the
    /// cap while building. Both inputs are already tier-capped (at most ten notable steps and ten
    /// environment-error records, so at most thirty proposals), so this is bounded by construction,
    /// and it buys an exact omitted COUNT.
    /// </para>
    /// <para>
    /// <b>A step's proposals are ATOMIC, and that is the whole reason the truncation is not a plain
    /// <c>Take</c>.</b> A first version of this fix moved the pair-splitting rather than removing
    /// it: at odd parity the cap fell between a timeout step's <c>timeouts</c> and <c>match</c>
    /// proposals, and — measured — the half that SURVIVED was the misleading one. The step kept
    /// "raise the timeout, switch to RETRY" while the dropped sibling was the one saying values WERE
    /// observed, so raising the timeout alone is unlikely to help. Half the advice is worse than
    /// none of it. A step whose proposals do not all fit is therefore dropped whole, and both count
    /// as omitted.
    /// </para>
    /// <para>
    /// A later, smaller group may still fit after a larger one was dropped. That is deliberate: the
    /// alternative wastes cap on nothing, and no ordering guarantee is broken by it — what a host is
    /// promised is that any step PRESENT carries all of its proposals, and that
    /// <c>omittedSpecEditProposalCount</c> says how many were left out.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<SpecEditProposal> Proposals, int OmittedCount) BuildProposalsWithOmissions(
        Diagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        var kept = new List<SpecEditProposal>();
        var omitted = 0;

        // Environment-error records first (root cause), and individually atomic — one record yields
        // at most one proposal, so there is no group to keep together.
        foreach (var error in diagnosis.EnvironmentErrors)
        {
            if (BuildEnvironmentProposal(error) is not { } proposal)
            {
                continue;
            }

            if (kept.Count < MaxProposals)
            {
                kept.Add(proposal);
            }
            else
            {
                omitted++;
            }
        }

        // Then steps, each an all-or-nothing GROUP.
        foreach (var step in diagnosis.NotableSteps)
        {
            var group = new List<SpecEditProposal>(capacity: 2);
            AddStepProposals(step, group);

            if (group.Count == 0)
            {
                continue;
            }

            if (kept.Count + group.Count <= MaxProposals)
            {
                kept.AddRange(group);
            }
            else
            {
                omitted += group.Count;
            }
        }

        return (kept, omitted);
    }

    private static void AddStepProposals(StepDiagnosis step, List<SpecEditProposal> proposals)
    {
        // The structural half of "a Fail step never yields a spec-edit proposal": this method never
        // examines a step whose verdict is anything but EnvironmentError/Inconclusive, so no Fail
        // step can reach a branch below even if a future rule started classifying one.
        var isEditableOutcome =
            string.Equals(step.Verdict, nameof(Run.RunVerdict.EnvironmentError), StringComparison.Ordinal) ||
            string.Equals(step.Verdict, nameof(Run.RunVerdict.Inconclusive), StringComparison.Ordinal);

        if (!isEditableOutcome || step.Reason?.Kind is not { } kind)
        {
            return;
        }

        switch (kind)
        {
            case VerdictReasonKinds.Timeout:
                AddTimeoutProposals(step, proposals);
                break;

            case VerdictReasonKinds.CaptureUnmet:
                proposals.Add(new SpecEditProposal(
                    step.StepId,
                    SpecEditScopes.Capture,
                    $"{step.Reason.Hint} The capture's own extractor expression is the thing to check first.",
                    Format(CaptureFragment, Identifier(step.StepId), YamlQuote(Identifier(step.StepId)))));
                break;

            // partition: guidance text only, deliberately. The engine's own partition/grace wording
            // is a statement about the RUN, not a defect in the suite this server may mechanically
            // rewrite — US-S4-03 says so explicitly, and BuildEnvironmentGuidance already covers it.
            //
            // assertion: unreachable from here (Fail-only, filtered above) and would be forbidden
            // anyway — weakening an assertion is the one edit this server must never propose.
            default:
                break;
        }
    }

    /// <summary>
    /// A <c>timeout</c>-classified step always yields a <c>timeouts</c> proposal, and additionally a
    /// <c>match</c> proposal when the run OBSERVED values that simply did not match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The discriminator is <see cref="VerdictEvidence.ObservedValues"/> — the fact the classifier
    /// established from the UNTRIMMED attempt list and published structurally.</b> The variant is
    /// KNOWN at classification time, so there is nothing to infer here and nothing to be uncertain
    /// about.
    /// </para>
    /// <para>
    /// <b>An earlier version had a third, "unknown" state and it was wrong twice over</b> (a review
    /// finding). It re-derived the discriminator from <see cref="StepDiagnosis.Attempts"/>, which the
    /// response tiers trim, and when that list came back empty on a step whose attempts had been
    /// trimmed away it withheld the match proposal and appended "whether any value was observed could
    /// not be assessed" — to a rationale whose own first sentence read "Observed 6 value(s) but none
    /// matched". It fabricated ignorance about a fact it was holding, and contradicted itself in the
    /// same paragraph. Reading the evidence removes the whole state.
    /// </para>
    /// </remarks>
    private static void AddTimeoutProposals(StepDiagnosis step, List<SpecEditProposal> proposals)
    {
        // A reason built outside the rule table carries no evidence; treat that as "no observations",
        // the conservative branch (one proposal rather than two).
        var observedValues = step.Reason!.Evidence?.ObservedValues ?? false;
        var stepId = Identifier(step.StepId);
        var quotedStepId = YamlQuote(stepId);

        proposals.Add(new SpecEditProposal(
            step.StepId,
            SpecEditScopes.Timeouts,
            step.Reason.Hint,
            Format(TimeoutsFragment, stepId, quotedStepId)));

        if (observedValues)
        {
            proposals.Add(new SpecEditProposal(
                step.StepId,
                SpecEditScopes.Match,
                $"{step.Reason.Hint} Values WERE observed on at least one attempt, so raising the " +
                "timeout alone is unlikely to help.",
                Format(MatchFragment, stepId, quotedStepId)));
        }
    }

    /// <summary>
    /// The <c>environment</c>-scope proposal for one environment-error record, or
    /// <see langword="null"/> when the rule table did not classify it into an editable shape.
    /// </summary>
    /// <remarks>
    /// Every proposal from here carries a <see langword="null"/>
    /// <see cref="SpecEditProposal.StepId"/> — an environment-error record is not a step. See that
    /// property's own remarks for why that is an interpretation rather than an omission.
    /// </remarks>
    private static SpecEditProposal? BuildEnvironmentProposal(EnvironmentErrorDiagnosis error)
    {
        if (error.Reason?.Kind is not { } kind)
        {
            // Fail-closed, inherited: an ErrorKind US-S4-01 declined to classify gets guidance text
            // (BuildEnvironmentGuidance) and no mechanical suggestion. Guessing an edit from an
            // unrecognised failure is exactly the fabrication that rule exists to prevent.
            return null;
        }

        var evidence = error.Reason.Evidence;

        return kind switch
        {
            // The image comes from the classifier's OWN extraction, published structurally — never a
            // second heuristic here (which would re-open the accepted residual that the extraction
            // may admit a credential-shaped token) and never recovered from the hint sentence (which
            // put the RESOURCE NAME into this slot whenever the fallback variant fired).
            VerdictReasonKinds.Pull => new SpecEditProposal(
                StepId: null,
                SpecEditScopes.Environment,
                error.Reason.Hint,
                Format(EnvironmentServiceImageFragment, YamlQuote(Identifier(evidence?.ImageReference, ImagePlaceholder)))),

            VerdictReasonKinds.Unhealthy => new SpecEditProposal(
                StepId: null,
                SpecEditScopes.Environment,
                error.Reason.Hint,
                Format(
                    EnvironmentHealthFragment,
                    ResourceKey(error.ResourceName),
                    YamlQuote(ResourceKey(error.ResourceName)),
                    evidence?.HealthWindowMs is { } ms ? $"{Identifier(ms)}ms" : "its startup window",
                    ResourceNote(error.ResourceName))),

            VerdictReasonKinds.Seed => new SpecEditProposal(
                StepId: null,
                SpecEditScopes.Environment,
                error.Reason.Hint,
                Format(
                    EnvironmentSeedFragment,
                    ResourceKey(error.ResourceName),
                    YamlQuote(ResourceKey(error.ResourceName)),
                    ResourceNote(error.ResourceName))),

            _ => null,
        };
    }

    /// <summary>What the image slot carries when the engine named no image at all.</summary>
    private const string ImagePlaceholder = "<registry>/<repository>:<tag>";

    /// <summary>What a resource KEY carries when the engine named no resource — see <see cref="ResourceKey"/>.</summary>
    private const string ResourcePlaceholder = "<resource-name>";

    /// <summary>
    /// A comment line appended when the resource could not be named, so the placeholder above is not
    /// mistaken for the engine's own answer.
    /// </summary>
    private const string UnnamedResourceNote = "\n# (The engine did not name the resource; fill in the one this failure concerns.)";

    /// <summary>
    /// The ONE gate every engine-supplied identifier passes through before it is spliced into a
    /// fragment: sanitised, then capped to <see cref="VerdictReasonClassifier.MaxValueChars"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bounding happens HERE because nothing upstream does it for this surface.</b> A step id and
    /// a resource name reach a <see cref="Diagnosis"/> at <c>SuiteEventParser</c>'s 2,000-character
    /// parse cap; a fragment splices its identifier TWICE, and ten proposals of that size were ~42 KB
    /// — enough on their own to drive <c>diagnose_run</c>'s shrink ladder to its final stage, which
    /// drops every proposal. So the failure mode of an unbounded identifier is not a big response, it
    /// is SILENTLY NO ADVICE (a security MEDIUM). Sanitise-then-cap, in that order, for the reason
    /// <see cref="VerdictReasonClassifier"/>'s own splice sites use it: sanitisation can expand text
    /// sixfold, so capping first would bound the input rather than the output.
    /// </para>
    /// <para>
    /// <b>This narrows that worst case to roughly 30&#160;KB; it does not close it.</b>
    /// <see cref="SpecEditProposal.StepId"/> stays raw by design (it is the host's correlation key —
    /// see this type's remarks), so the remaining ~20&#160;KB rides on that field rather than inside
    /// the fragments. The ladder is what has to shed it, and that is US-S4-04's work.
    /// </para>
    /// </remarks>
    private static string Identifier(string value) =>
        VerdictReasonClassifier.Cap(TextSanitiser.SanitiseForDisplay(value), VerdictReasonClassifier.MaxValueChars);

    /// <summary><see cref="Identifier(string)"/> with a placeholder for the absent case.</summary>
    private static string Identifier(string? value, string placeholder) =>
        string.IsNullOrWhiteSpace(value) ? placeholder : Identifier(value);

    /// <summary>
    /// Renders an identifier as a YAML SINGLE-QUOTED scalar — the form every key and value slot in
    /// the templates uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bare identifiers do not round-trip, which a review demonstrated rather than supposed.</b>
    /// A step id containing <c>" #"</c> silently truncates its line when the fragment is applied
    /// (everything after it becomes a YAML comment); one containing <c>": "</c> turns a scalar into
    /// a nested mapping; one starting with <c>-</c> reads as a sequence entry. All three are legal
    /// step ids as far as this server is concerned — it relays whatever the engine reported — so a
    /// fragment that spliced them bare was emitting advice that changes meaning when pasted.
    /// </para>
    /// <para>
    /// Single quotes rather than double: inside a YAML single-quoted scalar the ONLY escape is a
    /// doubled quote, so no backslash sequence in an identifier can be reinterpreted. The identifier
    /// is already sanitised to printable ASCII and capped by <see cref="Identifier(string)"/> before
    /// it gets here, so quoting is the last transformation, not a substitute for either.
    /// </para>
    /// <para>
    /// PROSE slots in the templates' <c>#</c> comment lines take the UNQUOTED value: a comment cannot
    /// have its meaning changed by its content, and reading "Step 'poll-01' ran out of time" is what
    /// a human wants. That is why each template takes an identifier twice.
    /// </para>
    /// </remarks>
    private static string YamlQuote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// A resource name rendered as a YAML KEY, with the parser's "(unknown)" sentinel replaced by a
    /// schema-plausible placeholder plus a note.
    /// </summary>
    /// <remarks>
    /// <b>The sentinel must never land as a key.</b> <c>SuiteEventParser.UnnamedResourceSentinel</c>
    /// is the string <c>(unknown)</c> — prose, not an identifier — and emitting
    /// <c>dependencies:\n  (unknown):</c> would be advice a reader could not apply and, pasted
    /// verbatim, a suite that does not validate. A placeholder plus one comment line says the same
    /// thing honestly.
    /// </remarks>
    private static string ResourceKey(string resourceName) =>
        string.Equals(resourceName, Run.SuiteEventParser.UnnamedResourceSentinel, StringComparison.Ordinal)
            ? ResourcePlaceholder
            : Identifier(resourceName);

    /// <summary>The note that accompanies <see cref="ResourcePlaceholder"/>, or nothing when the resource WAS named.</summary>
    private static string ResourceNote(string resourceName) =>
        string.Equals(resourceName, Run.SuiteEventParser.UnnamedResourceSentinel, StringComparison.Ordinal)
            ? UnnamedResourceNote
            : string.Empty;

    private static string Format(string template, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, template, arguments);
}
