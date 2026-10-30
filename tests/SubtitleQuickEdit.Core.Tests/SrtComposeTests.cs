using System;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtComposeTests
{
    [Fact]
    public void A_new_file_has_the_lines_in_start_order_numbered_from_one()
    {
        SubtitleApplyResult result = SrtDocument.Compose(new[]
        {
            new SubtitleAddCue("b", 5000, 6500, "Second"),
            new SubtitleAddCue("a", 1000, 2000, "First\nline two"),
        });

        string text = Encoding.UTF8.GetString(result.Content.Span);
        Assert.Equal(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\nFirst\r\nline two\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,500\r\nSecond\r\n\r\n",
            text);
        Assert.Equal(1, result.AddedCuePositions["a"]);
        Assert.Equal(2, result.AddedCuePositions["b"]);
        Assert.Equal(SubtitleHash.Compute(result.Content.ToArray()), result.Hash);
    }

    [Fact]
    public void The_new_file_is_editable_utf8_without_a_bom()
    {
        SubtitleApplyResult result = SrtDocument.Compose(new[] { new SubtitleAddCue("a", 0, 900, "Zażółć gęślą jaźń") });

        TrackInspection inspection = SubtitleTrackInspector.InspectContent(SubtitleFormat.Srt, result.Content.ToArray());
        Assert.True(inspection.Editable);
        Assert.Equal("Zażółć gęślą jaźń", inspection.Document!.Cues.Single().Text);
        Assert.NotEqual(0xEF, result.Content.Span[0]);
    }

    [Fact]
    public void Lines_with_the_same_start_keep_the_order_they_were_given()
    {
        SubtitleApplyResult result = SrtDocument.Compose(new[]
        {
            new SubtitleAddCue("x", 1000, 2000, "Top"),
            new SubtitleAddCue("y", 1000, 3000, "Bottom"),
        });

        SrtDocument doc = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(new[] { "Top", "Bottom" }, doc.Cues.Select(c => c.Text));
    }

    [Fact]
    public void No_lines_is_refused()
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(() => SrtDocument.Compose(Array.Empty<SubtitleAddCue>()));
        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
    }

    [Theory]
    [InlineData(2000, 1000, "Hi")]
    [InlineData(1000, 1050, "Hi")]
    [InlineData(1000, 2000, "")]
    [InlineData(1000, 2000, "One\n\nTwo")]
    [InlineData(1000, 2000, "00:00:01,000 --> 00:00:02,000")]
    [InlineData(-1, 2000, "Hi")]
    public void A_line_gets_the_same_checks_as_an_added_line(long start, long end, string text)
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => SrtDocument.Compose(new[] { new SubtitleAddCue("ok", 0, 500, "Fine"), new SubtitleAddCue("bad", start, end, text) }));
        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
        Assert.Equal("bad", ex.ClientId);
    }

    [Fact]
    public void A_line_past_the_runtime_is_refused()
    {
        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => SrtDocument.Compose(new[] { new SubtitleAddCue("a", 1000, 61000, "Late") }, runtimeMs: 60000));
        Assert.Equal("a", ex.ClientId);
    }

    [Fact]
    public void Two_lines_with_one_client_id_are_refused()
    {
        Assert.Throws<SubtitleEditException>(
            () => SrtDocument.Compose(new[] { new SubtitleAddCue("a", 0, 500, "One"), new SubtitleAddCue("a", 600, 900, "Two") }));
    }

    [Fact]
    public void A_last_text_line_that_is_a_number_reads_back_as_text()
    {
        SubtitleApplyResult result = SrtDocument.Compose(new[] { new SubtitleAddCue("a", 0, 500, "Count\n42"), new SubtitleAddCue("b", 600, 900, "Two") });

        SrtDocument doc = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(new[] { "Count\n42", "Two" }, doc.Cues.Select(c => c.Text));
    }
}
