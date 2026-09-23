using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// Covers <see cref="EngineDiagnosticExcerpt"/> — vouchfx-mcp#96's signature enumeration and its
/// sanitise-and-cap rule — as the pure functions they are, so the real-child runner test and the
/// orchestrator test can each assert the ONE thing they are actually about (that the excerpt is
/// captured from the real stream, and that it reaches the wire) rather than re-proving the
/// hygiene rules through a process boundary.
/// </summary>
public class EngineDiagnosticExcerptTests
{
    /// <summary>
    /// The engine's refusal line as the pinned CLI (v1.0.0-rc.5) genuinely emits it for a suite
    /// declaring <c>environment.dependencies.search.env.ES_JAVA_OPTS</c> — <b>the TRUE text, carrying
    /// three U+2014 EM DASHES</b>, not the form a Windows OEM console shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This constant was WRONG until CI measured it, and how it was wrong is worth keeping.</b> It
    /// was first transcribed from a live run on the maintainer's Windows host, whose console output
    /// code page is 852: the engine BEST-FITS U+2014 to <c>0x2D</c> (an ASCII hyphen) at the encode,
    /// before this server ever sees a byte, so both local measurements — the original one and a
    /// reviewer's independent "byte-identical, 637 characters" check — observed hyphens and agreed
    /// with each other while both being the TRANSCODED form. <b>The 637 was never the tell</b>: the
    /// best-fit is one character for one character, so the true line is 637 too — what differs is
    /// three CODE POINTS, which no length check could ever have caught and no cp852 host could ever
    /// have shown. Both counts are now pinned in
    /// <see cref="SanitiseAndCap_TheMeasuredRc5RefusalLine_SurvivesWhole_WithItsEmDashesEscaped"/>
    /// (637 raw, 652 rendered), including a correction of a "639 true vs 637 transcoded" figure that
    /// circulated in review and is wrong. The first CI run on
    /// <c>ubuntu-latest</c> (UTF-8) received the real em dashes, <see cref="TextSanitiser"/> escaped
    /// each to a literal \u2014 escape, and the byte-equality tripwire in
    /// <see cref="RealEnvRefusalAgainstPinnedCliTests"/> failed — which is that tripwire doing exactly
    /// its job on its first run. Same mechanism as issue #89; issue #115 (suite-runner decoder parity)
    /// is the systemic fix.
    /// </para>
    /// <para>
    /// <b>CROSS-CHECKED against the rc.5 SOURCE, which is the authority here rather than any single
    /// host's capture</b> (engine repo at tag <c>v1.0.0-rc.5</c>): <c>ScenarioRunner.cs:1703</c>
    /// contributes the first em dash (<c>$"RunSuiteAsync: environment configuration error — {aex.Message}"</c>)
    /// and <c>EnvironmentMapper.cs:1167-1177</c>'s <c>ArgumentException</c> message contributes the
    /// other two ("shares \u2014 and on 'minio'", "consuming it \u2014 so honouring"). <b>Three, not two</b> —
    /// counted in the source, because the CI failure message only revealed the first and a reworded
    /// guess at the rest would have re-broken the tripwire on the next CI run.
    /// </para>
    /// <para>
    /// Written with <c>\u2014</c> escapes rather than literal glyphs ON PURPOSE: the exact code point
    /// is the load-bearing content of this constant, and an escape cannot be silently mangled by an
    /// editor, a re-encode, or a patch tool the way a literal em dash can.
    /// </para>
    /// <para>
    /// <b>Never compare a captured line to this constant directly</b> — see
    /// <see cref="RealEnvRefusalAgainstPinnedCliTests"/>, which projects it through
    /// <c>EngineOutputEncoding.Current</c> first. On a cp852 host the true text is unreachable; what
    /// arrives is the hyphen form, and a test demanding this exact string would fail on every Windows
    /// machine.
    /// </para>
    /// </remarks>
    internal const string MeasuredRc5RefusalLine =
        "RunSuiteAsync: environment configuration error \u2014 Dependency 'search' (type 'elasticsearch') "
        + "declares env entry 'ES_JAVA_OPTS', which the engine sets itself for this dependency type. "
        + "That entry is REFUSED: the engine relies on its engine-set variables to bring this "
        + "dependency up in the shape every scenario shares \u2014 and on 'minio' they are the credentials "
        + "${conn:<dependency>} advertises to every other scenario consuming it \u2014 so honouring an "
        + "override would break other scenarios rather than only this one. Remove the entry, or "
        + "declare the backend as a service with 'image:' if you need full control of its environment. "
        + "(Parameter 'env')";

