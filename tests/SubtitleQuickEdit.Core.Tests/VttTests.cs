using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Subtitles;
using SubtitleQuickEdit.Core.Vtt;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class VttTests
{
    // A file with every kind of block the editor must leave alone: header metadata, NOTE, STYLE, REGION, cue ids,
    // cue settings, short (mm:ss.ttt) and long times, voice and italic tags, and a cue with no text.
    private const string Rich =
        "WEBVTT - Test file\n"
        + "Kind: captions\n"
        + "Language: en\n"
        + "\n"
        + "STYLE\n"
        + "::cue { color: yellow; }\n"
        + "\n"
        + "REGION\n"
        + "id:top width:40%\n"
        + "\n"
        + "NOTE This is a comment\n"
        + "that spans two lines\n"
        + "\n"
        + "intro\n"
        + "00:01.000 --> 00:03.000 align:start line:0\n"
        + "<v Anna>Hello there.</v>\n"
        + "\n"
        + "00:00:04.000 --> 00:00:06.500\n"
        + "Two lines\n"
        + "<i>in one cue</i>\n"
        + "\n"
        + "NOTE between cues\n"
        + "\n"
        + "3\n"
        + "00:00:08.250 --> 00:00:09.000 region:top\n"
        + "\n"
        + "01:00:10.000 --> 01:00:12.000\n"
        + "Last line\n";

    public static IEnumerable<object[]> Files()
    {
        yield return new object[] { "rich-lf", Fixtures.Utf8(Rich) };
        yield return new object[] { "rich-crlf", Fixtures.Utf8(Rich.Replace("\n", "\r\n", StringComparison.Ordinal)) };
        yield return new object[] { "rich-bom", Fixtures.Utf8(Rich, bom: true) };
        yield return new object[] { "no-final-newline", Fixtures.Utf8(Rich.TrimEnd('\n')) };
        yield return new object[] { "extra-blank-lines", Fixtures.Utf8(Rich + "\n\n\n") };
        yield return new object[] { "polish", Fixtures.Utf8("WEBVTT\n\n00:01.000 --> 00:02.000\nZażółć gęślą jaźń.\n") };
        yield return new object[] { "utf16", Fixtures.Utf16Le("WEBVTT\n\n00:01.000 --> 00:02.000\nGrüß dich.\n") };
        yield return new object[] { "header-without-blank", Fixtures.Utf8("WEBVTT\n00:01.000 --> 00:02.000\nTight.\n") };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Files_round_trip_byte_for_byte_and_with_an_unchanged_edit(string name, byte[] data)
    {
        VttDocument doc = Parse(data, name);

        Assert.Equal(data, doc.Apply(Array.Empty<SubtitleEdit>()).Content.ToArray());
        SubtitleCue first = doc.Cues.First(c => c.Text.Length > 0);
        SubtitleApplyResult same = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(first.Position, first.Text, first.Text) });
        Assert.Equal(data, same.Content.ToArray());
        Assert.Equal(SubtitleHash.Compute(data), same.Hash);
    }

    [Fact]
    public void Only_cues_are_cues_with_their_times_and_text()
    {
        VttDocument doc = Parse(Fixtures.Utf8(Rich));

        Assert.Equal(SubtitleFormat.WebVtt, doc.Format);
        Assert.Equal(
            new[] { (1, 1000L, 3000L, "<v Anna>Hello there.</v>"), (2, 4000L, 6500L, "Two lines\n<i>in one cue</i>"), (3, 8250L, 9000L, string.Empty), (4, 3_610_000L, 3_612_000L, "Last line") },
            doc.Cues.Select(c => (c.Position, c.StartMs, c.EndMs, c.Text)).ToArray());
        Assert.All(doc.Cues, c => Assert.Null(c.Number));
    }

    [Fact]
    public void A_timing_line_inside_a_payload_starts_the_next_cue()
    {
        VttDocument doc = Parse(Fixtures.Utf8("WEBVTT\n\n00:01.000 --> 00:02.000\nOne\n00:03.000 --> 00:04.000\nTwo\n"));

        Assert.Equal(new[] { "One", "Two" }, doc.Cues.Select(c => c.Text));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void A_text_edit_changes_only_that_cues_text_in_the_files_line_ending(string eol)
    {
        byte[] data = Fixtures.Utf8(Rich.Replace("\n", eol, StringComparison.Ordinal));
        VttDocument doc = Parse(data);

        byte[] edited = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(2, "Two lines\n<i>in one cue</i>", "Fixed\nsecond") }).Content.ToArray();

        string expected = Rich.Replace("Two lines\n<i>in one cue</i>", "Fixed\nsecond", StringComparison.Ordinal);
        Assert.Equal(Fixtures.Utf8(expected.Replace("\n", eol, StringComparison.Ordinal)), edited);
    }

    [Fact]
    public void Text_can_be_given_to_a_cue_that_had_none()
    {
        VttDocument doc = Parse(Fixtures.Utf8(Rich));

        byte[] edited = doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(3, string.Empty, "Now with text") }).Content.ToArray();

        string expected = Rich.Replace("region:top\n\n", "region:top\nNow with text\n\n", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(edited));
    }

    [Fact]
    public void An_added_cue_goes_before_the_first_later_cue_without_an_id_and_nothing_is_renumbered()
    {
        VttDocument doc = Parse(Fixtures.Utf8(Rich));

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("a1", 7000, 7800, "Added") });

        string expected = Rich.Replace("NOTE between cues\n\n3\n", "NOTE between cues\n\n00:00:07.000 --> 00:00:07.800\nAdded\n\n3\n", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(result.Content.ToArray()));
        Assert.Equal(3, result.AddedCuePositions["a1"]);
        Assert.Equal(5, Parse(result.Content.ToArray()).Cues.Count);
    }

    [Fact]
    public void Added_cues_before_the_first_cue_and_after_the_last_keep_the_other_blocks()
    {
        VttDocument doc = Parse(Fixtures.Utf8(Rich));

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[]
        {
            new SubtitleAddCue("end", 3_700_000, 3_701_000, "After all"),
            new SubtitleAddCue("start", 0, 500, "First"),
        });

        string expected = Rich
            .Replace("\nintro\n", "\n00:00:00.000 --> 00:00:00.500\nFirst\n\nintro\n", StringComparison.Ordinal)
            + "\n01:01:40.000 --> 01:01:41.000\nAfter all\n";
        Assert.Equal(expected, Encoding.UTF8.GetString(result.Content.ToArray()));
        Assert.Equal(1, result.AddedCuePositions["start"]);
        Assert.Equal(6, result.AddedCuePositions["end"]);
        Assert.Equal(new[] { 2 }, doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("s", 0, 500, "x"), new SubtitleTextEdit(1, "<v Anna>Hello there.</v>", "Hi") }).EditedCuePositions);
    }

    [Fact]
    public void An_added_cue_at_the_end_of_a_file_without_a_final_newline_gets_a_blank_line_before_it()
    {
        VttDocument doc = Parse(Fixtures.Utf8("WEBVTT\n\n00:01.000 --> 00:02.000\nOne"));

        byte[] edited = doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("a", 5000, 6000, "Two") }).Content.ToArray();

        Assert.Equal("WEBVTT\n\n00:01.000 --> 00:02.000\nOne\n\n00:00:05.000 --> 00:00:06.000\nTwo", Encoding.UTF8.GetString(edited));
    }

    [Theory]
    [InlineData("a --> b")]
    [InlineData("first\n00:05.000 --> 00:06.000")]
    public void Text_with_an_arrow_is_refused(string text)
    {
        VttDocument doc = Parse(Fixtures.Utf8(Rich));

        var ex = Assert.Throws<SubtitleEditException>(() => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(4, "Last line", text) }));
        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
        Assert.Equal(4, ex.CuePosition);
    }

    [Fact]
    public void Shared_rules_apply_blank_lines_mismatches_missing_cues_and_bad_times()
    {
        VttDocument doc = Parse(Fixtures.Utf8(Rich));

        Assert.Equal(SubtitleEditError.InvalidEdit, Refusal(doc, new SubtitleTextEdit(4, "Last line", "a\n\nb")).Error);
        Assert.Equal(SubtitleEditError.TextMismatch, Refusal(doc, new SubtitleTextEdit(4, "Other", "b")).Error);
        Assert.Equal(SubtitleEditError.CueNotFound, Refusal(doc, new SubtitleTextEdit(9, "x", "b")).Error);
        SubtitleEditException shortCue = Refusal(doc, new SubtitleAddCue("c9", 1000, 1050, "x"));
        Assert.Equal(SubtitleEditError.InvalidEdit, shortCue.Error);
        Assert.Equal("c9", shortCue.ClientId);
        Assert.Equal("c8", Refusal(doc, new SubtitleAddCue("c8", 1000, 2000, "x --> y")).ClientId);
        Assert.Equal(SubtitleEditError.InvalidEdit, Assert.Throws<SubtitleEditException>(() => doc.Apply(new SubtitleEdit[] { new SubtitleAddCue("r", 1000, 3000, "x") }, runtimeMs: 2000)).Error);
    }

    [Fact]
    public void Characters_the_encoding_cannot_hold_are_refused()
    {
        string body = string.Concat(Fixtures.Polish.Select((t, i) => $"\n00:{i + 10}.000 --> 00:{i + 10}.900\n{t}\n"));
        VttDocument doc = Parse(Fixtures.Windows1250("WEBVTT\n" + body));
        Assert.Equal("windows-1250", doc.Encoding.Name);

        var ex = Assert.Throws<SubtitleEditException>(() => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, doc.Cues[0].Text, "日本") }));
        Assert.Equal(SubtitleEditError.Unrepresentable, ex.Error);
    }

    [Theory]
    [InlineData("00:01.000 --> 00:02.000\nNo signature\n", "WEBVTT")]
    [InlineData("WEBVTTX\n\n00:01.000 --> 00:02.000\nx\n", "WEBVTT")]
    [InlineData("WEBVTT\n\nstray text\n\n00:01.000 --> 00:02.000\nx\n", "line 3")]
    [InlineData("WEBVTT\n\nNOTE only a note\n", "no cues")]
    [InlineData("", "empty")]
    public void Files_that_cannot_be_read_reliably_are_not_editable(string content, string reason)
    {
        VttParseResult result = VttDocument.Parse(Fixtures.Utf8(content));

        Assert.Null(result.Document);
        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, 0L, "changed")]
    [InlineData(1L, 0L, null)]
    [InlineData(0L, 1L, null)]
    public void The_self_check_refuses_a_result_whose_cue_reads_back_differently(long startShift, long endShift, string? text)
    {
        byte[] data = Fixtures.Utf8("WEBVTT\n\n00:01.000 --> 00:02.000\nOne\n\n00:03.000 --> 00:04.000\nTwo\n");
        VttDocument doc = Parse(data);
        var expected = doc.Cues.Select(c => (c.StartMs, c.EndMs, c.Text)).ToList();
        VttEditor.Verify(doc, data, expected);
        expected[1] = (expected[1].StartMs + startShift, expected[1].EndMs + endShift, text ?? expected[1].Text);

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(() => VttEditor.Verify(doc, data, expected));

        Assert.Equal(SubtitleEditError.VerificationFailed, ex.Error);
        Assert.Equal(2, ex.CuePosition);
    }

    private static SubtitleEditException Refusal(VttDocument doc, SubtitleEdit edit) =>
        Assert.Throws<SubtitleEditException>(() => doc.Apply(new[] { edit }));

    private static VttDocument Parse(byte[] data, string name = "file")
    {
        VttParseResult result = VttDocument.Parse(data);
        Assert.True(result.Document is not null, name + ": " + result.FailureReason);
        return result.Document!;
    }
}
