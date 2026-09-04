using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The wire contract shared between the <c>validate_suite</c> orchestrator
/// (<see cref="ValidationWorkerClient"/>) and the hidden
/// <c>--validate-worker &lt;source&gt; [--level=&lt;level&gt;]</c> child-process mode it spawns (see
/// <c>Program.cs</c>'s worker-mode branch): a <see cref="SuiteAnalysis"/> serialised as JSON on the
/// worker's stdout — nothing else, ever.
/// </summary>
/// <remarks>
/// <para>
/// Kept as one small, shared type rather than letting each side build its own
/// <see cref="JsonSerializerOptions"/> and spell its own argument tokens independently: the worker
/// (in <c>Program.cs</c>) and the orchestrator (<see cref="ValidationWorkerClient"/>) must
/// serialise/deserialise with byte-for-byte compatible settings and agree on every argument, and a
/// future change to one side without the other would otherwise be a silent wire-format mismatch
/// rather than a compile error.
/// </para>
/// <para>
/// <b>US-S2-02 widened this contract in two ways, both additive.</b> The single positional
/// <c>&lt;source&gt;</c> argument is now either a suite file path (as before) or
/// <see cref="InlineYamlArgument"/>, in which case the suite text arrives on the worker's stdin;
/// and an optional <see cref="LevelArgumentPrefix"/> token selects which passes run. A worker
/// invoked exactly as before — one bare path, no level — behaves exactly as before.
/// </para>
/// </remarks>
public static class ValidationWorkerProtocol
{
    /// <summary>
    /// The command-line argument that switches the vouchfx-mcp executable into its hidden,
    /// one-shot worker mode: <c>--validate-worker &lt;source&gt;</c>. Checked first in
    /// <c>Program.cs</c>, before the ENGINE_PIN load or any MCP host bootstrap, since worker mode
    /// needs neither.
    /// </summary>
    public const string WorkerModeArgument = "--validate-worker";

    /// <summary>
    /// Written in the <c>&lt;source&gt;</c> position instead of a path to say "the suite text is
    /// coming on stdin" (US-S2-02's inline-<c>yaml</c> input).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why stdin and not a scratch file — the decision, recorded where it is made.</b> The two
    /// candidate transports for inline YAML were a temp-directory scratch file named in the existing
    /// <c>&lt;path&gt;</c> position, and this: the bytes streamed to the child's stdin. Stdin wins on
    /// four counts, and the first two are the load-bearing ones.
    /// </para>
    /// <list type="number">
    /// <item><description><b>No file lifecycle to get wrong.</b> A scratch file must be created with
    /// an unpredictable name in a directory the caller does not control, and deleted on EVERY exit
    /// path — including the two that matter most here, the wall-clock timeout and the whole-tree
    /// kill, which are precisely the paths where cleanup code is least likely to run and least
    /// likely to be noticed when it does not. A pipe has no such path: the handle dies with the
    /// process, whatever killed it.</description></item>
    /// <item><description><b>Nothing is written to any filesystem at all.</b> This server is
    /// read-only by invariant (CLAUDE.md). That invariant is scoped to SUITE files, so an ephemeral
    /// temp file would not literally violate it — but "the read-only server writes a file, only a
    /// temporary one, only in the system temp directory" is a sentence a reader now has to check,
    /// and stdin means there is nothing to check. It also removes the whole class of temp-directory
    /// concerns (a symlinked or hostile TMPDIR, a full disk, a scanner holding the file open) from
    /// the validate_suite path.</description></item>
    /// <item><description><b>The plumbing already exists and is already hardened.</b>
    /// <see cref="ValidationWorkerClient"/> has always set <c>RedirectStandardInput</c> — for the
    /// separate reason that the child must never inherit this server's real MCP stdin — and then
    /// closed the handle unused. Inline YAML writes to that same already-redirected handle before
    /// closing it; the isolation property the redirect exists for is untouched.</description></item>
    /// <item><description><b>The suite text never lands anywhere durable.</b> Caller-supplied YAML
    /// can contain <c>${secret:…}</c> references naming a secret store's layout; a pipe leaves no
    /// artefact behind for something else to read.</description></item>
    /// </list>
    /// <para>
    /// The cost stdin carries — a parent writing to a pipe while the child writes to another can
    /// deadlock if neither drains — is handled explicitly at the write site; see
    /// <c>ValidationWorkerClient.AnalyseAsyncCore</c>'s ordering comment.
    /// </para>
    /// <para>
    /// <b>This value is an IN-BAND discriminator, and that is only safe because the tool boundary
    /// rejects the collision.</b> It occupies the same argument position as a suite path, so a file
    /// literally named <c>--yaml-stdin</c> would be read as "the text is on stdin" and never opened
    /// — the caller would get a verdict about an empty document instead of about their file.
    /// <c>Tools.ValidateSuiteInput.TryResolve</c> refuses that exact <c>path</c> with VFX-E-1152
    /// before an argument list is ever built, which is what makes "no confusion possible" true for
    /// the <c>validate_suite</c> entry point. <b>There is a SECOND entry point, and it is covered by
    /// a different guard:</b> <c>Run.RunSuiteOrchestrator</c> calls
    /// <see cref="ValidationWorkerClient"/> directly for its EDGE-003 pre-flight, bypassing
    /// <c>ValidateSuiteInput</c> entirely — safe only because that orchestrator rejects any
    /// <c>path</c> beginning with <c>-</c> (a guard originally added against CLI flag injection,
    /// which subsumes this literal since <c>--yaml-stdin</c> leads with one). Both guards are
    /// load-bearing for this constant's safety; neither may be removed on the grounds that the other
    /// exists, because they cover disjoint call paths. A THIRD entry point would need its own — move
    /// the check into a shared place rather than duplicating it a second time if one appears.
    /// </para>
    /// </remarks>
    public const string InlineYamlArgument = "--yaml-stdin";