    /// <summary>
    /// The same refusal as printed by the rc.6 pin (<c>v1.0.0-rc.6</c>,
    /// <c>93287ffbb0623ba253816ed4d909f50e1b26da93</c>), measured 2026-09-23 on a UTF-8 Linux host
    /// with the pinned CLI installed: <see cref="MeasuredRc5RefusalLine"/> with each of its three em
    /// dashes printed as an ASCII hyphen, and nothing else changed. 637 characters, all of them ASCII.
    /// </summary>
    /// <remarks>
    /// Engine PR #474 ("ASCII-only runtime diagnostics", in rc.6) made the engine's runtime
    /// diagnostics ASCII at the source, so the cp852 best-fit the rc.5 constant's remarks describe no
    /// longer changes this line on any host. <see cref="RealEnvRefusalAgainstPinnedCliTests"/> compares
    /// the live line to this constant; the rc.5 one stays as the em-dash input the sanitiser tests in
    /// this class need.
    /// </remarks>
    internal const string MeasuredRc6RefusalLine =
        "RunSuiteAsync: environment configuration error - Dependency 'search' (type 'elasticsearch') "
        + "declares env entry 'ES_JAVA_OPTS', which the engine sets itself for this dependency type. "
        + "That entry is REFUSED: the engine relies on its engine-set variables to bring this "
        + "dependency up in the shape every scenario shares - and on 'minio' they are the credentials "
        + "${conn:<dependency>} advertises to every other scenario consuming it - so honouring an "
        + "override would break other scenarios rather than only this one. Remove the entry, or "
        + "declare the backend as a service with 'image:' if you need full control of its environment. "
        + "(Parameter 'env')";

    /// <summary>
    /// The events file the rc.6 pin wrote for the same refused suite, verbatim from the same
    /// measurement: one scenario, recorded <c>INCONCLUSIVE</c>, no step, and
    /// <see cref="MeasuredRc6RefusalLine"/> as the <c>scenario-completed</c> event's <c>message</c>
    /// (JSON-escaped as the engine wrote it). rc.5 wrote no events file for this suite at all.
    /// </summary>
    /// <remarks>
    /// <see cref="RealEnvRefusalAgainstPinnedCliTests"/> checks the live file's shape and message;
    /// <c>MeasuredRc6RefusalEvents_MessageIsTheRefusalLine</c> keeps this copy consistent with
    /// <see cref="MeasuredRc6RefusalLine"/>.
    /// </remarks>
    internal const string MeasuredRc6RefusalEvents = """
        {"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-09-23T09:41:38.0329697+00:00","runId":"35ec14a973e5422ba5bfb1fe6a046553","scenarioId":"vouchfx-mcp#96 probe: engine-set dependency env refusal"}
        {"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-09-23T09:41:38.0329697+00:00","runId":"35ec14a973e5422ba5bfb1fe6a046553","scenarioId":"vouchfx-mcp#96 probe: engine-set dependency env refusal","verdict":"INCONCLUSIVE","counts":{"pass":0,"fail":0,"envError":0,"inconclusive":1},"message":"RunSuiteAsync: environment configuration error - Dependency \u0027search\u0027 (type \u0027elasticsearch\u0027) declares env entry \u0027ES_JAVA_OPTS\u0027, which the engine sets itself for this dependency type. That entry is REFUSED: the engine relies on its engine-set variables to bring this dependency up in the shape every scenario shares - and on \u0027minio\u0027 they are the credentials ${conn:\u003Cdependency\u003E} advertises to every other scenario consuming it - so honouring an override would break other scenarios rather than only this one. Remove the entry, or declare the backend as a service with \u0027image:\u0027 if you need full control of its environment. (Parameter \u0027env\u0027)"}
        """;

    /// <summary>
    /// <see cref="MeasuredRc5RefusalLine"/> as this server STORES and RELAYS it on a UTF-8 host: each
    /// em dash escaped to a literal <c>\u2014</c> by <see cref="TextSanitiser"/>. This — never the raw
    /// constant — is what an assertion about a hint's CONTENT should use, because every path from the
    /// runner to the wire renders the line through
    /// <see cref="EngineDiagnosticExcerpt.SanitiseAndCap"/>.
    /// </summary>
    internal static readonly string MeasuredRc5RefusalLineAsRendered =
        EngineDiagnosticExcerpt.SanitiseAndCap(MeasuredRc5RefusalLine);

    /// <summary>
    /// A pure-ASCII stand-in carrying the signature, for tests whose subject is the CAPTURE MECHANISM
    /// rather than the engine's exact wording.
    /// </summary>
    /// <remarks>
    /// Used by the tests that drive a REAL child process
    /// (<c>VouchfxCliSuiteRunnerTests</c>' <c>emit</c> fixture), where the line makes a round trip
    /// through argv and the child's own console encoding before the parent decodes it — a
    /// transcoding those tests are not about and which would make them assert different text on
    /// Windows than on Linux. The engine's true wording has exactly one oracle, and it is the one
    /// that talks to the real engine: <see cref="RealEnvRefusalAgainstPinnedCliTests"/>.
    /// </remarks>
    internal const string AsciiRefusalSample =
        "RunSuiteAsync: environment configuration error - Dependency 'search' (type 'elasticsearch') "
        + "declares env entry 'ES_JAVA_OPTS', which the engine sets itself for this dependency type.";

