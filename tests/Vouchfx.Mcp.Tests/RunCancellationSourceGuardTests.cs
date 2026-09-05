using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The third source-level guard in this repo, and the one that keeps
/// <c>InProcessRunCancellations</c>' central safety argument true: <b>nothing in <c>src/</c>
/// registers a callback on a cancellation token</b>. Plus a second, unrelated structural invariant
/// over the same scan — <c>RunRegistryStatus.Timeout</c> is declared and deliberately never written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the callback rule needs enforcing.</b> <c>InProcessRunCancellations.Scope.TryRequest</c>
/// calls <see cref="CancellationTokenSource.Cancel()"/> <i>while holding the entry's gate</i>, and
/// that placement is load-bearing: releasing the lock first would reopen the use-after-dispose window
/// between signalling the source and the holder disposing it. <see cref="CancellationTokenSource.Cancel()"/>
/// invokes the token's registered callbacks SYNCHRONOUSLY on the calling thread, so the safety of
/// that design rests entirely on there being no user callbacks to run. Today there are none — the
/// only registrations on that token are the framework's own linked-source propagation — and this
/// test is what makes that a checked fact rather than an observation someone once made. Were a
/// callback added, two things become reachable: one that blocks stalls the holder's own
/// <c>Dispose</c>, and one that throws surfaces as an <see cref="AggregateException"/> escaping
/// <c>cancel_run</c> uncoded, with no <c>VFX-</c> code (a security review's INFO findings, promoted
/// to a guard here).
/// </para>
/// <para>
/// <b>Shape, not identity: the pattern matches INVOCATIONS on a receiver, never declarations.</b>
/// <c>IRunCancellationRegistry.Register</c> is a method NAME of this server's own, called as
/// <c>_cancellations.Register(...)</c> — which is the same shape as a token registration. It cannot
/// be told apart syntactically, so it is not: the allow-list below names its ONE call site with its
/// expected count, exactly as <see cref="RunLockSourceGuardTests"/> does for <c>TryAcquire</c>. A
/// token registration added anywhere else fails by file name; a second one added inside the allowed
/// file fails on the count.
/// </para>
/// <para>
/// Mirrors <see cref="RunLockSourceGuardTests"/>'s construction throughout — a whitespace-tolerant
/// regex over source with comments and string literals stripped
/// (<see cref="SourceGuardScan.ExecutableSourceOf"/>), and a fail-closed EXACT-equality check against
/// a named set, so a new site fails by name and a stale entry cannot rot.
/// </para>
/// </remarks>
public class RunCancellationSourceGuardTests
{
    /// <summary>
    /// Every file in <c>src/</c> allowed to contain a <c>.Register(</c>/<c>.UnsafeRegister(</c>
    /// invocation, and how many.
    /// </summary>
    /// <remarks>
    /// Exactly one entry, and it is NOT a token registration: it is
    /// <c>RunSuiteOrchestrator</c> publishing the run into <c>IRunCancellationRegistry</c>. It is
    /// listed because the two shapes are indistinguishable to a regex, not because a token callback
    /// is tolerated there — see this class's remarks.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, int> AllowedRegisterInvocations =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs"] = 1,
        };

    /// <summary>
    /// An INVOCATION of <c>Register</c>/<c>UnsafeRegister</c>: a receiver, a dot, the name, an open
    /// paren. A declaration (<c>IRunCancellationScope Register(...)</c>) carries no leading dot and is
    /// deliberately not matched — this guard is about who CALLS, not who defines.
    /// </summary>
    private static readonly Regex RegisterInvocation =
        new(@"\.\s*(Unsafe)?Register\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// A reference to <c>RunRegistryStatus.Timeout</c> — the status this server declares in spec
    /// §5.8's union and deliberately never writes.
    /// </summary>
    private static readonly Regex TimeoutStatusReference =
        new(@"RunRegistryStatus\s*\.\s*Timeout\b", RegexOptions.Compiled);

    /// <summary>Where <c>RunRegistryStatus.Timeout</c> is declared — the anti-vacuity anchor below.</summary>
    private const string TimeoutStatusDeclarationFile = "src/Vouchfx.Mcp/Run/RunRegistryEntry.cs";

    /// <summary>Its declaration, which must still exist for the scan above to be about anything.</summary>
    private static readonly Regex TimeoutStatusDeclaration =
        new(@"const\s+string\s+Timeout\s*=", RegexOptions.Compiled);

    [Fact]
    public void NoCancellationTokenCallback_IsRegisteredAnywhereInSrc()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => RegisterInvocation.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            AllowedRegisterInvocations.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void EachAllowedRegisterSite_CallsItExactlyTheExpectedNumberOfTimes()
    {
        foreach (var (relativePath, expectedCount) in AllowedRegisterInvocations)
        {
            var fullPath = FullPathOf(relativePath);

            // Anti-vacuity: a renamed or deleted file would otherwise make this check pass over nothing.
            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            var matches = RegisterInvocation.Matches(SourceGuardScan.ExecutableSourceOf(fullPath)).Count;

            Assert.True(
                matches == expectedCount,
                $"'{relativePath}' contains {matches} Register/UnsafeRegister invocation(s); exactly "
                + $"{expectedCount} is expected, and that one is the cancel_run bridge publishing a "
                + "run — NOT a cancellation-token callback. If a token callback was added, read "
                + "InProcessRunCancellations' remarks first: Cancel() runs callbacks synchronously "
                + "under the entry's gate, so a blocking one stalls the holder's disposal and a "
                + "throwing one escapes cancel_run as an uncoded AggregateException.");
        }
    }

    [Fact]
    public void TheRegisterPattern_MatchesCallsAndNotDeclarations()
    {
        // Sanity check for the regex above, because the whole guard is only as good as it is.
        Assert.Matches(RegisterInvocation, "using var r = token.Register(() => tcs.TrySetResult());");
        Assert.Matches(RegisterInvocation, "cancellationToken\n    .UnsafeRegister(static _ => { }, null);");
        Assert.DoesNotMatch(RegisterInvocation, "IRunCancellationScope Register(string runId, CancellationTokenSource s);");
        Assert.DoesNotMatch(RegisterInvocation, "public IRunCancellationScope Register(string runId)");
    }

    /// <summary>
    /// <c>RunRegistryStatus.Timeout</c> is written NOWHERE — a run that exhausts its
    /// <c>timeoutSeconds</c> budget is recorded as <c>completed</c> with an <c>Inconclusive</c>
    /// outcome, exactly as it was before US-S3-03.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Structural rather than behavioural (a gatekeeper review's MINOR finding), because the
    /// behavioural version cannot be written honestly: proving "no code path writes this" by running
    /// code means enumerating every path, and the value of the claim is precisely that it holds for
    /// paths nobody has thought of. A future story that DOES split the timeout case out will fail here
    /// and be sent to <c>RunRegistryStatus.Timeout</c>'s own remarks, which explain what changing it
    /// costs: every existing result asserts <c>completed</c>+<c>Inconclusive</c> for that case.
    /// </para>
    /// <para>
    /// <b>The expected set is EMPTY, including the declaration's own file.</b> Inside
    /// <c>RunRegistryStatus</c> the member is named unqualified (in <c>All</c> and in
    /// <c>IsTerminal</c>), so the qualified form this scans for appears in no file at all — which is
    /// the strongest available statement of the invariant, and is why the anti-vacuity check below is
    /// a separate assertion on the declaration rather than a member of the set.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTimeoutRegistryStatus_IsNeverReferencedFromOutsideItsOwnDeclaration_AndSoIsNeverWritten()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => TimeoutStatusReference.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(actualSites);

        // Anti-vacuity: the status must still be DECLARED, or this scan is asserting nothing. A
        // deletion is as much a contract change as a write would be — spec §5.8's union names it.
        var declarationPath = FullPathOf(TimeoutStatusDeclarationFile);
        Assert.True(
            File.Exists(declarationPath),
            $"Expected a tracked file at '{declarationPath}' — update this guard if it moved.");
        Assert.Matches(TimeoutStatusDeclaration, SourceGuardScan.ExecutableSourceOf(declarationPath));
    }

    private static string FullPathOf(string repoRelativePath) =>
        Path.Combine(
            SourceGuardScan.RepoRoot.FullName, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
}
