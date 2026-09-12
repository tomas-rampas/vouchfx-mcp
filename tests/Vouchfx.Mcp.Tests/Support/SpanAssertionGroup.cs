namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The xUnit collection every span-asserting test class joins, so those classes run one at a time
/// relative to each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="System.Diagnostics.ActivityListener"/> is PROCESS-wide: a
/// recorder created in one test hears spans emitted by every other test's server running
/// concurrently. xUnit runs test classes in parallel by default and this assembly has no
/// parallelism configuration, so without a shared collection an "exactly one span" assertion would
/// be racing every other class in the suite and would fail intermittently — the worst kind of test,
/// since the product would be fine.
/// </para>
/// <para>
/// <b>What <c>DisableParallelization</c> actually buys here, measured rather than assumed.</b> An
/// earlier revision of this remark understated it, saying the attribute only serialises
/// span-asserting classes against EACH OTHER and that a non-span class could still run alongside.
/// That is wrong on xUnit 2.9.3: a collection marked <c>DisableParallelization = true</c> is run
/// OUTSIDE the parallel pool entirely, so while it is running no other collection in the assembly
/// runs — which is precisely the isolation a process-global <see cref="System.Diagnostics.ActivityListener"/>
/// or a <see cref="Console.SetError"/> redirection needs. The cost is real and accepted: these
/// classes cannot overlap with anything, so they are kept few and fast.
/// </para>
/// <para>
/// The per-test scoping the members here still use — <see cref="SpanRecorder.ForWorkspaceHash"/>, and
/// the <c>runId</c> filter in <c>RealStructuredLogMcpTests</c> — is therefore belt and braces rather
/// than the load-bearing defence it was first written as. It is kept for two reasons that survive the
/// correction: it makes each assertion state WHICH server or run it is about, so a failure reads as a
/// fact rather than a puzzle; and it does not depend on a test-framework behaviour that a runner
/// upgrade could quietly change.
/// </para>
/// <para>
/// Deliberately NOT a global <c>CollectionBehavior.CollectionPerAssembly</c>: serialising the whole
/// 2400-test suite to make a handful of span assertions safe would cost minutes on every run.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpanAssertionGroup
{
    public const string Name = "span-assertions";
}