    /// <summary>
    /// The prefix of the optional level argument, written as a single <c>--level=&lt;token&gt;</c>
    /// token (see <see cref="LevelArgumentFor"/>).
    /// </summary>
    /// <remarks>
    /// One token rather than two (<c>--level full</c>), so the worker's argument handling stays a
    /// positional source plus zero-or-one flag — no index arithmetic that could mistake a level for
    /// a path, or a path beginning with a dash for a flag.
    /// </remarks>
    public const string LevelArgumentPrefix = "--level=";

    /// <summary>Builds the <c>--level=&lt;token&gt;</c> argument for <paramref name="level"/>.</summary>
    public static string LevelArgumentFor(ValidationLevel level) =>
        LevelArgumentPrefix + ValidationLevels.ToToken(level);

    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> both sides of the worker boundary use — camelCase,
    /// matching every other JSON shape this server emits (see <c>StructuredToolResult</c>).
    /// </summary>
    /// <remarks>
    /// <b>The Web defaults' <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> is
    /// load-bearing here, not cosmetic.</b> It escapes every non-ASCII character as <c>\uXXXX</c>,
    /// which is the only reason the worker's RETURN leg is immune to defect #70
    /// (https://github.com/tomas-rampas/vouchfx-mcp/issues/70 — <c>Console.OutputEncoding</c> on
    /// Windows is the OEM code page, so non-ASCII bytes written to stdout are best-fit mapped before
    /// the parent decodes them). Measured: a suite whose content is non-ASCII produces ZERO
    /// non-ASCII bytes on the worker's stdout even with the console at cp1252, so nothing is left
    /// for the code page to mangle. <b>Do not introduce
    /// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> (or any other relaxed encoder) here while
    /// #70 is open</b> — it would emit raw UTF-8 on a channel that has no encoding agreement, and
    /// the failure would be silent character corruption in a returned message, not an exception.
    /// The INBOUND leg (this server writing the suite to the worker's stdin) is covered separately
    /// and explicitly by <c>ValidationWorkerClient</c>'s <c>StandardInputEncoding</c>.
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
