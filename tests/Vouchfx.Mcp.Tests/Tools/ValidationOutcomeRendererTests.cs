using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Tools;

/// <summary>
/// Unit coverage for the one place spec §4.4's diagnostic/error split is applied —
/// <see cref="ValidationOutcomeRenderer"/>, shared by <c>validate_suite</c> and <c>run_suite</c>.
/// </summary>
/// <remarks>
/// The wire-level consequences of this classifier are covered end-to-end in
/// <c>RealVfxCodeContractMcpTests</c>; these tests cover the cases that are awkward or impossible to
/// provoke through the real pipeline — most importantly an UNCATALOGUED code, which only a
/// misbehaving or future validation worker would ever produce and which no integration test can
/// therefore construct.
/// </remarks>
public class ValidationOutcomeRendererTests
{
    [Fact]
    public void DiagnosticsOnly_AreNotACallFailure()
    {
        var validation = new ValidateSuiteResult(
            Valid: false,
            Errors:
            [
                new SuiteValidationError("VFX-D-1101", "/steps/1", "Required properties are not present", 12, null),
                new SuiteValidationError("VFX-D-1201", null, "Unknown step type", null, null),
            ]);

        Assert.False(ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void AValidResult_IsNotACallFailure()
    {
        Assert.False(ValidationOutcomeRenderer.TryRenderCallFailure(new ValidateSuiteResult(true, []), out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void AnErrorCode_IsRenderedAsASingleVfxError()
    {
        var validation = new ValidateSuiteResult(
            Valid: false,
            Errors: [new SuiteValidationError("VFX-E-1002", null, "File not found: 'x.e2e.yaml'.", null, null)]);

        Assert.True(ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure));

        var error = ErrorObjectOf(failure!);
        Assert.Equal("VFX-E-1002", error.GetProperty("code").GetString());
        Assert.Equal("File not found: 'x.e2e.yaml'.", error.GetProperty("message").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void AnUncataloguedCode_FailsClosedAsUnrecognisedOutcome_RatherThanThrowing()
    {
        // The process-boundary case (review finding m2). A SuiteValidationError's Code arrives as
        // TEXT on the isolated validation worker's stdout, so this server cannot assume it names a
        // code the server knows: a future worker, a version skew, or a malfunctioning child could
        // all put something else there. Before this guard, classification called a THROWING lookup,
        // which made validate_suite's published "never throws" contract depend on the child
        // process's honesty.
        var validation = new ValidateSuiteResult(
            Valid: false,
            Errors: [new SuiteValidationError("VFX-E-1042", null, "something this server has never heard of", null, null)]);

        var exception = Record.Exception(
            () => ValidationOutcomeRenderer.TryRenderCallFailure(validation, out _));
        Assert.Null(exception);

        Assert.True(ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure));

        var error = ErrorObjectOf(failure!);

        // Fails CLOSED (a tool error, not silently passed through as a diagnostic a host might act
        // on) and re-labels it into the internal range rather than echoing an uninterpretable code.
        Assert.Equal("VFX-E-1902", error.GetProperty("code").GetString());

        // The worker's own message still travels — it is already sanitised by whichever guard
        // produced it, and it is the only thing here that can help a human diagnose the skew.
        Assert.Equal("something this server has never heard of", error.GetProperty("message").GetString());

        // The uncatalogued code itself is NOT echoed: it is unvalidated text from another process.
        Assert.DoesNotContain("VFX-E-1042", failure!.Content.OfType<TextContentBlock>().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedCode_IsAlsoFailedClosed_NotThrown()
    {
        // The same boundary, at its ugliest: a value that is not even code-SHAPED. VfxCode.Validate
        // would throw on this if it ever reached a VfxError constructor with it.
        var validation = new ValidateSuiteResult(
            Valid: false,
            Errors: [new SuiteValidationError("not-a-code-at-all", null, "worker said something odd", null, null)]);

        var exception = Record.Exception(
            () => ValidationOutcomeRenderer.TryRenderCallFailure(validation, out _));
        Assert.Null(exception);

        Assert.True(ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure));
        Assert.Equal("VFX-E-1902", ErrorObjectOf(failure!).GetProperty("code").GetString());
    }

    [Fact]
    public void TheFirstErrorCodeWins_EvenWhenDiagnosticsPrecedeIt()
    {
        // The real pipeline returns a call failure as the sole entry and stops, so this ordering
        // cannot arise today — asserted anyway so the classifier's behaviour is defined rather than
        // incidental if that ever changes.
        var validation = new ValidateSuiteResult(
            Valid: false,
            Errors:
            [
                new SuiteValidationError("VFX-D-1101", "/steps/1", "schema", 1, null),
                new SuiteValidationError("VFX-E-1003", null, "unreadable", null, null),
            ]);

        Assert.True(ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure));
        Assert.Equal("VFX-E-1003", ErrorObjectOf(failure!).GetProperty("code").GetString());
    }

    [Fact]
    public void TwoErrorCodesInOneResult_TheFirstIsRenderedAndTheSecondIsSilentlyDropped()
    {
        // Guards the invariant TryRenderCallFailure's own remarks name explicitly: "every
        // VFX-E-producing path (missing file, unreadable file, rejected path, worker timeout,
        // worker failure) returns its error as the sole entry and stops, because each of them means
        // validation could not proceed at all" — which is why that method uses FirstOrDefault
        // rather than collecting every error. The real pipeline (SuiteValidator /
        // ValidationWorkerClient) therefore never actually constructs a ValidateSuiteResult carrying
        // TWO VFX-E entries today; this fixture is synthetic, standing in for a future producer that
        // broke that invariant. Should one ever do so, this test pins TryRenderCallFailure's own
        // defined behaviour for that case — "first wins, every later error entry is silently
        // dropped" — by name, so a change to that behaviour (e.g. "last wins", or throwing) fails
        // HERE, at the classifier, rather than only surfacing later as a confusing downstream
        // symptom the next time some producer accidentally emits two.
        var validation = new ValidateSuiteResult(
            Valid: false,
            Errors:
            [
                new SuiteValidationError("VFX-E-1002", null, "first: file not found", null, null),
                new SuiteValidationError("VFX-E-1003", null, "second: unreadable", null, null),
            ]);

        Assert.True(ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure));

        var error = ErrorObjectOf(failure!);
        Assert.Equal("VFX-E-1002", error.GetProperty("code").GetString());
        Assert.Equal("first: file not found", error.GetProperty("message").GetString());
    }

    private static JsonElement ErrorObjectOf(CallToolResult result)
    {
        Assert.True(result.IsError);

        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var document = JsonDocument.Parse(content.Text);
        return document.RootElement.Clone();
    }
}
