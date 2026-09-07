using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Specs;

/// <summary>
/// Parses ONE suite file into its <see cref="SpecIndexWorkerEntry"/> — the only code in this server
/// that hands a workspace suite's bytes to YamlDotNet, and therefore code that <b>must only ever run
/// inside the spec-index worker child process</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE RULE: nothing in the long-lived server process may call this.</b> Its whole body is the
/// hazard <see cref="SpecIndexWorkerProtocol"/>'s header describes — a twelve-byte suite can drive
/// YamlDotNet's Scanner into an uninterruptible ~100%-CPU spin, and
/// <see cref="YamlSafetyGuard.CheckNestingDepth"/> cannot save it because that check IS the Scanner.
/// Calling this on a request thread wedges the server permanently. The rule is held structurally by
/// <c>SpecIndexParserSourceGuardTests</c>, which pins the call sites in <c>src/</c> to exactly
/// <c>Program.cs</c> (the worker) with fail-closed exact equality — the same shape
/// <c>RunLockSourceGuardTests</c> and <c>CursorCallSiteSourceGuardTests</c> already use for their own
/// must-not-spread invariants.
/// </para>
/// <para>
/// <b>Reuses <c>Validation/</c>'s parse path rather than reading YAML a second way</b>, which is
/// US-S5-01 AC-005's own requirement:
/// <see cref="YamlSafetyGuard.Check"/> (which must still run first — inside the worker it is cheap
/// insurance against the OTHER YAML hazards it was built for, and the one it cannot cover is exactly
/// what the process boundary covers), then
/// <see cref="YamlToJsonConverter"/>, then <see cref="SuiteSummaryBuilder"/>. The step types this
/// index reports are therefore the identical values <c>validate_suite</c>'s own
/// <c>summary.stepTypes</c> carries, from the identical walk.
/// </para>
/// <para>
/// <b>It deliberately does NOT validate.</b> No schema pass, no semantic pass. A directory of five
/// hundred suites would otherwise cost five hundred JSON Schema evaluations per resource read, for an
/// answer ("does one of these already cover my flow?") that never needed a verdict. A host that has
/// picked a candidate calls <c>validate_suite</c> on it.
/// </para>
/// </remarks>
public static class SpecIndexParser
{
    /// <summary>Longest <c>metadata.name</c> echoed into an entry.</summary>
    public const int MaxNameChars = 200;

    /// <summary>Longest single tag echoed into an entry.</summary>
    public const int MaxTagChars = 100;

    /// <summary>
    /// Longest single step type echoed into an entry. Its own constant rather than reusing
    /// <see cref="MaxTagChars"/> (a security review's finding): the two bound unrelated vocabularies,
    /// and a step type is a dotted <c>family.provider</c> identifier whose longest real value at the
    /// pinned commit is under thirty characters.
    /// </summary>
    public const int MaxStepTypeChars = 64;

    /// <summary>Most tags echoed per entry.</summary>
    public const int MaxTagsPerSpec = 20;

    /// <summary>
    /// Most step types echoed per entry. The registry has 25 step types at the pinned commit, so this
    /// cannot bite on a well-formed suite; it exists so a document declaring ten thousand distinct
    /// bogus types cannot inflate one index entry without bound. <see cref="SuiteSummaryBuilder"/>
    /// caps its own lists far higher (1,000), which is right for a per-suite digest and wrong for a
    /// per-directory index.
    /// </summary>
    public const int MaxStepTypesPerSpec = 40;

    /// <summary>Longest parse-failure sentence echoed into an entry.</summary>
    public const int MaxParseErrorChars = 300;

