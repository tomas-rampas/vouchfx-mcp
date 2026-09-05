using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Tools;

/// <summary>
/// US-S2-02: <c>validate_suite</c> v2's input-resolution branch — exactly one of <c>path</c>/
/// <c>yaml</c>, plus the <c>level</c> selector — checked directly, before anything is spawned.
/// </summary>
/// <remarks>
/// Split out of the tool handler into <see cref="ValidateSuiteInput"/> for exactly this reason: the
/// both/neither rule is a pure decision over three nullable strings, and proving it through an MCP
/// round trip (which additionally spawns a worker process) would be slower and would prove less.
/// The MCP-level counterpart — that this VfxError actually reaches the wire as <c>isError</c> — is
/// in <c>RealToolsMcpTests</c>.
/// </remarks>
public class ValidateSuiteInputTests
{
    [Fact]
    public void TryResolve_PathOnly_ResolvesToAFileSourceAtTheDefaultLevel()
    {
        Assert.True(ValidateSuiteInput.TryResolve("suite.e2e.yaml", yaml: null, level: null, out var resolved, out var error));

        Assert.Null(error);
        Assert.False(resolved.Source.IsInline);
        Assert.Equal("suite.e2e.yaml", resolved.Source.Path);
        Assert.Equal(ValidationLevel.Full, resolved.Level);
    }

    [Fact]
    public void TryResolve_YamlOnly_ResolvesToAnInlineSource()
    {
        Assert.True(ValidateSuiteInput.TryResolve(path: null, "steps: []", level: null, out var resolved, out var error));

        Assert.Null(error);
        Assert.True(resolved.Source.IsInline);
        Assert.Equal("steps: []", resolved.Source.InlineYaml);
    }

    [Fact]
    public void TryResolve_BothPathAndYaml_FailsWithASchemaValidationRangeError()
    {
        Assert.False(ValidateSuiteInput.TryResolve("suite.e2e.yaml", "steps: []", level: null, out _, out var error));

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1152", error!.Code);
        AssertInSchemaValidationRange(error.Code);
    }

    [Fact]
    public void TryResolve_NeitherPathNorYaml_FailsWithTheSameSchemaValidationRangeError()
    {
        Assert.False(ValidateSuiteInput.TryResolve(path: null, yaml: null, level: null, out _, out var error));

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1152", error!.Code);
        AssertInSchemaValidationRange(error.Code);
    }

    [Fact]
    public void TryResolve_BothAndNeither_AreTheSameCodeButDistinguishableMessages()
    {
        ValidateSuiteInput.TryResolve("suite.e2e.yaml", "steps: []", level: null, out _, out var both);
        ValidateSuiteInput.TryResolve(path: null, yaml: null, level: null, out _, out var neither);

        Assert.NotEqual(both!.Message, neither!.Message);
    }

    [Fact]
    public void TryResolve_PathEqualToTheWorkerStdinMarker_IsRefusedRatherThanSilentlyReinterpreted()
    {
        // The worker's <source> argument is an in-band discriminator: "--yaml-stdin" in the path
        // position means "the text is on stdin". Without this refusal, a file with that exact name
        // would never be opened — the worker would read an empty stdin and answer VFX-D-1102 ("the
        // document is empty") about a file it never looked at, which is a wrong answer delivered as
        // a confident one. Refused here because this is the last layer that still knows the caller
        // meant a path.
        Assert.False(ValidateSuiteInput.TryResolve(
            ValidationWorkerProtocol.InlineYamlArgument, yaml: null, level: null, out _, out var error));

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1152", error!.Code);
        AssertInSchemaValidationRange(error.Code);

        // The message names the collision, so the caller can tell this apart from the both/neither
        // shapes that share the code.
        Assert.Contains(ValidationWorkerProtocol.InlineYamlArgument, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("./--yaml-stdin")]
    [InlineData("suites/--yaml-stdin")]
    [InlineData("--yaml-stdin.e2e.yaml")]
    [InlineData("--yaml-stdinx")]
    public void TryResolve_PathMerelyCONTAININGTheMarker_IsStillAValidPath(string path)
    {
        // The refusal is an exact, ordinal equality — not a substring or prefix test. Any qualified
        // form reaches the worker unambiguously in the path position, which is also the workaround
        // the refusal's own message offers.
        Assert.True(ValidateSuiteInput.TryResolve(path, yaml: null, level: null, out var resolved, out var error));

        Assert.Null(error);
        Assert.Equal(path, resolved.Source.Path);
    }

    [Theory]
    [InlineData("schema", ValidationLevel.Schema)]
    [InlineData("semantic", ValidationLevel.Semantic)]
    [InlineData("full", ValidationLevel.Full)]
    public void TryResolve_EveryAdvertisedLevelToken_Resolves(string token, ValidationLevel expected)
    {
        Assert.True(ValidateSuiteInput.TryResolve("suite.e2e.yaml", yaml: null, token, out var resolved, out var error));

        Assert.Null(error);
        Assert.Equal(expected, resolved.Level);
    }

    [Theory]
    [InlineData("Schema")]
    [InlineData("SEMANTIC")]
    [InlineData("deep")]
    [InlineData("")]
    public void TryResolve_UnrecognisedLevel_FailsWithInvalidToolArgument(string token)
    {
        // Case-sensitive, matching get_schema's `section`/`format` convention: one canonical
        // spelling per DSL/tool vocabulary term across this server.
        Assert.False(ValidateSuiteInput.TryResolve("suite.e2e.yaml", yaml: null, token, out _, out var error));

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1006", error!.Code);
    }

    [Fact]
    public void TryResolve_ChecksTheSourceBeforeTheLevel()
    {
        // A call that supplies neither source AND a bad level is a source problem first: the
        // caller has to name something to validate before the level means anything.
        Assert.False(ValidateSuiteInput.TryResolve(path: null, yaml: null, "nonsense", out _, out var error));

        Assert.Equal("VFX-E-1152", error!.Code);
    }

    /// <summary>
    /// The acceptance criterion is a RANGE, not just a code: 1100-1199 is the schema-validation
    /// area (see <c>VfxCodeCatalogue</c>'s range headers), so a future renumbering that left the
    /// range would fail here even if the constant were updated in lockstep.
    /// </summary>
    private static void AssertInSchemaValidationRange(string code)
    {
        var number = int.Parse(code.AsSpan("VFX-E-".Length), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(number, 1100, 1199);
    }
}
