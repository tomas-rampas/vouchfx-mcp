using System.Collections.Concurrent;
using System.Diagnostics;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Captures <see cref="Activity"/> instances for the lifetime of the instance via a plain
/// <see cref="ActivityListener"/> — the identical seam an OpenTelemetry exporter subscribes to,
/// with no OpenTelemetry package on either side of the build (US-S6-04, amended 2026-09-12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a listener and not an in-memory OTel exporter.</b> An exporter is a consumer of exactly
/// this API: the OTel SDK registers an <see cref="ActivityListener"/> internally and forwards what it
/// hears. Asserting at the listener therefore tests the same thing one layer closer to the
/// instrumentation, and keeps the dependency count at zero. It also makes the no-collector scenario
/// honest rather than simulated: with no instance of this type alive, <c>StartActivity</c> genuinely
/// returns <see langword="null"/>, which is the real production shape.
/// </para>
/// <para>
/// <b>Listeners are PROCESS-wide, and this test assembly runs classes in parallel.</b> That is a real
/// cross-talk hazard, not a theoretical one: a recorder created by one test hears spans produced by
/// every other test's harness concurrently. Two mechanisms handle it, and span-asserting tests should
/// use both — see <see cref="SpanAssertionGroup"/> for the serialising collection, and prefer
/// <see cref="ForWorkspaceHash"/> over <see cref="All"/> so the assertion is scoped to the one server
/// instance the test started.
/// </para>
/// </remarks>
internal sealed class SpanRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _stopped = new();

    private SpanRecorder(Func<ActivitySource, bool> shouldListenTo)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = shouldListenTo,

            // Sample everything. AllDataAndRecorded is what makes tags actually materialise on the
            // Activity — a lower sampling result yields an Activity whose tags are dropped, which
            // would read here as "the helper set no attributes" rather than "the listener asked for
            // none".
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,

            // Recorded on STOP, never on start: duration_ms and outcome are only known once the tool
            // call has completed, so a start-time snapshot would assert against a half-built span.
            ActivityStopped = activity => _stopped.Enqueue(activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Records spans from every source in the process, including the MCP SDK's own.</summary>
    /// <remarks>
    /// Used by the canary test, deliberately: a leak through the SDK's attributes would be just as
    /// real as one through ours, so hygiene is asserted over EVERY captured span rather than only
    /// this server's. Also used to measure which sources exist at all.
    /// </remarks>
    public static SpanRecorder ForAllSources() => new(_ => true);

    /// <summary>Records spans from this server's own <see cref="ActivitySource"/> only.</summary>
    public static SpanRecorder ForVouchfxToolSource() =>
        new(source => source.Name == ToolActivitySourceName);

    /// <summary>
    /// The name of this server's tool <see cref="ActivitySource"/>, restated here rather than
    /// referenced from production so a rename has to be made deliberately in both places.
    /// </summary>
    public const string ToolActivitySourceName = "Vouchfx.Mcp";

    /// <summary>Every span captured so far, in completion order.</summary>
    public IReadOnlyList<Activity> All => _stopped.ToArray();

    /// <summary>
    /// Spans from this server's tool source carrying the given <c>workspace.hash</c> — the scoping a
    /// parallel test assembly requires. The hash is unique per workspace root, so giving each span
    /// test its own temp workspace makes its assertions immune to concurrent servers.
    /// </summary>
    public IReadOnlyList<Activity> ForWorkspaceHash(string workspaceHash) =>
        All.Where(activity =>
                activity.Source.Name == ToolActivitySourceName &&
                activity.GetTagItem("workspace.hash") as string == workspaceHash)
            .ToArray();

    /// <summary>Spans from this server's tool source, whatever workspace they carry.</summary>
    public IReadOnlyList<Activity> VouchfxToolSpans() =>
        All.Where(activity => activity.Source.Name == ToolActivitySourceName).ToArray();

    /// <summary>Every distinct source name heard — the measurement behind the SDK-source finding.</summary>
    public IReadOnlyList<string> DistinctSourceNames() =>
        All.Select(activity => activity.Source.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every tag value on every captured span, rendered as text, for hygiene assertions.</summary>
    public IReadOnlyList<(string SpanName, string Key, string Value)> AllTags() =>
        All.SelectMany(activity => activity.TagObjects.Select(tag =>
                (activity.DisplayName, tag.Key, tag.Value?.ToString() ?? string.Empty)))
            .ToArray();

    public void Dispose() => _listener.Dispose();
}