    /// <summary>
    /// Reads and lightly parses the suite at <paramref name="path"/>. <b>Never throws</b> — every
    /// failure becomes a <see cref="SpecIndexWorkerEntry.Readable"/>-false entry with one sentence.
    /// </summary>
    /// <param name="index">The entry's position in the worker's input array; echoed back verbatim.</param>
    /// <param name="path">
    /// An absolute suite path the PARENT has already enumerated and containment-checked. This method
    /// performs no path safety of its own and must never be handed an unchecked path.
    /// </param>
    public static SpecIndexWorkerEntry Parse(int index, string path)
    {
        if (!TryReadSuiteText(path, out var yamlText, out var readFailure))
        {
            return Unreadable(index, readFailure!);
        }

        // Still run, and still FIRST — but understood correctly. It bounds size, line length, anchors
        // and aliases, and its nesting check runs the Scanner. Inside this child process that last
        // point is no longer a hazard: a spin here costs one killed worker and one degraded entry, and
        // the parent resumes. Outside it, it would cost the server.
        if (YamlSafetyGuard.Check(yamlText) is { } safetyError)
        {
            return Unreadable(index, safetyError.Message);
        }

        JsonDocument document;
        try
        {
            document = YamlToJsonConverter.Convert(yamlText);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException or JsonException)
        {
            // The same three families SuiteValidator.AnalyseYaml catches, and for the same reasons it
            // documents there (a syntax error; a syntactically empty document; a raw control
            // character that survives YamlDotNet's JSON re-emission as invalid JSON).
            //
            // THE MESSAGE IS ECHOED HERE, AND DELIBERATELY IS NOT ON Program.cs's FALLBACK PATH.
            // The two look contradictory until the difference is named, so it is named (a security
            // review asked for exactly this reconciliation):
            //
            //   * HERE the exception type is one of three KNOWN parser families, whose messages are
            //     the diagnosis — "(Line 4, Col 3): mapping values are not allowed in this context" is
            //     the entire value of reporting a parse failure at all, and it is the same text
            //     validate_suite already returns for the same file through SuiteValidator. Yes, a
            //     YamlException can quote the offending LINE; that line is from a suite in the
            //     operator's OWN specs directory, being reported back to that same operator, and it is
            //     capped and control-character-sanitised by Unreadable() before it goes anywhere.
            //   * Program.cs's catch is the UNKNOWN-exception fallback: an arbitrary type whose message
            //     this repository has never inspected. There the message is refused and only the type
            //     name printed, because "we do not know what this text contains" is a different
            //     situation from "we know exactly what this text contains and it is the answer".
            //
            // The rule both sides follow is therefore one rule: echo a message only where its content
            // is known and bounded. Widening this catch to more exception types means re-deciding
            // that, not inheriting it.
            return Unreadable(index, ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;

            // The SAME walk validate_suite's own summary comes from. The fact set half is discarded:
            // it exists for semantic rules to decide set membership against and is never serialised.
            var digest = SuiteSummaryBuilder.Build(root);

            return new SpecIndexWorkerEntry(
                Index: index,
                Name: ReadMetadataName(root),
                Tags: ReadMetadataTags(root),
                StepTypes: [.. digest.Summary.StepTypes
                    .Take(MaxStepTypesPerSpec)
                    .Select(stepType => CapAndSanitise(stepType, MaxStepTypeChars))],
                Steps: digest.Summary.Steps,
                Readable: true,
                ParseError: null);
        }
    }

    /// <summary>How many times a transient <see cref="IOException"/> is retried before giving up.</summary>
    /// <remarks>
    /// <b>A sharing violation on a suite file is transient and ordinary, not a property of the
    /// file</b> — and treating it as permanent produced a real, MEASURED intermittent defect. On
    /// Windows a file that was just written (by an editor, by a generator, or by this repository's own
    /// tests moments earlier) can briefly refuse to open while an antivirus or search indexer holds
    /// it. Without a retry the index published that suite as unreadable, which is both wrong and
    /// unstable: it depended on what else the machine happened to be doing. Measured: a full-suite run
    /// under parallel load failed on a healthy no-metadata suite in 432 ms — far too fast to be a
    /// timeout, and exactly the shape of a first-open collision.
    /// <para>
    /// Bounded and short, because this runs per file inside the worker: three attempts at
    /// <see cref="ReadRetryDelay"/> apart is 50 ms of worst-case delay for a file that never opens,
    /// against a stall clock measured in seconds. <see cref="UnauthorizedAccessException"/> is NOT
    /// retried — a permissions failure is not transient and retrying it only wastes the budget.
    /// </para>
    /// </remarks>
    private const int MaxReadAttempts = 3;

    /// <summary>Delay between read attempts — see <see cref="MaxReadAttempts"/>.</summary>
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Reads the suite text, retrying a transient <see cref="IOException"/>. Never throws; returns
    /// <see langword="false"/> with a one-sentence reason instead.
    /// </summary>
    private static bool TryReadSuiteText(string path, out string yamlText, out string? failure)
    {
        yamlText = string.Empty;
        failure = null;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Size-checked before the read rather than after, so an accidentally-enormous file in
                // the suites directory costs a stat instead of its own length in memory. The bound is
                // YamlSafetyGuard's own, shared rather than restated: a file this server would refuse
                // to parse is a file it should not read either.
                var length = new FileInfo(path).Length;
                if (length > YamlSafetyGuard.MaxSuiteSizeBytes)
                {
                    failure =
                        $"File is {length} bytes, over the {YamlSafetyGuard.MaxSuiteSizeBytes}-byte "
                        + "suite size limit, so it was listed but not parsed.";
                    return false;
                }

                yamlText = File.ReadAllText(path);
                return true;
            }
            catch (IOException ex) when (attempt < MaxReadAttempts)
            {
                _ = ex;

                // Synchronous sleep, deliberately: this is a one-shot child process with nothing else
                // to do, so there is no thread to free and an async hop would only add machinery.
                Thread.Sleep(ReadRetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                failure = $"File could not be read ({ex.GetType().Name}).";
                return false;
            }
        }
    }

