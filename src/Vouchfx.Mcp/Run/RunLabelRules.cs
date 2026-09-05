using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// The ONE definition of what a run's <c>labels</c> map may contain — bounds and rules, shared by the
/// tool boundary that rejects a bad call and the storage layer that refuses to record one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two enforcers, one rule, deliberately</b> (a security review's MINOR finding). Labels used to
/// be validated only in <c>RunSuiteOrchestrator.ValidateLabels</c>, which made every
/// <see cref="IRunRegistry"/> implementation trust that its caller had already done so — a trust the
/// interface never states and that nothing enforces for a future second caller.
/// <see cref="RunRegistryCore.CreateStartedEntry"/> now applies these same rules itself, matching the
/// doctrine this codebase already follows for security-relevant parameters (see
/// <see cref="PathSafetyGuard.CheckLocalPath"/>'s no-default-workspace-parameter note): the layer
/// that persists is the layer that refuses, rather than the layer that assumes.
/// </para>
/// <para>
/// <b>The two enforcers answer differently on purpose.</b> The tool boundary returns a MESSAGE, which
/// becomes a catalogued <c>VFX-E-1006</c> a caller can act on; the storage layer THROWS
/// <see cref="ArgumentException"/>, because by then the call has already been accepted and a map
/// reaching it in violation is a bug in this server, not a bad request. Neither restates the rules —
/// both call <see cref="Validate"/>.
/// </para>
/// <para>
/// <b>Why these bounds exist at all</b> is a storage concern rather than an injection one: nothing
/// here is spliced into a command line (the pinned engine has no labels flag), but everything here is
/// persisted verbatim into a JSON document on the operator's disk and read back by later server
/// processes. See <c>RunSuiteOrchestrator.MaxLabelCount</c> and
/// <c>FileRunRegistry.MaxEntryFileBytes</c> for the byte arithmetic these character bounds are the
/// cheap first line of.
/// </para>
/// </remarks>
internal static class RunLabelRules
{
    /// <summary>The largest number of label entries a run may carry.</summary>
    public const int MaxCount = 20;

    /// <summary>The largest length, in characters, a single label KEY may have.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The largest length, in characters, a single label VALUE may have.</summary>
    public const int MaxValueLength = 256;

    /// <summary>
    /// Returns a human-readable reason <paramref name="labels"/> is unacceptable, or
    /// <see langword="null"/> when it is fine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A null or blank KEY, a null VALUE, and an over-long either are all reachable from the wire
    /// regardless of this server's compile-time nullable-reference-type annotations — a JSON object
    /// may legally carry <c>{"trigger": null}</c>, exactly as a JSON string array may carry a null
    /// element (the same reasoning <c>RunSuiteOrchestrator.ValidateTags</c> records for <c>tags</c>).
    /// </para>
    /// <para>
    /// <b>A control character is REFUSED, not sanitised</b> — the one place in this server where a
    /// caller string is rejected for its characters rather than escaped for display. The reason is
    /// what a label is FOR: a host writes <c>{"trigger":"agent:author"}</c> in order to find that run
    /// again by that exact value later. Escaping a stray character into its <c>\uXXXX</c> form, the
    /// way every displayed string here is sanitised, would store a value that no longer matches what
    /// the host holds — silently, and only for the labels unlucky enough to contain one. A
    /// correlation failure is strictly worse than a refusal a caller can see and fix. (Tags and paths
    /// are sanitised rather than refused because they are ECHOED into messages, which is a display
    /// problem; a label is STORED, which is an identity one.)
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT checked: the VALUE's meaning.</b> Labels are caller-authored
    /// metadata and are persisted verbatim; this server does not scan them for secrets, and a host
    /// must not put one in a label. The engine remains the sole redaction authority (plan §2.7
    /// invariant 4), and inventing a redaction rule here would be this server making a judgement
    /// about content it is not the authority on.
    /// </para>
    /// </remarks>
    public static string? Validate(IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count > MaxCount)
        {
            return $"Too many labels: {labels.Count} supplied, at most {MaxCount} are accepted.";
        }

        foreach (var (key, value) in labels)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "Label keys must not be null, empty, or whitespace-only.";
            }

            if (key.Length > MaxKeyLength)
            {
                return $"Label key exceeds the {MaxKeyLength}-character limit ({key.Length} characters).";
            }

            if (value is null)
            {
                return $"Label '{TextSanitiser.SanitiseForDisplay(key)}' has a null value. Use an empty "
                       + "string to record a label with no value.";
            }

            if (value.Length > MaxValueLength)
            {
                return $"Label '{TextSanitiser.SanitiseForDisplay(key)}' has a value exceeding the "
                       + $"{MaxValueLength}-character limit ({value.Length} characters).";
            }

            if (ContainsControlCharacter(key) || ContainsControlCharacter(value))
            {
                return $"Label '{TextSanitiser.SanitiseForDisplay(key)}' contains a control character in "
                       + "its key or value. Labels are stored and matched verbatim, so they must be "
                       + "plain text.";
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="text"/> contains any Unicode control character.</summary>
    private static bool ContainsControlCharacter(string text)
    {
        foreach (var character in text)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}
