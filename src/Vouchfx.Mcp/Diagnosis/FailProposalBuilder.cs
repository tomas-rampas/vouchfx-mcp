namespace Vouchfx.Mcp.Diagnosis;

/// <summary>
/// Deterministic, template-based Fail proposal and EnvironmentError guidance builder for
/// <c>diagnose_run</c> (Spec C / M2 Healer). No model APIs — string assembly from event evidence only.
/// </summary>
/// <remarks>
/// Proposals are emitted only for notable steps whose own verdict is <c>Fail</c> and whose
/// observation is non-empty (usable expected/observed evidence). EnvironmentError and Inconclusive
/// never produce suite-rewrite patches. When environment-error records are present, infrastructure
/// guidance is assembled from those fields instead.
/// </remarks>
internal static class FailProposalBuilder
{
    /// <summary>Maximum proposals returned (aligns with the rich explain tier's notable-step cap).</summary>
    public const int MaxProposals = 10;

    /// <summary>Maximum characters kept in a proposal's rationale.</summary>
    public const int MaxRationaleChars = 500;

    /// <summary>Maximum characters kept in a proposal's patch body.</summary>
    public const int MaxPatchChars = 2_000;

    /// <summary>Maximum guidance lines returned for EnvironmentError.</summary>
    public const int MaxGuidanceLines = 12;

    /// <summary>
    /// Builds Fail-only review proposals from an already-built <see cref="Diagnosis"/>.
    /// Empty when there are no Fail steps with observation evidence.
    /// </summary>
    public static IReadOnlyList<FailProposal> BuildProposals(Diagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        var proposals = new List<FailProposal>(capacity: Math.Min(MaxProposals, diagnosis.NotableSteps.Count));

        foreach (var step in diagnosis.NotableSteps)
        {
            if (proposals.Count >= MaxProposals)
            {
                break;
            }

            if (!string.Equals(step.Verdict, "Fail", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.Observation))
            {
                // No usable expected/observed evidence — skip rather than inventing a patch.
                continue;
            }

            var observation = Cap(step.Observation.Trim(), MaxRationaleChars);
            var rationale = Cap(
                $"Step '{step.StepId}' failed with observation evidence: {observation}",
                MaxRationaleChars);
            var patch = Cap(BuildUnifiedDiffStylePatch(step.StepId, observation), MaxPatchChars);

            proposals.Add(new FailProposal(step.StepId, rationale, patch));
        }

        return proposals;
    }

    /// <summary>
    /// Builds infrastructure-oriented guidance when environment-error evidence is present or the
    /// overall verdict is <c>EnvironmentError</c>. Empty otherwise. Never emits suite YAML rewrites.
    /// </summary>
    public static IReadOnlyList<string> BuildEnvironmentGuidance(Diagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        var hasEnvErrors = diagnosis.EnvironmentErrors.Count > 0 || diagnosis.OmittedEnvironmentErrorCount > 0;
        var overallEnv = string.Equals(diagnosis.Verdict, "EnvironmentError", StringComparison.Ordinal);

        if (!hasEnvErrors && !overallEnv)
        {
            // Inconclusive may carry non-patch guidance (Spec C REQ-003); keep it separate from
            // infrastructure checklist text so Fail-vs-EnvError rules stay sharp.
            if (string.Equals(diagnosis.Verdict, "Inconclusive", StringComparison.Ordinal))
            {
                return
                [
                    "Inconclusive is neither a pass nor a product defect — inspect each notable " +
                    "step's RETRY attempt timeline and timeouts before changing assertions. " +
                    "Do not rewrite the suite solely to force a green run.",
                ];
            }

            return [];
        }

        var lines = new List<string>(MaxGuidanceLines)
        {
            "EnvironmentError is an infrastructure/topology problem — not a test defect. " +
            "Do not rewrite suite assertions to paper over it; fix the environment first.",
        };

        foreach (var error in diagnosis.EnvironmentErrors)
        {
            if (lines.Count >= MaxGuidanceLines)
            {
                break;
            }

            var detailSuffix = string.IsNullOrWhiteSpace(error.Detail)
                ? string.Empty
                : $": {Cap(error.Detail.Trim(), 300)}";
            lines.Add(
                $"Resource '{error.ResourceName}' reported {error.ErrorKind}{detailSuffix}.");
        }

        if (lines.Count < MaxGuidanceLines)
        {
            lines.Add(
                "Checklist: confirm Docker (or the target fabric) is running; images can be " +
                "pulled; health gates and WaitFor targets name the most specific dependency " +
                "(e.g. the database, not only the server); connection strings resolve in the " +
                "consumer's network context.");
        }

        if (diagnosis.OmittedEnvironmentErrorCount > 0 && lines.Count < MaxGuidanceLines)
        {
            lines.Add(
                $"{diagnosis.OmittedEnvironmentErrorCount} additional environment-error event(s) " +
                "were omitted from this response for size; see the events file for the full list.");
        }

        return lines;
    }

    /// <summary>
    /// Builds a unified-diff style review comment block incorporating the observation. Deterministic
    /// template only — not an LLM rewrite and not applied to disk.
    /// </summary>
    private static string BuildUnifiedDiffStylePatch(string stepId, string observation)
    {
        // Comment-form unified diff so hosts can display it as a review patch without implying a
        // precise line-anchored edit of a suite file we may not have been given (suite path optional).
        return
            $"""
            --- a/suite.e2e.yaml (step: {stepId})
            +++ b/suite.e2e.yaml (review-only proposal — do not auto-apply)
            @@ step {stepId} @@
             # Observation evidence from the events file:
             # {observation}
             #
             # Suggested human review:
             # 1. If the assertion is wrong for the intended product behaviour, update this
             #    step's expect/assert fields (or capture) so they match the intended contract.
             # 2. If the observation shows a genuine product defect, fix the system under test
             #    and re-run — do not weaken the assertion solely to force a green run.
             #
             # Example step fragment (placeholders — human must fill real type/fields):
             #   - id: {stepId}
             #     type: <family.provider>   # e.g. db-assert.postgres / http.rest
             #     # review expected vs actual in the observation above
            """;
    }

    private static string Cap(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars];
    }
}
