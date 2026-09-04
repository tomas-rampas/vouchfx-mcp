using System.Text.Json.Serialization;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Normalization;

/// <summary>
/// <c>normalize_suite</c>'s result (US-S2-04): the suite's canonical text, what asking for it cost,
/// and the full <c>validate_suite</c>-shaped verdict for it.
/// </summary>
/// <param name="NormalizedYaml">
/// The suite rendered in <see cref="SuiteNormalizer"/>'s canonical form, or <see langword="null"/>
/// for one of three reasons a caller can tell apart without guessing:
/// <list type="bullet">
/// <item><description>the caller did not opt in — <c>normalize</c> defaults to
/// <see langword="false"/> because normalization DROPS COMMENTS (see <see cref="SuiteNormalizer"/>'s
/// remarks for the measured evidence). <see cref="CommentsDropped"/> is then
/// <see langword="false"/> and <see cref="NormalizationRefused"/> is <see langword="null"/>;</description></item>
/// <item><description>there was no document to canonicalise at all (unparseable YAML, a
/// safety-guard rejection, a root that is not a mapping), in which case <paramref name="Validation"/>
/// carries the reason and its <c>Summary</c> is likewise <see langword="null"/>;</description></item>
/// <item><description>the canonical text was rendered and then REFUSED by the emission gate, in
/// which case <see cref="NormalizationRefused"/> names which half of the gate failed — see that
/// property.</description></item>
/// </list>
/// </param>
/// <param name="Validation">
/// The verdict, produced by the SAME <see cref="SuiteValidator"/> pipeline <c>validate_suite</c>
/// runs, at <see cref="ValidationLevel.Full"/> — never a second, parallel implementation, and never
/// a narrowed one. This is what makes the story's secret gate structural rather than a rule to
/// remember: VFX-D-1207 is detected here because the full semantic pass ran, not because
/// <c>normalize_suite</c> checks for it separately.
/// </param>
/// <param name="NormalizationRefused">
/// <see langword="null"/> in every ordinary outcome; otherwise one of the two constants on this
/// type, naming which half of <see cref="SuiteNormalizer"/>'s emission gate rejected the text it had
/// just produced.
/// </param>
/// <remarks>
/// <para>
/// <b>This is both the worker's wire type and the tool's payload</b>, the same double duty
/// <see cref="SuiteAnalysis"/> already performs — for the same reason: the canonical text is rendered
/// from a parse that only exists inside the isolated <c>--validate-worker</c> child process, so it
/// has to cross that boundary anyway, and a separate tool-layer shape would be a second definition of
/// one thing.
/// </para>
/// <para>
/// <b>The worker emits this shape ONLY when asked to normalise</b>
/// (<see cref="ValidationWorkerProtocol.NormaliseArgument"/>); a plain <c>--validate-worker</c>
/// invocation still writes a bare <see cref="SuiteAnalysis"/> exactly as it always has. That keeps
/// US-S2-04 additive at the worker boundary rather than reshaping the wire every existing
/// <c>validate_suite</c> and <c>run_suite</c> pre-flight call already travels.
/// </para>
/// <para>
/// <b>Why the refusal is a payload field and not a <c>VFX-E-####</c> code.</b> The
/// <c>VFX-E</c>/<c>VFX-D</c> taxonomy describes the CALLER'S input — what is wrong with the suite,
/// or with the arguments naming it. A gate refusal says nothing about the suite: the document is
/// fine, the verdict in <paramref name="Validation"/> is complete and trustworthy, and only this
/// server's own emitter could not render it faithfully. Minting a diagnostic code for it would put a
/// server-side limitation into a catalogue hosts read as "things wrong with your file", and would
/// oblige a <c>docs/errors/</c> page describing a condition an author cannot act on. A null
/// <c>normalizedYaml</c> plus a content-free reason says exactly as much as is true. The reason
/// strings are deliberately CONTENT-FREE — a fixed token, never an echo of the document — because
/// this field crosses the same boundary as everything else here under the secret-hygiene rule.
/// </para>
/// <para>
/// <b><c>meta</c> is not a field here.</b> It is stamped once, at the top level, by
/// <c>Tools.StructuredToolResult.Success</c> — so a host reads <c>{normalizedYaml, commentsDropped,
/// normalizationRefused, validation, meta}</c> and the nested <paramref name="Validation"/> is
/// byte-for-byte the object <c>validate_suite</c> returns minus its own stamp.
/// </para>
/// </remarks>
public sealed record SuiteNormalization(
    [property: JsonPropertyOrder(0)] string? NormalizedYaml,
    [property: JsonPropertyOrder(3)] SuiteAnalysis Validation,
    [property: JsonPropertyOrder(2)] string? NormalizationRefused = null)
{
    /// <summary>
    /// <see cref="NormalizationRefused"/> when the canonical text this server emitted could not be
    /// parsed back at all — the document would have been handed to a host as text to write over the
    /// author's file, and it is not valid YAML.
    /// </summary>
    public const string CanonicalTextDidNotReParse = "canonical-text-did-not-re-parse";

    /// <summary>
    /// <see cref="NormalizationRefused"/> when the canonical text parses, but to a DIFFERENT
    /// document than the one that went in — the failure mode that is worse than the one above,
    /// because nothing downstream would notice it.
    /// </summary>
    public const string CanonicalTextChangedTheDocument = "canonical-text-changed-the-document";

    /// <summary>
    /// Whether the author's <c>#</c> comments were discarded to produce
    /// <see cref="NormalizedYaml"/> — <see langword="true"/> exactly when there is canonical text,
    /// because on the pinned YamlDotNet producing it and losing them are the same act (spec open
    /// decision #2, closed as outcome (b); see <see cref="SuiteNormalizer"/>'s remarks).
    /// </summary>
    /// <remarks>
    /// <b>A DERIVED property, deliberately, rather than a fourth constructor parameter.</b> Outcome
    /// (b) requires the loss to be stated in the tool's description AND on its output; a settable
    /// field would let those two disagree, and the one value it could ever take that
    /// <see cref="NormalizedYaml"/> does not already imply is a wrong one. Being computed also means
    /// it survives the worker's JSON boundary correctly without being trusted across it: it is
    /// written by the child, ignored on the parent's read (no setter, no constructor parameter), and
    /// recomputed from the field it describes.
    /// </remarks>
    [JsonPropertyOrder(1)]
    public bool CommentsDropped => NormalizedYaml is not null;

    /// <summary>
    /// Wraps a verdict that has no canonical text to go with it — the shape every outcome takes
    /// when normalization was not requested, or when no document was ever built.
    /// </summary>
    public static SuiteNormalization WithoutCanonicalYaml(SuiteAnalysis validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        return new SuiteNormalization(null, validation);
    }

    /// <summary>
    /// Wraps a verdict whose canonical text was rendered and then rejected by
    /// <see cref="SuiteNormalizer"/>'s emission gate.
    /// </summary>
    /// <param name="validation">The verdict, which is unaffected by the refusal and still complete.</param>
    /// <param name="reason">
    /// <see cref="CanonicalTextDidNotReParse"/> or <see cref="CanonicalTextChangedTheDocument"/>.
    /// </param>
    public static SuiteNormalization RefusedCanonicalYaml(SuiteAnalysis validation, string reason)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        return new SuiteNormalization(null, validation, reason);
    }
}
