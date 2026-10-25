using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SubtitleFormatsTests
{
    [Theory]
    [InlineData("/m/movie.en.srt", SubtitleFormat.Srt)]
    [InlineData("/m/MOVIE.EN.SRT", SubtitleFormat.Srt)]
    [InlineData("/m/movie.en.vtt", SubtitleFormat.WebVtt)]
    [InlineData("/m/movie.en.ass", SubtitleFormat.Ass)]
    [InlineData("/m/movie.en.SSA", SubtitleFormat.Ass)]
    public void Known_extensions_map_to_their_format(string path, SubtitleFormat expected)
    {
        Assert.Equal(expected, SubtitleFormats.FromPath(path));
    }

    [Theory]
    [InlineData("/m/movie.en.sub")]
    [InlineData("/m/movie.en.sup")]
    [InlineData("/m/movie.srt.bak")]
    [InlineData("/m/movie")]
    public void Other_extensions_have_no_format(string path)
    {
        Assert.Null(SubtitleFormats.FromPath(path));
        Assert.False(SubtitleFormats.IsEditable(path));
    }

    [Fact]
    public void Only_srt_is_editable_until_the_other_parsers_land()
    {
        Assert.True(SubtitleFormats.IsEditable(SubtitleFormat.Srt));
        Assert.False(SubtitleFormats.IsEditable(SubtitleFormat.WebVtt));
        Assert.False(SubtitleFormats.IsEditable(SubtitleFormat.Ass));
        Assert.True(SubtitleFormats.IsEditable("/m/a.srt"));
        Assert.False(SubtitleFormats.IsEditable("/m/a.vtt"));
    }

    [Theory]
    [InlineData(SubtitleFormat.Srt, "srt")]
    [InlineData(SubtitleFormat.WebVtt, "vtt")]
    [InlineData(SubtitleFormat.Ass, "ass")]
    public void Formats_have_short_api_names(SubtitleFormat format, string name)
    {
        Assert.Equal(name, SubtitleFormats.Name(format));
    }

    [Fact]
    public void A_parsed_srt_reports_its_format_and_content()
    {
        byte[] data = Content.Srt("Hello");
        ISubtitleDocument doc = Srt.SrtDocument.Parse(data).Document!;

        Assert.Equal(SubtitleFormat.Srt, doc.Format);
        Assert.Equal(data, doc.Content.ToArray());
        Assert.Equal(SubtitleHash.Compute(data), doc.Hash);
    }
}
