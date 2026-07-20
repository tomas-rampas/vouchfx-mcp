namespace Vouchfx.Mcp.Validation;

/// <summary>
/// One problem <see cref="SuiteValidator"/> found with a suite, or a reason it could not be
/// checked at all.
/// </summary>
/// <param name="Kind">
/// A short discriminator naming the KIND of problem. The full set this can be, across every
/// producer:
/// <list type="bullet">
/// <item><description><c>file-not-found</c> — the suite file does not exist (EDGE-003a).</description></item>
/// <item><description><c>file-access-error</c> — the file exists but could not be read (permissions, a
/// locked file, a too-long path, …; N1).</description></item>
/// <item><description><c>invalid-path</c> — the path is a UNC/network path, rejected before any
/// filesystem call is made against it (M2).</description></item>
/// <item><description><c>too-large</c> — the file exceeds <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>,
/// rejected by its length alone before its content is read (B1).</description></item>
/// <item><description><c>too-deep</c> — the YAML nests deeper than <see cref="YamlSafetyGuard.MaxNestingDepth"/>
/// (block/flow collections combined), rejected before any recursive-descent parse (B1).</description></item>
/// <item><description><c>alias-limit</c> — the YAML declares more anchors/aliases than
/// <see cref="YamlSafetyGuard.MaxAnchorCount"/>/<see cref="YamlSafetyGuard.MaxAliasCount"/> allow, rejected
/// before any alias expansion ("billion laughs", B1).</description></item>
/// <item><description><c>yaml-parse</c> — the YAML is otherwise unparseable (EDGE-003b).</description></item>
/// <item><description><c>schema</c> — a genuine JSON Schema violation (EDGE-003c).</description></item>
/// <item><description><c>unknown-step-type</c> — a step's <c>type</c> does not match any type the embedded
/// schema defines — the schema's own if/then structure cannot express this itself; see
/// <see cref="SuiteValidator"/>'s remarks.</description></item>
/// <item><description><c>validation-timeout</c> — <see cref="ValidationWorkerClient"/>'s isolated worker
/// process did not finish within its timeout and was killed; the suite's actual validity was never
/// determined.</description></item>
/// <item><description><c>validation-worker-failed</c> — <see cref="ValidationWorkerClient"/>'s worker
/// process could not be started, exited with a non-zero code, produced more output than its cap allows, or
/// produced output that could not be parsed as a result; the suite's actual validity was never
/// determined.</description></item>
/// </list>
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
/// The 1-based YAML source column, populated only for <c>yaml-parse</c> errors (where the
/// underlying parser reports one); <see langword="null"/> otherwise.
/// </param>
public sealed record SuiteValidationError(string Kind, string? InstancePath, string Message, long? Line, long? Column);

/// <summary>The outcome of validating one <c>.e2e.yaml</c> suite — REQ-003's validate_suite result contract.</summary>
/// <param name="Valid"><see langword="true"/> only when <paramref name="Errors"/> is empty.</param>
/// <param name="Errors">Every problem found; empty when the suite is fully conforming.</param>
public sealed record ValidateSuiteResult(bool Valid, IReadOnlyList<SuiteValidationError> Errors);
