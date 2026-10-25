using System;
using System.IO;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtTrackInspectorTests
{
    [Fact]
    public void A_good_srt_is_editable_and_parsed()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Content.Srt("x"));

        TrackInspection result = SubtitleTrackInspector.Inspect(path);

        Assert.True(result.Editable);
        Assert.Null(result.Reason);
        Assert.Equal(3, result.Document!.Cues.Count);
    }

    [Theory]
    [InlineData("movie.en.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n", SubtitleFormat.WebVtt)]
    [InlineData("movie.en.ass", "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello\n", SubtitleFormat.Ass)]
    [InlineData("MOVIE.EN.SSA", "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello\n", SubtitleFormat.Ass)]
    public void Vtt_and_ass_files_are_editable_and_parsed(string name, string content, SubtitleFormat format)
    {
        using var dir = new TempDir();
        string path = dir.Write(name, System.Text.Encoding.UTF8.GetBytes(content));

        TrackInspection result = SubtitleTrackInspector.Inspect(path);

        Assert.True(result.Editable, result.Reason);
        Assert.Equal(format, result.Document!.Format);
        Assert.Equal("Hello", Assert.Single(result.Document.Cues).Text);
    }

    [Theory]
    [InlineData("movie.en.sub")]
    [InlineData("movie.srt.bak")]
    public void Other_formats_are_not_editable_and_say_so(string name)
    {
        using var dir = new TempDir();
        string path = dir.Write(name, Content.Srt("x"));

        TrackInspection result = SubtitleTrackInspector.Inspect(path);

        Assert.False(result.Editable);
        Assert.Contains("SubRip", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_extension_check_ignores_case()
    {
        using var dir = new TempDir();
        string path = dir.Write("MOVIE.EN.SRT", Content.Srt("x"));

        Assert.True(SubtitleTrackInspector.Inspect(path).Editable);
    }

    [Fact]
    public void A_missing_file_is_not_editable()
    {
        using var dir = new TempDir();

        TrackInspection result = SubtitleTrackInspector.Inspect(Path.Combine(dir.Root, "nope.srt"));

        Assert.False(result.Editable);
        Assert.Contains("does not exist", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_srt_carries_the_parsers_reason()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Fixtures.Utf8("this is not a subtitle file\n"));

        TrackInspection result = SubtitleTrackInspector.Inspect(path);

        Assert.False(result.Editable);
        Assert.Contains("outside any cue", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_is_not_editable_with_a_clear_reason()
    {
        if (UnixFileMetadata.CurrentUid() == 0)
        {
            Integration.SkippedBecause("running as root, which ignores file permissions");
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Content.Srt("x"), UnixFileMode.UserWrite);

        TrackInspection result = SubtitleTrackInspector.Inspect(path);

        Assert.False(result.Editable);
        Assert.Contains("cannot read", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_over_the_size_limit_is_not_editable()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "huge.srt");
        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength(SubtitleLimits.MaxFileBytes + 1);
        }

        TrackInspection result = SubtitleTrackInspector.Inspect(path);

        Assert.False(result.Editable);
        Assert.Contains("too large", result.Reason, StringComparison.Ordinal);
    }
}
