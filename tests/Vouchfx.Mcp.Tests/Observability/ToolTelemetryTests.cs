using System.Diagnostics;
using System.Reflection;
using Vouchfx.Mcp.Observability;

namespace Vouchfx.Mcp.Tests.Observability;

/// <summary>
/// Mirror-namespace unit tests for US-S6-04's span-emission helper, including the attribute-allowlist
/// guard the AC asks to be held BY CONSTRUCTION.
/// </summary>
[Collection(SpanAssertionGroup.Name)]
public class ToolTelemetryTests
{
    /// <summary>The allowlist on a span with no runId — hoisted per CA1861.</summary>
    private static readonly string[] BaseAttributeKeys = ["duration_ms", "outcome", "workspace.hash"];

    /// <summary>The allowlist on a run-lifecycle span — hoisted per CA1861.</summary>
    private static readonly string[] RunLifecycleAttributeKeys = ["duration_ms", "outcome", "runId", "workspace.hash"];

    /// <summary>The only two rendered outcome values — hoisted per CA1861.</summary>
    private static readonly string[] RenderedOutcomes = ["error", "success"];

    /// <summary>
    /// The only string parameter names the helper API may expose — the three caller-supplied slots.
    /// Anything else would be a caller-chosen attribute value or key. See the allowlist guard below.
    /// </summary>
    private static readonly string[] AllowedStringParameterNames = ["toolName", "workspaceHash", "runId"];

    [Fact]
    public void SpanName_IsTheDocumentedToolNamespacedForm()
    {
        Assert.Equal("vouchfx.mcp.tool/validate_suite", ToolTelemetry.SpanName("validate_suite"));
        Assert.Equal("vouchfx.mcp.tool/run_suite", ToolTelemetry.SpanName("run_suite"));
    }

    [Fact]
    public void WithNoListener_StartProducesNoSpanAtAll()
    {
        // The additive AC's mechanism, asserted directly rather than inferred: with nothing
        // listening the BCL returns null, so there is no Activity to populate, time or drop.
        Assert.False(ToolTelemetry.HasListeners(), "A listener leaked from another test — this assertion is void.");

        using var span = ToolTelemetry.Start("validate_suite", workspaceHash: "deadbeefdeadbeef");

        Assert.False(span.IsRecording);

        // And completing an absent span is a no-op rather than a null dereference — the production
        // path on every host without a collector runs exactly this.
        span.Complete(ToolCallOutcome.Success, runId: "run-1");
    }

    [Fact]
    public void ASuccessfulSpan_CarriesExactlyTheAllowlistedAttributes()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();

        using (var span = ToolTelemetry.Start("validate_suite", workspaceHash: "0011223344556677"))
        {
            span.Complete(ToolCallOutcome.Success);
        }

        var activity = Assert.Single(recorder.ForWorkspaceHash("0011223344556677"));

        // EXACT equality on the key set — a subset check would let a fifth attribute through, which
        // is the whole thing this AC forbids.
        Assert.Equal(
            BaseAttributeKeys,
            activity.TagObjects.Select(tag => tag.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());

        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.Equal("0011223344556677", activity.GetTagItem("workspace.hash"));
        Assert.IsType<long>(activity.GetTagItem("duration_ms"));
    }

    [Fact]
    public void ARunLifecycleSpan_AddsRunIdAndNothingElse()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();

        using (var span = ToolTelemetry.Start("get_run_status", workspaceHash: "8899aabbccddeeff"))
        {
            span.Complete(ToolCallOutcome.Error, runId: "run-abc");
        }

        var activity = Assert.Single(recorder.ForWorkspaceHash("8899aabbccddeeff"));

        Assert.Equal(
            RunLifecycleAttributeKeys,
            activity.TagObjects.Select(tag => tag.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());

        Assert.Equal("run-abc", activity.GetTagItem("runId"));
        Assert.Equal("error", activity.GetTagItem("outcome"));
    }

    [Fact]
    public void WithNoWorkspaceConfigured_TheWorkspaceHashAttributeIsOmittedRatherThanBlank()
    {
        using var recorder = SpanRecorder.ForVouchfxToolSource();

        using (var span = ToolTelemetry.Start("search_docs", workspaceHash: null))
        {
            span.Complete(ToolCallOutcome.Success);
        }

        var activity = Assert.Single(recorder.VouchfxToolSpans(), a => a.DisplayName == "vouchfx.mcp.tool/search_docs");

        // Absent, not empty-string: an empty attribute reads in a backend as "this server has a
        // workspace whose hash is blank", which is a different and false claim.
        Assert.Null(activity.GetTagItem("workspace.hash"));
        Assert.DoesNotContain("workspace.hash", activity.TagObjects.Select(tag => tag.Key));
    }

