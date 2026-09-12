using System.Security.Cryptography;
using System.Text;

namespace Vouchfx.Mcp.Observability;

/// <summary>
/// Derives the <c>workspace.hash</c> span attribute from a resolved <see cref="Workspace"/> — the
/// only form in which this server will name a filesystem location on a span (US-S6-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> Tool spans need SOME stable way to distinguish one workspace's traffic
/// from another's in a shared tracing backend — otherwise latency and failure rates from every
/// project a developer works on pile into one undifferentiated series. The raw path would do that
/// job and is exactly what must not be sent: a shared backend is read by people who have no business
/// knowing a developer's directory layout, client names frequently appear in paths, and the path is
/// the one attribute with no upper bound on what it might disclose.
/// </para>
/// <para>
/// <b>Be precise about the strength claim, because "hashed" is routinely over-read as "safe".</b>
/// This is a one-way function in the practical sense that matters here — a person reading a trace
/// cannot recover the path from the attribute, and no amount of staring at
/// <c>workspace.hash=3f2a…</c> reveals a directory name. It is NOT secret-strength: a filesystem path
/// is low-entropy and highly guessable, so someone holding a list of candidate paths can hash each
/// one and confirm a match. That is an unavoidable property of hashing a low-entropy input, not a
/// defect in the choice of algorithm.
/// </para>
/// <para>
/// <b>A salt does not fix that, and it is worth being exact about why, because "just add a salt" is
/// the obvious suggestion.</b> A salt has to be either constant or per-process, and neither helps
/// here. A BUILD-CONSTANT salt is shipped inside an open-source binary — it is public, so an attacker
/// computing candidate hashes simply includes it and is no worse off than before; it buys the
/// appearance of protection and nothing else. A PER-PROCESS random salt would genuinely defeat the
/// guessing attack, and in doing so would destroy the entire point of the attribute: the same
/// workspace would hash differently after every restart, so nothing would correlate across runs,
/// which is the one thing this value exists to do. The honest summary: this removes CASUAL disclosure
/// of a filesystem layout to trace readers, which is what the AC asks for, and is not a defence
/// against an adversary who already knows which paths to test.
/// </para>
/// <para>
/// <b>Why SHA-256 truncated to 16 hex characters.</b> SHA-256 because it is the same primitive this
/// repository already uses for vendored-artefact digests, so there is one hash family here rather
/// than two. Truncated because the full 64 characters buy nothing: the attribute is a correlation
/// key, not an integrity digest, and 64 bits of it is far past the point where two workspaces on one
/// developer's machine collide. Truncation does not weaken the reversibility story above in either
/// direction — a guessing attack was already viable at full length.
/// </para>
/// <para>
/// The input is <see cref="Workspace.Root"/> as already resolved at startup — an absolute,
/// fully-qualified path — so the same workspace hashes identically across restarts, and two servers
/// launched against the same root correlate. Case is preserved rather than normalised: on the one
/// platform where paths are case-insensitive this can in principle yield two hashes for one
/// directory, which costs a split series in a dashboard and nothing else, whereas lower-casing would
/// silently merge two genuinely distinct roots on Linux.
/// </para>
/// </remarks>
internal static class WorkspaceHash
{
    /// <summary>The number of leading hex characters of the digest retained. See the type remarks.</summary>
    private const int HexLength = 16;

    /// <summary>
    /// The <c>workspace.hash</c> value for <paramref name="workspace"/>, or <see langword="null"/>
    /// when the host configured no workspace.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is a real answer, not a missing one: with no <c>--workspace</c> flag
    /// there IS no resolved workspace root, and inventing one (the process directory, say) would put
    /// a path-derived value on the span that corresponds to nothing the host chose. The attribute is
    /// then simply absent, exactly as <c>runId</c> is absent on a tool that has none.
    /// </remarks>
    public static string? Of(Workspace? workspace) =>
        workspace is null ? null : OfRoot(workspace.Root);

    /// <summary>The hash of an already-resolved absolute workspace root.</summary>
    public static string OfRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(root));

        // HexLength/2 bytes rendered lower-case — Convert.ToHexString yields upper case, and a
        // lower-case attribute reads better beside the lower-case digests this repo already prints.
        return Convert.ToHexString(digest.AsSpan(0, HexLength / 2)).ToLowerInvariant();
    }
}
