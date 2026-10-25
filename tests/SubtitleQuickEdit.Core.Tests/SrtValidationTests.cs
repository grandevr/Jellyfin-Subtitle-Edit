using System;
using System.Linq;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtValidationTests
{
    private static SrtDocument Doc() =>
        SrtDocument.Parse(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)))).Document!;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\n\nb")]
    [InlineData("a\n")]
    [InlineData("\na")]
    [InlineData("a\n   \nb")]
    [InlineData("00:00:01,000 --> 00:00:02,000")]
    [InlineData("fine\n00:00:01,000 --> 00:00:02,000")]
    [InlineData("bell\u0007")]
    [InlineData("nul\0here")]
    public void Unusable_cue_text_is_rejected_for_edits_and_adds(string text)
    {
        SrtDocument doc = Doc();

        SubtitleEditException edit = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", text) }));
        SubtitleEditException add = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("x", 100, 900, text) }));

        Assert.Equal(SubtitleEditError.InvalidEdit, edit.Error);
        Assert.Equal(1, edit.CuePosition);
        Assert.Equal(SubtitleEditError.InvalidEdit, add.Error);
        Assert.Null(edit.ClientId);
        Assert.Equal("x", add.ClientId);
    }

    [Fact]
    public void An_invalid_added_line_names_its_client_id_and_other_errors_do_not()
    {
        SrtDocument doc = Doc();

        SubtitleEditException late = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("fine", 100, 900, "ok"), new SubtitleAddCue("late", 500, 520, "too short") }));
        SubtitleEditException twin = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("same", 100, 900, "a"), new SubtitleAddCue("same", 1000, 2000, "b") }));
        SubtitleEditException mismatch = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "not the text", "x") }));

        Assert.Equal("late", late.ClientId);
        Assert.Equal("same", twin.ClientId);
        Assert.Equal(SubtitleEditError.TextMismatch, mismatch.Error);
        Assert.Null(mismatch.ClientId);
    }

    [Theory]
    [InlineData("tab\tseparated")]
    [InlineData("one\r\ntwo")]
    [InlineData("<i>tags</i> & {\\an8}ass-ish")]
    [InlineData("--> not a time line")]
    public void Ordinary_text_is_accepted(string text)
    {
        SrtDocument doc = Doc();

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", text) });

        string expected = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, SrtDocument.Parse(result.Content.ToArray()).Document!.Cues[0].Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(13)]
    [InlineData(500)]
    public void A_cue_position_that_does_not_exist_is_refused(int position)
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => Doc().Apply(new SubtitleEdit[] { new SubtitleTextEdit(position, "x", "y") }));

        Assert.Equal(SubtitleEditError.CueNotFound, ex.Error);
        Assert.Equal(position, ex.CuePosition);
    }

    [Fact]
    public void Old_text_that_no_longer_matches_is_a_conflict_naming_the_cue()
    {
        SrtDocument doc = Doc();

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(
                new SubtitleEdit[]
                {
                    new SubtitleTextEdit(1, "Hello there.", "Fine"),
                    new SubtitleTextEdit(2, "Somebody else changed this", "Mine"),
                }));

        Assert.Equal(SubtitleEditError.TextMismatch, ex.Error);
        Assert.Equal(2, ex.CuePosition);
        Assert.Contains("Cue 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_cue_edited_twice_in_one_request_is_refused()
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => Doc().Apply(
                new SubtitleEdit[]
                {
                    new SubtitleTextEdit(1, "Hello there.", "One"),
                    new SubtitleTextEdit(1, "Hello there.", "Two"),
                }));

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
    }

    [Fact]
    public void An_unknown_edit_type_is_refused()
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(() => Doc().Apply(new SubtitleEdit[] { new UnknownEdit() }));

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
    }

    [Theory]
    [InlineData(-1, 500)]
    [InlineData(1000, 1000)]
    [InlineData(1000, 900)]
    [InlineData(1000, 1099)]
    [InlineData(0, 99)]
    [InlineData(1000, 360_000_000)]
    public void Bad_times_on_an_added_cue_are_refused(long start, long end)
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => Doc().Apply(new SubtitleEdit[] { new SubtitleAddCue("x", start, end, "Text") }));

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
    }

    [Theory]
    [InlineData(1000, 1100)]
    [InlineData(0, 100)]
    [InlineData(358_000_000, 359_999_999)]
    public void Boundary_times_on_an_added_cue_are_accepted(long start, long end)
    {
        SubtitleApplyResult result = Doc().Apply(new SubtitleEdit[] { new SubtitleAddCue("x", start, end, "Text") });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Contains(after.Cues, c => c.StartMs == start && c.EndMs == end);
    }

    [Fact]
    public void An_added_cue_may_not_end_after_the_runtime_but_may_end_exactly_at_it()
    {
        SrtDocument doc = Doc();

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("x", 59_000, 60_001, "Text") }, runtimeMs: 60_000));
        SubtitleApplyResult exact = doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("x", 59_000, 60_000, "Text") }, runtimeMs: 60_000);

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
        Assert.NotEmpty(exact.Content.ToArray());
    }

    [Fact]
    public void Added_cues_need_a_unique_non_empty_client_id()
    {
        SrtDocument doc = Doc();

        Assert.Equal(
            SubtitleEditError.InvalidEdit,
            Assert.Throws<SubtitleEditException>(() => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue(string.Empty, 100, 900, "T") })).Error);
        Assert.Equal(
            SubtitleEditError.InvalidEdit,
            Assert.Throws<SubtitleEditException>(
                () => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("a", 100, 900, "T"), new SubtitleAddCue("a", 2000, 2900, "U") })).Error);
    }

    [Fact]
    public void A_refused_request_changes_nothing_in_the_document()
    {
        SrtDocument doc = Doc();
        string before = doc.Hash;

        Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", "Fine"), new SubtitleTextEdit(99, "x", "y") }));

        Assert.Equal(before, doc.Hash);
        Assert.Equal(Fixtures.English, doc.Cues.Select(c => c.Text).ToArray());
        Assert.Equal(before, doc.Apply(Array.Empty<SubtitleEdit>()).Hash);
    }

    private sealed record UnknownEdit : SubtitleEdit;
}