    private static SpecIndexWorkerEntry Unreadable(int index, string reason) =>
        new(
            Index: index,
            Name: null,
            Tags: [],
            StepTypes: [],
            Steps: 0,
            Readable: false,
            ParseError: CapAndSanitise(reason, MaxParseErrorChars));

    private static string? ReadMetadataName(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = name.GetString();
        return string.IsNullOrEmpty(text) ? null : CapAndSanitise(text, MaxNameChars);
    }

    /// <remarks>
    /// Returns the concrete <see cref="List{T}"/> rather than <see cref="IReadOnlyList{T}"/> because
    /// CA1859 requires it of a private method whose only caller is in this file. The record field it
    /// feeds is still typed as the interface, which is where the immutability promise belongs.
    /// </remarks>
    private static List<string> ReadMetadataTags(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var tag in tags.EnumerateArray())
        {
            if (result.Count == MaxTagsPerSpec)
            {
                break;
            }

            if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 } text)
            {
                result.Add(CapAndSanitise(text, MaxTagChars));
            }
        }

        return result;
    }

    /// <summary>
    /// Caps <paramref name="value"/> BEFORE sanitising and AGAIN afterwards — the two-stage shape
    /// <see cref="PathSafetyGuard.CapAndSanitisePathForDisplay"/> documents: sanitising can EXPAND
    /// length (each non-printable character becomes a 6-character <c>\uXXXX</c> escape), so only the
    /// second cap actually bounds what reaches the wire.
    /// </summary>
    /// <remarks>
    /// Every string here comes from a file on the operator's own disk rather than from a caller's
    /// argument, which lowers the stakes but does not remove them: an index is content a host may
    /// render, and a suite whose <c>metadata.name</c> carries an ANSI escape sequence would otherwise
    /// write it into that host's terminal.
    /// </remarks>
    internal static string CapAndSanitise(string value, int maxChars)
    {
        var rawCapped = value.Length > maxChars ? value[..maxChars] : value;
        var sanitised = TextSanitiser.SanitiseForDisplay(rawCapped);
        return sanitised.Length > maxChars ? sanitised[..maxChars] : sanitised;
    }
}
