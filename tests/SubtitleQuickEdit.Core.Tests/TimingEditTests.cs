using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Ass;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Submissions;
using SubtitleQuickEdit.Core.Subtitles;
using SubtitleQuickEdit.Core.Vtt;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class TimingEditTests
{
    public static IEnumerable<object[]> AllFixtures() => Fixtures.All().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void New_times_change_only_the_times_on_that_cues_time_line(Fixtures.Fixture fixture)
    {
        SrtDocument doc = SrtDocument.Parse(fixture.Data).Document!;
        foreach (int pos in new[] { 1, doc.Cues.Count / 2, doc.Cues.Count })
        {
            SubtitleCue cue = doc.Cues[pos - 1];

            SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { Retime(pos, cue.Text, cue.Text, cue.StartMs + 250, cue.EndMs - 500) });

            byte[] after = result.Content.ToArray();
            int from = doc.ByteOffset(cue.Layout.TimesStart);
            int to = doc.ByteOffset(cue.Layout.TimesEnd);
            Assert.Equal(fixture.Data[..from], after[..from]);
            Assert.Equal(fixture.Data[to..], after[^(fixture.Data.Length - to)..]);
            SrtDocument read = SrtDocument.Parse(after).Document!;
            Assert.Equal(cue.StartMs + 250, read.Cues[pos - 1].StartMs);
            Assert.Equal(cue.EndMs - 500, read.Cues[pos - 1].EndMs);
            Assert.Equal(cue.Text, read.Cues[pos - 1].Text);
            Assert.Equal(new[] { pos }, result.EditedCuePositions);
        }
    }

    [Fact]
    public void A_line_that_runs_to_the_end_of_the_episode_can_be_cut_short_and_its_text_changed_at_once()
    {
        string srt = "538\n00:24:25,000 --> 00:24:27,000\nWait.\n\n539\n00:24:27,870 --> 00:48:55,717 X1:10 X2:20\nNo.\n\n540\n00:24:27,871 --> 00:24:28,430\nNow.\n";
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(srt)).Document!;

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { Retime(2, "No.", "No!", 1_467_870, 1_468_400) }, runtimeMs: 1_500_000);

        Assert.Equal(
            "538\n00:24:25,000 --> 00:24:27,000\nWait.\n\n539\n00:24:27,870 --> 00:24:28,400 X1:10 X2:20\nNo!\n\n540\n00:24:27,871 --> 00:24:28,430\nNow.\n",
            Encoding.UTF8.GetString(result.Content.Span));
    }

    [Fact]
    public void A_line_keeps_its_place_when_its_new_start_is_after_the_next_line()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { Retime(1, doc.Cues[0].Text, doc.Cues[0].Text, 20_000, 21_000) });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(doc.Cues[0].Text, after.Cues[0].Text);
        Assert.Equal(20_000, after.Cues[0].StartMs);
        Assert.Equal(1, after.Cues[0].Number);
    }

    [Fact]
    public void New_times_and_added_lines_go_together_with_renumbering_in_a_file_without_numbers()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English), numbers: false));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[]
        {
            Retime(2, doc.Cues[1].Text, doc.Cues[1].Text, 6400, 9000),
            new SubtitleAddCue("n1", 100, 900, "First"),
        });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal("First", after.Cues[0].Text);
        Assert.Equal(6400, after.Cues[2].StartMs);
        Assert.Equal(9000, after.Cues[2].EndMs);
        Assert.Equal(3, after.Cues[2].Number);
        Assert.Equal(new[] { 3 }, result.EditedCuePositions);
    }

    [Fact]
    public void Times_given_unchanged_write_nothing_new()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        SubtitleCue cue = doc.Cues[2];

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { Retime(3, cue.Text, cue.Text, cue.StartMs, cue.EndMs) });

        Assert.Equal(data, result.Content.ToArray());
    }

    [Theory]
    [InlineData(-1L, 1000L, "A line cannot start before 00:00:00,000.")]
    [InlineData(5000L, 5000L, "A line must end after it starts.")]
    [InlineData(5000L, 4000L, "A line must end after it starts.")]
    [InlineData(5000L, 5050L, "A line must be at least 100 ms long.")]
    [InlineData(5000L, 700_000L, "A line must not end after the end of the video.")]
    [InlineData(5000L, 360_000_000L, "A line ends after 99:59:59,999, which an .srt file cannot hold.")]
    public void Bad_new_times_are_refused_and_name_the_line(long start, long end, string message)
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)))).Document!;

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { Retime(4, doc.Cues[3].Text, doc.Cues[3].Text, start, end) }, runtimeMs: 600_000));

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
        Assert.Equal(message, ex.Message);
        Assert.Equal(4, ex.CuePosition);
    }

    [Fact]
    public void A_start_without_an_end_is_refused()
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)))).Document!;

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { new SubtitleTextEdit(1, doc.Cues[0].Text, doc.Cues[0].Text) { NewStartMs = 100 } }));

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
        Assert.Equal(1, ex.CuePosition);
    }

    [Fact]
    public void Vtt_new_times_keep_the_cue_settings_and_identifier()
    {
        string vtt = "WEBVTT\n\nintro\n00:01.000 --> 00:03.000 align:start line:0\n<v Anna>Hello.</v>\n\n00:00:04.000 --> 00:00:06.500\nBye.\n";
        VttDocument doc = VttDocument.Parse(Fixtures.Utf8(vtt)).Document!;

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { Retime(1, "<v Anna>Hello.</v>", "<v Anna>Hello.</v>", 1500, 2750) });

        Assert.Equal(
            "WEBVTT\n\nintro\n00:00:01.500 --> 00:00:02.750 align:start line:0\n<v Anna>Hello.</v>\n\n00:00:04.000 --> 00:00:06.500\nBye.\n",
            Encoding.UTF8.GetString(result.Content.Span));
    }

    [Fact]
    public void Vtt_bad_new_times_are_refused()
    {
        VttDocument doc = VttDocument.Parse(Fixtures.Utf8("WEBVTT\n\n00:01.000 --> 00:03.000\nHello.\n")).Document!;

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(
            () => doc.Apply(new SubtitleEdit[] { Retime(1, "Hello.", "Hello.", 3000, 2000) }));

        Assert.Equal("A line must end after it starts.", ex.Message);
    }

    [Fact]
    public void Ass_new_times_change_only_the_start_and_end_fields_rounded_to_centiseconds()
    {
        string ass = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:03.50,Default,Anna,0,0,0,,{\\an8}Hello, there.\n"
            + "Dialogue: 1, 0:00:04.00 ,0:48:55.71,Sign,,0,0,0,,No.\n";
        AssDocument doc = AssDocument.Parse(Fixtures.Utf8(ass)).Document!;

        SubtitleApplyResult result = doc.Apply(new SubtitleEdit[] { Retime(2, "No.", "No.", 4004, 5126) }, runtimeMs: 1_500_000);

        Assert.Equal(
            ass.Replace("Dialogue: 1, 0:00:04.00 ,0:48:55.71,", "Dialogue: 1, 0:00:04.00 ,0:00:05.13,", StringComparison.Ordinal),
            Encoding.UTF8.GetString(result.Content.Span));
    }

    [Fact]
    public void Ass_drawings_cannot_be_retimed_here()
    {
        string ass = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:05.00,0:00:05.80,Sign,,0,0,0,,{\\p1}m 0 0 l 100 0 100 100{\\p0}\n";
        AssDocument doc = AssDocument.Parse(Fixtures.Utf8(ass)).Document!;
        string text = doc.Cues[0].Text;

        SubtitleEditException ex = Assert.Throws<SubtitleEditException>(() => doc.Apply(new SubtitleEdit[] { Retime(1, text, text, 5000, 6000) }));

        Assert.Equal(SubtitleEditError.InvalidEdit, ex.Error);
    }

    [Fact]
    public void A_submitted_retime_is_written_on_approval_and_is_a_conflict_when_the_line_was_retimed_since()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "movie.en.srt");
        File.WriteAllBytes(path, Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English))));
        var saver = new SubtitleSaveService(new SafeFileWriter(null, null), new BackupStore(Path.Combine(dir.Root, "backups"), BackupMode.OriginalOnly, 3));
        var service = new SubmissionService(new SubmissionStore(Path.Combine(dir.Root, "submissions")), saver, TimeProvider.System);
        string Hash() => SubtitleHash.Compute(File.ReadAllBytes(path));
        SrtDocument Doc() => SrtDocument.Parse(File.ReadAllBytes(path)).Document!;
        Submission Submit(SubtitleEdit edit) => service.Submit(new SubmitRequest(
            "0a1b2c3d4e5f60718293a4b5c6d7e8f9", "user", "55de67aa9c161f4100fce9da37b4b0cb", 3, path, Hash(), new[] { edit }, null, 600_000, 50));
        string one = Fixtures.English[0];
        string two = Fixtures.English[1];

        Submission first = Submit(Retime(1, one, one, 2100, 4000));
        SubmittedEdit stored = Assert.Single(first.Edits);
        Assert.Equal((2000L, 5000L, 2100L, 4000L), (stored.CueStartMs, stored.CueEndMs, stored.NewStartMs, stored.NewEndMs));
        Submission second = Submit(Retime(2, two, two, 6500, 8000));
        saver.Save(new SaveRequest("55de67aa9c161f4100fce9da37b4b0cb", 3, path, Hash(), new SubtitleEdit[] { Retime(2, two, two, 6600, 9000) }, null));

        ApproveResult approved = service.Approve(new ApproveRequest(first.Id, path, "admin", null, 600_000));
        ApproveResult conflict = service.Approve(new ApproveRequest(second.Id, path, "admin", null, 600_000));

        Assert.Equal(SubmissionState.Approved, approved.Submission.State);
        Assert.Equal((2100L, 4000L), (Doc().Cues[0].StartMs, Doc().Cues[0].EndMs));
        Assert.Equal(EditOutcome.Conflict, Assert.Single(conflict.Submission.Edits).Outcome);
        Assert.Equal((6600L, 9000L), (Doc().Cues[1].StartMs, Doc().Cues[1].EndMs));
    }

    private static SubtitleTextEdit Retime(int position, string oldText, string newText, long start, long end) =>
        new(position, oldText, newText) { NewStartMs = start, NewEndMs = end };
}
