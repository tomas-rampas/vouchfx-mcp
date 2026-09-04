using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// One problem <see cref="SuiteValidator"/> found with a suite, or a reason it could not be
/// checked at all.
/// </summary>
/// <param name="Code">
/// A stable <see cref="VfxCodeCatalogue"/> code naming the problem — see that type for the full
/// table, the rationale behind each code's range, and its <c>retryable</c>/<c>docsUrl</c> metadata.
/// US-S1-04 replaced this field's former ad-hoc <c>kind</c> string with this code; the mapping was
/// one-to-one, so this field's cardinality and position are unchanged and only its VALUES differ.
/// The full set this can be, across every producer:
/// <list type="bullet">
/// <item><description><see cref="VfxCodeCatalogue.SuiteFileNotFound"/> (was <c>file-not-found</c>) — the
/// suite file does not exist (EDGE-003a).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.SuiteFileUnreadable"/> (was <c>file-access-error</c>) —
/// the file exists but could not be read (permissions, a locked file, a too-long path, …; N1).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.PathOutsideWorkspace"/> (was <c>invalid-path</c>) — the
/// path is a UNC/network path, rejected before any filesystem call is made against it (M2).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.SuiteFileTooLarge"/> (was <c>too-large</c>) — the file
/// exceeds <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>, rejected by its length alone before its
/// content is read (B1).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.SuiteNestingTooDeep"/> (was <c>too-deep</c>) — the YAML
/// nests deeper than <see cref="YamlSafetyGuard.MaxNestingDepth"/> (block/flow collections combined),
/// rejected before any recursive-descent parse (B1).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.SuiteAliasLimitExceeded"/> (was <c>alias-limit</c>) — the
/// YAML declares more anchors/aliases than <see cref="YamlSafetyGuard.MaxAnchorCount"/>/
/// <see cref="YamlSafetyGuard.MaxAliasCount"/> allow, rejected before any alias expansion
/// ("billion laughs", B1).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.YamlParseError"/> (was <c>yaml-parse</c>) — the YAML is
/// otherwise unparseable (EDGE-003b).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.SchemaViolation"/> (was <c>schema</c>) — a genuine JSON
/// Schema violation (EDGE-003c).</description></item>
/// <item><description><see cref="VfxCodeCatalogue.UnknownStepType"/> (was <c>unknown-step-type</c>) — a
/// step's <c>type</c> does not match any type the embedded schema defines — the schema's own if/then
/// structure cannot express this itself; see <see cref="SuiteValidator"/>'s remarks.</description></item>
/// <item><description><see cref="VfxCodeCatalogue.ValidationTimeout"/> (was <c>validation-timeout</c>) —
/// <see cref="ValidationWorkerClient"/>'s isolated worker process did not finish within its timeout and
/// was killed; the suite's actual validity was never determined.</description></item>
/// <item><description><see cref="VfxCodeCatalogue.ValidationWorkerFailed"/> (was
/// <c>validation-worker-failed</c>) — <see cref="ValidationWorkerClient"/>'s worker process could not be
/// started, exited with a non-zero code, produced more output than its cap allows, or produced output that
/// could not be parsed as a result; the suite's actual validity was never determined.</description></item>
/// </list>
/// <para>
/// <b>The list above is split by prefix, and that split is load-bearing.</b> The <c>VFX-D-</c> entries
/// are DIAGNOSTICS — the pipeline reached a determination about the suite, and the tool reports them as
/// data on a successful call. The <c>VFX-E-</c> entries are ERRORS — the suite's validity was never
/// determined, and the tool surfaces them as <c>isError: true</c> carrying a single
/// <see cref="VfxError"/>. Both still travel in this record inside the pipeline; the split is applied
/// once, at the tool boundary. See <see cref="VfxCodeCatalogue"/>'s header for the rule.
/// </para>
/// </param>
/// <param name="InstancePath">
/// A JSON Pointer (RFC 6901) to the offending location in the suite document (e.g.
/// <c>/steps/1/type</c>), or <see langword="null"/> when the problem has no single location
/// (e.g. the file could not be found at all, or the whole document is empty).
/// </param>
/// <param name="Message">A human-readable description of the problem.</param>
/// <param name="Line">
/// The 1-based YAML source line the problem was resolved to on a best-effort basis, or
/// <see langword="null"/> when it could not be derived.
/// </param>
/// <param name="Column">
/// The 1-based YAML source column, populated only for <see cref="VfxCodeCatalogue.YamlParseError"/>
/// findings (where the underlying parser reports one); <see langword="null"/> otherwise.
/// </param>
public sealed record SuiteValidationError(string Code, string? InstancePath, string Message, long? Line, long? Column);

/// <summary>The outcome of validating one <c>.e2e.yaml</c> suite — REQ-003's validate_suite result contract.</summary>
/// <param name="Valid"><see langword="true"/> only when <paramref name="Errors"/> is empty.</param>
/// <param name="Errors">Every problem found; empty when the suite is fully conforming.</param>
public sealed record ValidateSuiteResult(bool Valid, IReadOnlyList<SuiteValidationError> Errors);
