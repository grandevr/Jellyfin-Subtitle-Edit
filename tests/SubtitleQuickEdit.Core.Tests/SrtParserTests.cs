using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Srt;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtParserTests
{
    public static IEnumerable<object[]> AllFixtures() => Fixtures.All().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Parses_every_fixture_with_the_right_texts_times_eol_and_bom(Fixtures.Fixture fixture)
    {
        SrtParseResult result = SrtDocument.Parse(fixture.Data);

        Assert.True(result.Document is not null, fixture.Name + ": " + result.FailureReason);
        SrtDocument doc = result.Document!;
        Assert.Equal(fixture.Texts, doc.Cues.Select(c => c.Text).ToArray());
        Assert.Equal(fixture.Eol, doc.LineEnding);
        Assert.Equal(fixture.BomLength, doc.Encoding.BomLength);
        Assert.Equal(Enumerable.Range(1, fixture.Texts.Length), doc.Cues.Select(c => c.Position));
        Assert.Equal(Enumerable.Range(1, fixture.Texts.Length), doc.Cues.Select(c => c.Number!.Value));
        Assert.Equal(2000, doc.Cues[0].StartMs);
        Assert.Equal(5000, doc.Cues[0].EndMs);
        Assert.Equal(SrtDocument.ComputeHash(fixture.Data), doc.Hash);
    }

    [Fact]
    public void Dot_milliseconds_are_read_as_milliseconds()
    {
        byte[] data = Fixtures.Utf8("1\n00:00:01.500 --> 00:00:02.250\nHi\n\n");

        SrtDocument doc = SrtDocument.Parse(data).Document!;

        Assert.Equal(1500, doc.Cues[0].StartMs);
        Assert.Equal(2250, doc.Cues[0].EndMs);
    }

    [Fact]
    public void Short_fractions_are_padded_on_the_right()
    {
        byte[] data = Fixtures.Utf8("1\n00:00:01,5 --> 00:00:02,25\nHi\n");

        SrtDocument doc = SrtDocument.Parse(data).Document!;

        Assert.Equal(1500, doc.Cues[0].StartMs);
        Assert.Equal(2250, doc.Cues[0].EndMs);
    }

    [Fact]
    public void Malformed_file_is_parsed_leniently()
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Malformed()).Document!;

        Assert.Equal(Fixtures.English, doc.Cues.Select(c => c.Text).ToArray());
        Assert.Null(doc.Cues[2].Number);
        Assert.Equal(2, doc.Cues[1].Number);
        Assert.Equal(4, doc.Cues[3].Number);
    }

    [Fact]
    public void Duplicate_and_out_of_order_numbers_are_kept_as_written_and_positions_stay_sequential()
    {
        string text = "5\n00:00:01,000 --> 00:00:02,000\nA\n\n5\n00:00:03,000 --> 00:00:04,000\nB\n\n2\n00:00:00,500 --> 00:00:00,900\nC\n";

        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        Assert.Equal(new int?[] { 5, 5, 2 }, doc.Cues.Select(c => c.Number).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, doc.Cues.Select(c => c.Position).ToArray());
    }

    [Fact]
    public void Leading_blank_lines_and_a_bom_are_accepted()
    {
        byte[] data = Fixtures.Utf8("\n\n1\n00:00:01,000 --> 00:00:02,000\nA\n", bom: true);

        SrtDocument doc = SrtDocument.Parse(data).Document!;

        Assert.Single(doc.Cues);
        Assert.Equal(3, doc.Encoding.BomLength);
    }

    [Fact]
    public void Cue_without_text_has_empty_text()
    {
        string text = "1\n00:00:01,000 --> 00:00:02,000\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n";

        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        Assert.Equal(string.Empty, doc.Cues[0].Text);
        Assert.Equal("B", doc.Cues[1].Text);
    }

    [Fact]
    public void Mixed_line_endings_use_the_most_common_one()
    {
        string text = "1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\n\r\n2\n00:00:03,000 --> 00:00:04,000\nB\r\n";

        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        Assert.Equal("\r\n", doc.LineEnding);
        Assert.Equal("B", doc.Cues[1].Text);
    }

    [Fact]
    public void Text_keeps_html_tags_and_leading_and_trailing_spaces()
    {
        string text = "1\n00:00:01,000 --> 00:00:02,000\n <i>Hi</i> \n";

        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        Assert.Equal(" <i>Hi</i> ", doc.Cues[0].Text);
    }

    [Fact]
    public void A_text_line_that_is_only_digits_stays_text_when_no_time_line_follows()
    {
        string text = "1\n00:00:01,000 --> 00:00:02,000\n42\nforty-two\n";

        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        Assert.Equal("42\nforty-two", doc.Cues[0].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  \n")]
    [InlineData("just some text\nwith no cues\n")]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHi\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nA\n\nstray paragraph after a blank line\n")]
    [InlineData("1\n00:00:01,000 --> nonsense\nA\n")]
    public void Unreliable_or_non_srt_files_are_not_editable_with_a_reason(string content)
    {
        SrtParseResult result = SrtDocument.Parse(Fixtures.Utf8(content));

        Assert.Null(result.Document);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public void Binary_garbage_is_not_editable()
    {
        var random = new Random(7);
        byte[] data = new byte[4096];
        random.NextBytes(data);

        Assert.Null(SrtDocument.Parse(data).Document);
    }

    [Fact]
    public void Oversized_files_are_not_editable()
    {
        byte[] data = new byte[SrtDocument.MaxFileBytes + 1];
        Array.Fill(data, (byte)'a');

        SrtParseResult result = SrtDocument.Parse(data);

        Assert.Null(result.Document);
        Assert.Contains("too large", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf32_is_not_supported()
    {
        byte[] data = new UTF32Encoding(false, true).GetPreamble()
            .Concat(new UTF32Encoding(false, false).GetBytes("1\n00:00:01,000 --> 00:00:02,000\nA\n"))
            .ToArray();

        Assert.Null(SrtDocument.Parse(data).Document);
    }

    [Fact]
    public void Parsing_is_deterministic_and_does_not_modify_the_input()
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish), "\r\n"));
        byte[] copy = (byte[])data.Clone();

        SrtDocument first = SrtDocument.Parse(data).Document!;
        SrtDocument second = SrtDocument.Parse(data).Document!;

        Assert.Equal(copy, data);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Cues.Select(c => c.Text), second.Cues.Select(c => c.Text));
    }

    [Fact]
    public void Four_byte_characters_in_utf8_map_to_the_right_offsets()
    {
        string text = "1\n00:00:01,000 --> 00:00:02,000\nHi \U0001F600 there\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond\n";
        byte[] data = Fixtures.Utf8(text);

        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SrtCue second = doc.Cues[1];
        int from = doc.ByteOffset(second.Layout.TextStart);
        int to = doc.ByteOffset(second.Layout.TextEnd);
        Assert.Equal("Second", Encoding.UTF8.GetString(data, from, to - from));
    }
}
