using Vouchfx.Mcp.Specs;

namespace Vouchfx.Mcp.Tests.Specs;

/// <summary>
/// The PARENT side of the spec-index worker boundary, driven with synthetic worker output —
/// <c>ApplyReportedEntries</c>'s line handling and <c>Sanitise</c>'s on-receipt re-bounding.
/// </summary>
/// <remarks>
/// <para>
/// <b>No process is spawned here, and that is the point.</b> The relay's job is to survive text a
/// misbehaving child could produce, and a real worker will never produce most of it — so driving these
/// paths through an actual spawn would be slow, flaky, and mostly impossible. Feeding the parser
/// exactly the bytes in question is both faster and STRICTER (a code review's finding: the boundary
/// shipped with its adversarial paths reachable only in theory).
/// </para>
/// <para>
/// <b>The posture being tested is "the child is untrusted".</b> That claim is made in
/// <c>SpecIndexWorkerProtocol</c>'s header and it has to mean something operationally: the parent
/// already re-derives the <c>path</c> rather than accepting the child's, and these tests hold the same
/// line for every other field. Caps enforced only child-side would trust the process this codebase
/// documents as untrusted.
/// </para>
/// </remarks>
public class SpecIndexWorkerRelayTests
{
    private static SpecIndexWorkerEntry?[] Empty(int length) => new SpecIndexWorkerEntry?[length];

    // ── ApplyReportedEntries: line handling ────────────────────────────────────────────────────

    [Fact]
    public void WellFormedLines_AreAppliedInOrderAndAdvanceTheHighWaterMark()
    {
        var results = Empty(3);

        var reported = SpecIndexWorkerClient.ApplyReportedEntries(
            """
            {"index":0,"name":"first","tags":["a"],"stepTypes":["http.rest"],"steps":1,"readable":true}
            {"index":1,"name":"second","tags":[],"stepTypes":[],"steps":0,"readable":true}

            """,
            startIndex: 0,
            batchLength: 3,
            results);

        Assert.Equal(2, reported);
        Assert.Equal("first", results[0]!.Name);
        Assert.Equal("second", results[1]!.Name);
        Assert.Null(results[2]);
    }