    [Fact]
    public void TheOutcomeAttribute_IsOnlyEverOneOfTwoValues()
    {
        // ToolCallOutcome is an enum precisely so this is true by compilation; this pins the two
        // rendered strings, which are what a backend groups on.
        using var recorder = SpanRecorder.ForVouchfxToolSource();

        foreach (var outcome in Enum.GetValues<ToolCallOutcome>())
        {
            using var span = ToolTelemetry.Start($"tool_{outcome}", workspaceHash: null);
            span.Complete(outcome);
        }

        var rendered = recorder.VouchfxToolSpans()
            .Select(activity => activity.GetTagItem("outcome") as string)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RenderedOutcomes, rendered);
    }

    /// <summary>
    /// THE allowlist-by-construction assertion: nothing on this API accepts a caller-chosen attribute
    /// key, so a future call site cannot add a fifth attribute without editing the helper.
    /// </summary>
    /// <remarks>
    /// The AC's wording is "the span-emission helper accepts only the four named attributes as typed
    /// parameters, so a future call site cannot pass an arbitrary string key/value pair without a
    /// compile error". A test cannot observe a compile error, so it observes the property that
    /// produces one: no public member of <see cref="ToolTelemetry"/> or <see cref="ToolSpan"/> takes
    /// a free-form key, and neither exposes the underlying <see cref="Activity"/> for a caller to tag
    /// directly. If someone adds a <c>SetTag</c> passthrough or returns the Activity, this fails and
    /// names the member.
    /// </remarks>
    [Fact]
    public void NoMemberOfTheHelperApi_AcceptsACallerSuppliedKeyOrHandsOutTheActivity()
    {
        // The PROPERTY, not a blocklist of member names. An earlier revision checked that no method
        // was called "SetTag"/"AddTag", which a method called "Tag" or "WithAttribute" walks
        // straight past. What actually has to hold is that no caller can supply a key at all: every
        // string a caller passes must land in one of the three named slots, and the Activity must
        // never escape for direct tagging.
        foreach (var type in new[] { typeof(ToolTelemetry), typeof(ToolSpan) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(m => m.DeclaringType == type && !m.IsSpecialName))
            {
                foreach (var parameter in method.GetParameters().Where(p => p.ParameterType == typeof(string)))
                {
                    Assert.True(
                        AllowedStringParameterNames.Contains(parameter.Name, StringComparer.Ordinal),
                        $"{type.Name}.{method.Name} takes a string parameter '{parameter.Name}' that is not one " +
                        $"of the allowlisted slots ({string.Join(", ", AllowedStringParameterNames)}). A " +
                        "caller-supplied string that is not one of those is an arbitrary attribute value — or, " +
                        "worse, an arbitrary KEY — and the allowlist is no longer closed by construction.");
                }

                Assert.False(
                    typeof(Activity).IsAssignableFrom(method.ReturnType),
                    $"{type.Name}.{method.Name} returns the underlying Activity, so a caller can tag it " +
                    "directly and bypass the allowlist.");
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(p => p.DeclaringType == type))
            {
                Assert.False(
                    typeof(Activity).IsAssignableFrom(property.PropertyType),
                    $"{type.Name}.{property.Name} exposes the underlying Activity, so a caller can tag it " +
                    "directly and bypass the allowlist.");
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(f => f.DeclaringType == type))
            {
                // Closes the same hole one level down: a public ActivitySource field would let any
                // type in this assembly call StartActivity on it and tag the result freely. This is
                // what made ToolTelemetry.Source private.
                Assert.False(
                    typeof(Activity).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(ActivitySource),
                    $"{type.Name}.{field.Name} exposes the Activity or its source, so a caller can start " +
                    "or tag a span outside this helper.");
            }
        }
    }

    [Fact]
    public void TheHelpersAttributeKeyConstants_AreExactlyTheFourTheAcNames()
    {
        Assert.Equal("workspace.hash", ToolTelemetry.WorkspaceHashAttribute);
        Assert.Equal("runId", ToolTelemetry.RunIdAttribute);
        Assert.Equal("duration_ms", ToolTelemetry.DurationMsAttribute);
        Assert.Equal("outcome", ToolTelemetry.OutcomeAttribute);
    }

    [Fact]
    public void TheSourceName_IsStableAndMatchesWhatTestsFilterOn()
    {
        // A backend selects this server's spans by this string; changing it is a breaking change for
        // every dashboard built on it, so it is pinned rather than left to drift with a namespace.
        Assert.Equal("Vouchfx.Mcp", ToolTelemetry.SourceName);
        Assert.Equal(ToolTelemetry.SourceName, SpanRecorder.ToolActivitySourceName);
    }
}
