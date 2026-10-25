using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtEditTests
{
    public static IEnumerable<object[]> AllFixtures() => Fixtures.All().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Editing_first_middle_and_last_cue_changes_only_that_cues_text_bytes(Fixtures.Fixture fixture)
    {
        SrtDocument doc = SrtDocument.Parse(fixture.Data).Document!;
        foreach (int pos in new[] { 1, doc.Cues.Count / 2, doc.Cues.Count })
        {
            SubtitleCue cue = doc.Cues[pos - 1];

            SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(pos, cue.Text, "Edited line.") });

            AssertOnlyTextRegionChanged(doc, cue, fixture.Data, result.Content.ToArray());
            SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
            Assert.Equal("Edited line.", after.Cues[pos - 1].Text);
            for (int i = 0; i < doc.Cues.Count; i++)
            {
                Assert.Equal(doc.Cues[i].StartMs, after.Cues[i].StartMs);
                Assert.Equal(doc.Cues[i].EndMs, after.Cues[i].EndMs);
                if (i != pos - 1)
                {
                    Assert.Equal(doc.Cues[i].Text, after.Cues[i].Text);
                }
            }

            Assert.Equal(new[] { pos }, result.EditedCuePositions);
            Assert.Empty(result.AddedCuePositions);
            Assert.Equal(SubtitleHash.Compute(result.Content.ToArray()), result.Hash);
        }
    }

    [Fact]
    public void Multi_line_text_uses_the_files_line_ending()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English), "\r\n"));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", "First\nSecond") });

        string text = Encoding.UTF8.GetString(result.Content.Span);
        Assert.Contains("First\r\nSecond", text, StringComparison.Ordinal);
        Assert.DoesNotContain("First\nSecond", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_line_text_in_an_lf_file_uses_lf_and_in_a_cr_file_uses_cr()
    {
        foreach (string eol in new[] { "\n", "\r" })
        {
            byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English), eol));
            SrtDocument doc = SrtDocument.Parse(data).Document!;

            SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", "First\nSecond") });

            Assert.Contains("First" + eol + "Second", Encoding.UTF8.GetString(result.Content.Span), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_multi_line_cue_can_become_one_line_and_back()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        string old = doc.Cues[5].Text;
        Assert.Contains("\n", old, StringComparison.Ordinal);

        byte[] oneLine = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(6, old, "Single line now.") }).Content.ToArray();
        SrtDocument doc2 = SrtDocument.Parse(oneLine).Document!;
        byte[] back = doc2.Apply(new SubtitleEdit[] { new SubtitleTextEdit(6, "Single line now.", old) }).Content.ToArray();

        Assert.Equal(data, back);
    }

    [Fact]
    public void Polish_text_is_written_back_in_windows_1250_bytes()
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish), "\r\n"));
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        const string NewText = "Źdźbło łączy żółć, gęś i jaźń.";

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(3, doc.Cues[2].Text, NewText) });

        byte[] after = result.Content.ToArray();
        byte[] expected = Fixtures.Cp1250.GetBytes(NewText);
        int from = doc.ByteOffset(doc.Cues[2].Layout.TextStart);
        Assert.Equal(expected, after[from..(from + expected.Length)]);
        Assert.Equal(NewText, SrtDocument.Parse(after).Document!.Cues[2].Text);
        AssertOnlyTextRegionChanged(doc, doc.Cues[2], data, after);
    }

    [Theory]
    [InlineData("Привет, мир")]
    [InlineData("Smile \U0001F600")]
    [InlineData("日本語")]
    public void A_character_the_encoding_cannot_hold_is_rejected_and_names_the_encoding(string text)
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        string encodingName = doc.Encoding.Name;

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(2, doc.Cues[1].Text, text) }));

        Assert.Equal(SubtitleEditError.Unrepresentable, ex.Error);
        Assert.Equal(2, ex.CuePosition);
        Assert.Contains(encodingName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ascii_only_file_is_treated_as_utf8_and_can_take_accented_text()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        Assert.Equal("utf-8", doc.Encoding.Name);

        byte[] after = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", "Café — naïve") }).Content.ToArray();

        SrtDocument reparsed = SrtDocument.Parse(after).Document!;
        Assert.Equal("utf-8", reparsed.Encoding.Name);
        Assert.Equal("Café — naïve", reparsed.Cues[0].Text);
    }

    [Fact]
    public void Utf8_files_accept_four_byte_characters()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.German)), bom: true);
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, doc.Cues[0].Text, "Hi \U0001F600") }).Content.ToArray();

        Assert.Equal("Hi \U0001F600", SrtDocument.Parse(after).Document!.Cues[0].Text);
        Assert.Equal(Fixtures.Utf8Bom, after[..3]);
    }

    [Fact]
    public void Utf16_files_keep_their_bom_and_are_written_in_utf16()
    {
        byte[] data = Fixtures.Utf16Le(Fixtures.Render(Fixtures.Cues(Fixtures.German), "\r\n"));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(2, doc.Cues[1].Text, "Zwei\nZeilen")  }).Content.ToArray();

        Assert.Equal(Fixtures.Utf16LeBom, after[..2]);
        SrtDocument reparsed = SrtDocument.Parse(after).Document!;
        Assert.Equal("Zwei\nZeilen", reparsed.Cues[1].Text);
        Assert.Equal("utf-16le", reparsed.Encoding.Name);
        AssertOnlyTextRegionChanged(doc, doc.Cues[1], data, after);
    }

    [Fact]
    public void A_cue_with_no_text_can_be_given_text()
    {
        string text = "1\n00:00:01,000 --> 00:00:02,000\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n";
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        byte[] after = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, string.Empty, "Now it speaks") }).Content.ToArray();

        Assert.Equal(
            "1\n00:00:01,000 --> 00:00:02,000\nNow it speaks\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n",
            Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void A_text_less_cue_at_the_very_end_of_the_file_can_be_given_text()
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8("1\n00:00:01,000 --> 00:00:02,000")).Document!;

        byte[] after = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, string.Empty, "Late text") }).Content.ToArray();

        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\nLate text", Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void Several_edits_in_one_request_all_apply()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English), "\r\n"));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SubtitleApplyResult result = doc.Apply(
            new SubtitleEdit[]
            {
                new SubtitleTextEdit(12, doc.Cues[11].Text, "Last, much longer line than before"),
                new SubtitleTextEdit(1, doc.Cues[0].Text, "Hi"),
                new SubtitleTextEdit(6, doc.Cues[5].Text, "One\nTwo"),
            });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal("Hi", after.Cues[0].Text);
        Assert.Equal("One\nTwo", after.Cues[5].Text);
        Assert.Equal("Last, much longer line than before", after.Cues[11].Text);
        Assert.Equal(doc.Cues[1].Text, after.Cues[1].Text);
        Assert.Equal(new[] { 1, 6, 12 }, result.EditedCuePositions);
    }

    [Fact]
    public void Old_text_with_windows_line_breaks_still_matches_an_lf_file()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SubtitleApplyResult result = doc.Apply(
            new SubtitleEdit[] { new SubtitleTextEdit(2, "This is a two-line cue\r\nwith a line break.", "Fixed") });

        Assert.Equal("Fixed", SrtDocument.Parse(result.Content.ToArray()).Document!.Cues[1].Text);
    }

    [Fact]
    public void Edits_in_a_gap_less_malformed_file_keep_it_readable()
    {
        byte[] data = Fixtures.Malformed();
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(2, doc.Cues[1].Text, "Two\nlines") }).Content.ToArray();

        SrtDocument reparsed = SrtDocument.Parse(after).Document!;
        Assert.Equal(doc.Cues.Count, reparsed.Cues.Count);
        Assert.Equal("Two\nlines", reparsed.Cues[1].Text);
        Assert.Equal(doc.Cues[2].Text, reparsed.Cues[2].Text);
        AssertOnlyTextRegionChanged(doc, doc.Cues[1], data, after);
    }

    [Fact]
    public void Text_that_would_be_read_back_as_the_next_cues_number_is_refused_not_saved()
    {
        // In a gap-less file whose next cue has no number line, a text ending in a bare number would be
        // taken as that cue's number. The post-edit self-check must catch it.
        SrtDocument doc = SrtDocument.Parse(Fixtures.Malformed()).Document!;
        Assert.Null(doc.Cues[2].Number);

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(2, doc.Cues[1].Text, "42") }));

        Assert.Equal(SubtitleEditError.VerificationFailed, ex.Error);
    }

    [Fact]
    public void The_documents_own_data_is_never_modified_by_apply()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        byte[] copy = (byte[])data.Clone();
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        _ = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, "Hello there.", "Changed") });

        Assert.Equal(copy, data);
        Assert.Equal(SubtitleHash.Compute(copy), doc.Hash);
    }

    /// <summary>
    /// The bytes before and after the edited cue's text region must be identical to the original's.
    /// </summary>
    private static void AssertOnlyTextRegionChanged(SrtDocument doc, SubtitleCue cue, byte[] before, byte[] after)
    {
        int from = doc.ByteOffset(cue.Layout.TextStart);
        int to = doc.ByteOffset(cue.Layout.TextEnd);
        Assert.Equal(before[..from], after[..from]);
        Assert.Equal(before[to..], after[^(before.Length - to)..]);
    }
}
