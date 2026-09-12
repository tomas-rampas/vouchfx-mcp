using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level guard pinning span emission and span TAGGING to ONE choke point: none of the five
/// scanned shapes — <c>new ActivitySource(…)</c>, a target-typed <c>ActivitySource X = new(…)</c>,
/// <c>.StartActivity(…)</c>, <c>Activity.Current</c>, or <c>.SetTag</c>/<c>.AddTag</c> — appears
/// anywhere in <c>src/</c> outside <c>Observability/ToolTelemetry.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant this holds.</b> US-S6-04's attribute allowlist is closed by the shape of
/// <c>ToolTelemetry</c>'s API — four typed parameters, no free-form tag setter, no way to reach the
/// underlying <c>Activity</c>. That closure is worth exactly as much as the guarantee that nobody
/// starts a span some other way: a second <c>ActivitySource</c> elsewhere in <c>src/</c> would be
/// free to tag anything at all, including a suite's contents or a <c>VfxError</c>'s message, and
/// <c>ToolTelemetryTests</c>' reflection assertions would still pass because they only inspect the
/// helper.
/// </para>
/// <para>
/// This repository holds must-not-spread rules structurally rather than by comment — the same shape
/// as <see cref="RecursiveYamlParseCallSiteSourceGuardTests"/> and
/// <see cref="SpecIndexParserSourceGuardTests"/>, with the same
/// comment-and-string-literal-stripped scan (<see cref="SourceGuardScan"/>) and the same fail-closed
/// exact-equality check, so a new emission site fails BY NAME and a stale entry cannot rot.
/// </para>
/// <para>
/// <b>A note on scope.</b> This does not forbid a future second span — it forbids a second span
/// emitted OUTSIDE the helper. Adding one properly means adding it to <c>ToolTelemetry</c>, where
/// the allowlist and this guard both still apply, which is precisely the outcome intended.
/// </para>
/// </remarks>
public class ToolTelemetrySourceGuardTests
{
    /// <summary>
    /// The only file in <c>src/</c> permitted to construct an <c>ActivitySource</c> or start an
    /// activity. One entry, and it should stay one entry.
    /// </summary>
    private static readonly string[] EmissionSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Observability/ToolTelemetry.cs",
    ];

    /// <summary>
    /// Any shape by which a span could be created or tagged outside the helper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four alternations, each closing a distinct hole, and all four are needed:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>new ActivitySource(…)</c> AND the target-typed
    /// <c>ActivitySource X = new(…)</c> form. The second is not pedantry — it is exactly how
    /// <c>ToolTelemetry</c>'s own source is declared, so without it the helper would land in the
    /// allow-list only by virtue of its <c>StartActivity</c> call, and this guard's own positive case
    /// would be proving a pattern the production code does not match.</description></item>
    /// <item><description><c>.StartActivity(…)</c> — a new source is not needed to start a span on an
    /// existing one.</description></item>
    /// <item><description><c>Activity.Current</c> — the ambient-span escape hatch. Reaching the
    /// current activity hands a caller the SDK's span (or ours) to tag at will, entirely bypassing
    /// this server's four-attribute surface.</description></item>
    /// <item><description><c>.SetTag(…)</c> / <c>.AddTag(…)</c> — the tagging call itself, so even a
    /// span obtained some way none of the above anticipated cannot be given an attribute outside the
    /// helper.</description></item>
    /// </list>
    /// </remarks>
    private static readonly Regex SpanEmission =
        new(
            @"new\s+ActivitySource\b\s*\(" +
            @"|ActivitySource\s+\w+\s*=\s*new\s*\(" +
            @"|\.\s*StartActivity\w*\s*\(" +
            @"|Activity\s*\.\s*Current" +
            @"|\.\s*(Set|Add)Tag\s*\(",
            RegexOptions.Compiled);

    [Fact]
    public void SpanEmissionInSrc_HappensOnlyInTheTelemetryHelper()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => SpanEmission.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            EmissionSiteRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void TheTelemetryHelper_StillExistsAndStillEmits()
    {
        // Anti-vacuity in both directions: a renamed file would make the set check above pass over
        // nothing, and a helper that stopped emitting would mean this guard reported success over a
        // server that had quietly lost its instrumentation.
        foreach (var relativePath in EmissionSiteRelativePaths)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            Assert.Matches(SpanEmission, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }

    [Fact]
    public void TheDecorator_ReachesSpanEmissionOnlyThroughTheHelper()
    {
        // The file most likely to grow its own span, since it is where the interception happens: the
        // decorator must keep going through ToolTelemetry.Start rather than starting one itself.
        var decorator = Path.Combine(
            SourceGuardScan.RepoRoot.FullName,
            Path.Combine("src", "Vouchfx.Mcp", "Observability", "InstrumentedMcpServerTool.cs"));

        Assert.True(File.Exists(decorator), $"Expected the decorator at '{decorator}'.");

        var source = SourceGuardScan.ExecutableSourceOf(decorator);

        Assert.DoesNotMatch(SpanEmission, source);

        // And it does still emit — through the helper. Without this the assertion above would pass
        // just as happily on a decorator that had dropped instrumentation altogether.
        Assert.Matches(new Regex(@"ToolTelemetry\s*\.\s*Start\s*\(", RegexOptions.Compiled), source);
    }

    [Fact]
    public void ThePattern_MatchesEveryEmissionShapeAndNotNeighbouringText()
    {
        Assert.Matches(SpanEmission, "public static readonly ActivitySource Source = new ActivitySource(SourceName);");
        Assert.Matches(SpanEmission, "var activity = Source.StartActivity(SpanName(toolName), ActivityKind.Internal);");
        Assert.Matches(SpanEmission, "_source.StartActivity(\"x\")");

        // The TARGET-TYPED declaration, asserted against SpanEmission itself rather than an inline
        // regex. This is the form ToolTelemetry actually uses, so testing it against a throwaway
        // pattern would have proven nothing about the guard that runs.
        Assert.Matches(SpanEmission, "public static readonly ActivitySource Source = new(SourceName, ServerIdentity.Version);");

        // The two escape hatches: reaching the ambient span, and tagging any span at all.
        Assert.Matches(SpanEmission, "Activity.Current?.SetTag(\"suite.yaml\", text);");
        Assert.Matches(SpanEmission, "var current = Activity . Current;");
        Assert.Matches(SpanEmission, "activity.SetTag(\"anything\", value);");
        Assert.Matches(SpanEmission, "activity.AddTag(\"anything\", value);");

        // Unrelated text must not trip it.
        Assert.DoesNotMatch(SpanEmission, "// starts an activity for each tool call");
        Assert.DoesNotMatch(SpanEmission, "var runner = new ActivityRunner();");
        Assert.DoesNotMatch(SpanEmission, "var tag = metadata.CurrentTag;");
    }
}