    // ── The signature: matches the measured line, and nothing an ordinary run prints ──────────────

    [Fact]
    public void IsDiagnosticLine_TheMeasuredRc5RefusalLine_Matches()
    {
        Assert.True(EngineDiagnosticExcerpt.IsDiagnosticLine(MeasuredRc5RefusalLine));
    }

    [Fact]
    public void IsDiagnosticLine_TheMeasuredRc6RefusalLine_Matches()
    {
        Assert.True(EngineDiagnosticExcerpt.IsDiagnosticLine(MeasuredRc6RefusalLine));
    }

    [Fact]
    public void MeasuredRc6RefusalEvents_MessageIsTheRefusalLine()
    {
        // The two measured constants came from one run; this keeps them from drifting apart, so a test
        // that feeds the events file in and another that feeds the line in describe the same refusal.
        var lines = MeasuredRc6RefusalEvents.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);

        using var started = JsonDocument.Parse(lines[0]);
        Assert.Equal("scenario-started", started.RootElement.GetProperty("type").GetString());

        using var completed = JsonDocument.Parse(lines[1]);
        Assert.Equal("scenario-completed", completed.RootElement.GetProperty("type").GetString());
        Assert.Equal("INCONCLUSIVE", completed.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(MeasuredRc6RefusalLine, completed.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("ENVIRONMENT CONFIGURATION ERROR - shouty")]
    [InlineData("Environment Configuration Error - title case")]
    [InlineData("some-future-prefix: environment configuration error - reworded prefix")]
    public void IsDiagnosticLine_CaseInsensitiveSubstring_StillMatches(string line)
    {
        // Substring and case-insensitive on purpose: a pin that changes only what precedes the phrase
        // (the rc.5 spelling is "RunSuiteAsync: …") must not silence the capture.
        Assert.True(EngineDiagnosticExcerpt.IsDiagnosticLine(line));
    }

    [Theory]
    [InlineData("Starting DCP...")]
    [InlineData("Waiting for container 'orders-db' to become healthy...")]
    [InlineData("environment error")]
    [InlineData("configuration error")]
    [InlineData("")]
    public void IsDiagnosticLine_OrdinaryEngineChatter_DoesNotMatch(string line)
    {
        // The enumeration is narrow BY DESIGN — stdout is untrusted engine output, and everything
        // that does not match is discarded rather than retained (see the type's remarks).
        Assert.False(EngineDiagnosticExcerpt.IsDiagnosticLine(line));
    }

    // ── SanitiseAndCap: hygiene, the bound, and idempotence ──────────────────────────────────────

    [Fact]
    public void SanitiseAndCap_TheMeasuredRc5RefusalLine_SurvivesWhole_WithItsEmDashesEscaped()
    {
        // REWRITTEN after CI measured the constant (see MeasuredRc5RefusalLine's remarks). This test
        // used to assert SanitiseAndCap(line) == line, which was only ever true because the constant
        // held the cp852-TRANSCODED form — all-ASCII by accident of the maintainer's console, not by
        // any property of the engine's text. The true line carries three U+2014, so the honest claims
        // are: it survives the cap WHOLE (never truncated), it comes out entirely printable ASCII,
        // and each em dash becomes a visible escape rather than a raw byte.
        var rendered = EngineDiagnosticExcerpt.SanitiseAndCap(MeasuredRc5RefusalLine);

        // Sanitising expands each em dash 1 -> 6 characters, so the LENGTH that has to fit the cap is
        // the rendered one, not the source one. If this ever starts failing, the engine's sentence
        // grew past the cap and the cap — not the sentence — is what should be re-argued.
        Assert.True(
            rendered.Length < EngineDiagnosticExcerpt.MaxExcerptChars,
            $"The rendered refusal line is {rendered.Length} characters, at or past the "
            + $"{EngineDiagnosticExcerpt.MaxExcerptChars}-character cap.");
        Assert.DoesNotContain(EngineDiagnosticExcerpt.TruncationMarker, rendered, StringComparison.Ordinal);

        Assert.All(rendered, c => Assert.InRange(c, (char)0x20, (char)0x7E));
        Assert.Equal(3, CountOccurrences(rendered, "\\u2014"));

        // THE COUNTS, pinned rather than left in prose — and one of them corrects the review record.
        // MEASURED on a cp852 host (2026-09-15): the line this server captures there is 637
        // characters. The TRUE line is ALSO 637: cp852's best-fit maps U+2014 to a single ASCII
        // hyphen, so the transcoding changes three CODE POINTS and not the length — which is exactly
        // why two local measurements could agree on "637 characters, byte-identical" while both held
        // the wrong text. (A "639 true vs 637 transcoded" figure circulated during review; it is
        // wrong, and this assertion is what stops it being re-adopted.) Escaping each em dash to the
        // six-character \u2014 adds five apiece, so the rendered form is 637 + 15 = 652.
        Assert.Equal(637, MeasuredRc5RefusalLine.Length);
        Assert.Equal(652, rendered.Length);

        // And the constant's own shape, pinned so a future edit cannot quietly reintroduce the
        // transcoded form: three real em dashes, no ASCII-hyphen stand-ins at those three joins.
        Assert.Equal(3, CountOccurrences(MeasuredRc5RefusalLine, "\u2014"));
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> — xUnit has no such assertion.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void SanitiseAndCap_ControlCharacters_AreEscapedNeverPassedThrough()
    {
        // An ANSI escape and a bell — the exact shapes TextSanitiser exists for (see its remarks:
        // "a security boundary, not cosmetics"). Engine stdout can echo suite-derived text, so this
        // is not hypothetical.
        var hostile = "environment configuration error - \u001b]0;pwned\u0007 resource";

        var rendered = EngineDiagnosticExcerpt.SanitiseAndCap(hostile);

        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", rendered, StringComparison.Ordinal);
        Assert.All(rendered, c => Assert.InRange(c, (char)0x20, (char)0x7E));
        Assert.Contains("\\u001b", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitiseAndCap_OverlongLine_IsClippedToTheBoundAndMarked()
    {
        var overlong = "environment configuration error - " + new string('x', 5_000);

        var rendered = EngineDiagnosticExcerpt.SanitiseAndCap(overlong);

        Assert.Equal(
            EngineDiagnosticExcerpt.MaxExcerptChars + EngineDiagnosticExcerpt.TruncationMarker.Length,
            rendered.Length);
        Assert.EndsWith(EngineDiagnosticExcerpt.TruncationMarker, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitiseAndCap_NonAsciiExpansion_IsBoundedAfterEscaping_NotBefore()
    {
        // 400 non-ASCII characters is well under the cap as CHARACTERS but 2,400 after escaping —
        // proving the bound is applied to the SANITISED text, which is what actually reaches the wire.
        var expanding = "environment configuration error - " + new string('é', 400);

        var rendered = EngineDiagnosticExcerpt.SanitiseAndCap(expanding);

        Assert.True(expanding.Length < EngineDiagnosticExcerpt.MaxExcerptChars);
        Assert.Equal(
            EngineDiagnosticExcerpt.MaxExcerptChars + EngineDiagnosticExcerpt.TruncationMarker.Length,
            rendered.Length);
    }

    [Theory]
    [InlineData("environment configuration error - short and clean")]
    [InlineData("environment configuration error - \u001b bell \u0007 and é")]
    public void SanitiseAndCap_IsIdempotent(string line)
    {
        // Load-bearing, not incidental: the helper runs once at VouchfxCliSuiteRunner's retention
        // boundary (where the memory bound must be applied) and again at
        // RunSuiteOrchestrator.BuildEngineRefusalHint's wire boundary (which must not depend on the
        // injected runner having honoured ISuiteRunner's documentation). If f(f(x)) != f(x), the
        // second pass would double-escape the first pass's output.
        var once = EngineDiagnosticExcerpt.SanitiseAndCap(line);

        Assert.Equal(once, EngineDiagnosticExcerpt.SanitiseAndCap(once));
    }

    [Fact]
    public void SanitiseAndCap_IsIdempotent_EvenWhenTheFirstPassTruncated()
    {
        // The interesting case for idempotence: a clipped result is MaxExcerptChars + marker, i.e.
        // longer than the bound, so a naive second clip would eat into the text and re-mark it. It
        // does not, because the second clip removes exactly the marker it then re-appends.
        var overlong = "environment configuration error - " + new string('x', 5_000);

        var once = EngineDiagnosticExcerpt.SanitiseAndCap(overlong);

        Assert.True(once.Length > EngineDiagnosticExcerpt.MaxExcerptChars);
        Assert.Equal(once, EngineDiagnosticExcerpt.SanitiseAndCap(once));
    }

    [Fact]
    public void TruncationMarker_IsPrintableAscii()
    {
        // This is WHY SanitiseAndCap is idempotent: the codebase's usual "…" glyph (U+2026) would be
        // rewritten into a six-character escape by the second sanitising pass, so the marker itself
        // would rot on every reapplication. Pinned as a property rather than left as a comment.
        Assert.All(
            EngineDiagnosticExcerpt.TruncationMarker,
            c => Assert.InRange(c, (char)0x20, (char)0x7E));
    }
}
