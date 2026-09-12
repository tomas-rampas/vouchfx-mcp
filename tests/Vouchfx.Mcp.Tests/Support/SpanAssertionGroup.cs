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
/// This is the narrower of the two defences and is deliberately not the only one. Joining a
/// collection serialises span-asserting classes against EACH OTHER; it does nothing about a
/// non-span test class running concurrently and driving the same tools. That is why
/// <see cref="SpanRecorder.ForWorkspaceHash"/> exists and why span tests give each server its own
/// temporary workspace: the <c>workspace.hash</c> attribute then scopes the assertion to the one
/// server the test started, which no concurrent class can forge. Belt and braces, because a flaky
/// observability test would be abandoned rather than fixed.
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