    [Fact]
    public void AMalformedJsonLine_IsSkippedWithoutLosingTheLinesAroundIt()
    {
        var results = Empty(3);

        var reported = SpecIndexWorkerClient.ApplyReportedEntries(
            """
            {"index":0,"name":"first","tags":[],"stepTypes":[],"steps":0,"readable":true}
            {this is not json at all
            {"index":2,"name":"third","tags":[],"stepTypes":[],"steps":0,"readable":true}

            """,
            startIndex: 0,
            batchLength: 3,
            results);

        // The hole is real and is left for the caller's degrade pass; what must NOT happen is the
        // whole batch being discarded because one line was garbage.
        Assert.Equal(3, reported);
        Assert.Equal("first", results[0]!.Name);
        Assert.Null(results[1]);
        Assert.Equal("third", results[2]!.Name);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void AnOutOfRangeIndex_IsDiscardedRatherThanClamped(int index)
    {
        var results = Empty(3);

        var reported = SpecIndexWorkerClient.ApplyReportedEntries(
            $$"""{"index":{{index}},"name":"stray","tags":[],"stepTypes":[],"steps":0,"readable":true}"""
                + "\n",
            startIndex: 0,
            batchLength: 3,
            results);

        // Clamping would attribute one suite's parse result to a DIFFERENT suite's path, which is
        // worse than losing it. Nothing is written and the high-water mark does not move.
        Assert.Equal(0, reported);
        Assert.All(results, entry => Assert.Null(entry));
    }

    [Fact]
    public void AnIndexIsOffsetByStartIndex_SoAResumedBatchLandsInTheRightSlots()
    {
        var results = Empty(5);

        // A resumed worker numbers ITS OWN batch from zero; the parent maps that onto absolute
        // positions. Getting this wrong would silently mis-attribute every entry after a resume.
        var reported = SpecIndexWorkerClient.ApplyReportedEntries(
            """{"index":0,"name":"resumed","tags":[],"stepTypes":[],"steps":0,"readable":true}""" + "\n",
            startIndex: 3,
            batchLength: 2,
            results);

        Assert.Equal(4, reported);
        Assert.Equal("resumed", results[3]!.Name);
        Assert.Equal(3, results[3]!.Index);
        Assert.Null(results[0]);
    }

    [Fact]
    public void ADuplicateIndex_DoesNotOverwriteAndDoesNotMoveTheMarkBackwards()
    {
        var results = Empty(2);

        var reported = SpecIndexWorkerClient.ApplyReportedEntries(
            """
            {"index":1,"name":"out-of-order","tags":[],"stepTypes":[],"steps":0,"readable":true}
            {"index":0,"name":"first","tags":[],"stepTypes":[],"steps":0,"readable":true}
            {"index":0,"name":"IMPOSTOR","tags":[],"stepTypes":[],"steps":0,"readable":true}

            """,
            startIndex: 0,
            batchLength: 2,
            results);

        // First writer wins — deterministic, rather than last-write-wins.
        Assert.Equal("first", results[0]!.Name);

        // And an out-of-order line does not drag the resume point backwards over files already
        // reported, which would make the caller re-run them.
        Assert.Equal(2, reported);
    }

    [Fact]
    public void ATornFinalLine_IsDropped()
    {
        var results = Empty(2);

        // Exactly what a kill landing mid-write leaves behind. The worker newline-terminates every
        // flush, so "no trailing newline" is precisely "this line is incomplete".
        var reported = SpecIndexWorkerClient.ApplyReportedEntries(
            """
            {"index":0,"name":"complete","tags":[],"stepTypes":[],"steps":0,"readable":true}
            {"index":1,"name":"tor
            """,
            startIndex: 0,
            batchLength: 2,
            results);

        Assert.Equal(1, reported);
        Assert.Equal("complete", results[0]!.Name);
        Assert.Null(results[1]);
    }

    [Fact]
    public void CrLfLineEndings_AreHandled() =>
        // The worker uses Console.Out.WriteLine, which emits the PLATFORM newline — CRLF on Windows.
        // A splitter that only understood LF would leave a stray '\r' on every line and fail to parse
        // every single one, on the platform this repository is most often built on.
        Assert.Equal(
            1,
            SpecIndexWorkerClient.ApplyReportedEntries(
                "{\"index\":0,\"name\":\"n\",\"tags\":[],\"stepTypes\":[],\"steps\":0,\"readable\":true}\r\n",
                startIndex: 0,
                batchLength: 1,
                Empty(1)));

    [Fact]
    public void EmptyOutput_ReportsNothingAndReturnsTheStartIndex() =>
        Assert.Equal(7, SpecIndexWorkerClient.ApplyReportedEntries(string.Empty, 7, 3, Empty(10)));

    // ── Sanitise: the parent re-applies every bound on receipt ─────────────────────────────────

    [Fact]
    public void MissingCollections_DeserialiseToNullAndAreNormalisedRatherThanThrowing()
    {
        var results = Empty(1);

        // `{"index":0}` is syntactically valid JSON and leaves Tags/StepTypes null. Before the record
        // declared them nullable and Sanitise normalised them, that null travelled all the way to
        // serialisation of the resource body (a security review's finding).
        SpecIndexWorkerClient.ApplyReportedEntries("""{"index":0}""" + "\n", 0, 1, results);

        var sanitised = SpecIndexWorkerClient.Sanitise(results[0]!);

        Assert.NotNull(sanitised.Tags);
        Assert.NotNull(sanitised.StepTypes);
        Assert.Empty(sanitised.Tags!);
        Assert.Empty(sanitised.StepTypes!);
        Assert.Null(sanitised.Name);
        Assert.False(sanitised.Readable);
    }

    [Fact]
    public void AnOversizedName_IsReCappedByTheParent()
    {
        var entry = new SpecIndexWorkerEntry(
            0, new string('n', SpecIndexParser.MaxNameChars * 10), [], [], 0, true, null);

        Assert.Equal(SpecIndexParser.MaxNameChars, SpecIndexWorkerClient.Sanitise(entry).Name!.Length);
    }

    [Fact]
    public void OversizedAndOverlongCollections_AreReCappedByTheParent()
    {
        var entry = new SpecIndexWorkerEntry(
            0,
            null,
            [.. Enumerable.Repeat(new string('t', 500), SpecIndexParser.MaxTagsPerSpec * 3)],
            [.. Enumerable.Repeat(new string('s', 500), SpecIndexParser.MaxStepTypesPerSpec * 3)],
            0,
            true,
            null);

        var sanitised = SpecIndexWorkerClient.Sanitise(entry);

        Assert.Equal(SpecIndexParser.MaxTagsPerSpec, sanitised.Tags!.Count);
        Assert.All(sanitised.Tags!, tag => Assert.True(tag.Length <= SpecIndexParser.MaxTagChars));

        Assert.Equal(SpecIndexParser.MaxStepTypesPerSpec, sanitised.StepTypes!.Count);
        Assert.All(
            sanitised.StepTypes!,
            stepType => Assert.True(stepType.Length <= SpecIndexParser.MaxStepTypeChars));
    }

    [Fact]
    public void ControlCharactersFromTheChild_AreSanitisedByTheParent()
    {
        // Composed numerically rather than written as a literal, per this repository's convention. A
        // child that emitted an ANSI escape would otherwise write it into the host's terminal.
        var escape = ((char)27).ToString();
        var entry = new SpecIndexWorkerEntry(
            0, $"name{escape}[31m", [$"tag{escape}"], [$"type{escape}"], 0, true, $"error{escape}");

        var sanitised = SpecIndexWorkerClient.Sanitise(entry);

        Assert.DoesNotContain(escape, sanitised.Name!, StringComparison.Ordinal);
        Assert.DoesNotContain(escape, sanitised.Tags![0], StringComparison.Ordinal);
        Assert.DoesNotContain(escape, sanitised.StepTypes![0], StringComparison.Ordinal);
        Assert.DoesNotContain(escape, sanitised.ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeStepCount_IsNormalisedToZero() =>
        // A count is not a signed quantity; a negative one is a lie the response should not repeat.
        Assert.Equal(
            0,
            SpecIndexWorkerClient.Sanitise(new SpecIndexWorkerEntry(0, null, [], [], -5, true, null)).Steps);

    [Fact]
    public void SanitiseIsIdempotentForAWellBehavedEntry()
    {
        var entry = new SpecIndexWorkerEntry(
            0, "Orders smoke", ["smoke"], ["http.rest"], 2, true, null);

        var once = SpecIndexWorkerClient.Sanitise(entry);
        var twice = SpecIndexWorkerClient.Sanitise(once);

        // The ordinary case must be a pure pass-through, or every real index would be quietly
        // reshaped by its own safety net.
        Assert.Equal(entry.Name, once.Name);
        Assert.Equal(entry.Tags, once.Tags);
        Assert.Equal(entry.StepTypes, once.StepTypes);
        Assert.Equal(once.Name, twice.Name);
        Assert.Equal(once.Tags, twice.Tags);
    }
}
