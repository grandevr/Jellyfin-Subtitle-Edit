using System;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Srt;
using UtfUnknown;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtEncodingTests
{
    [Fact]
    public void Utf8_bom_is_detected_with_its_length()
    {
        SrtEncoding? enc = SrtEncoding.Detect(Fixtures.Utf8("1\n00:00:01,000 --> 00:00:02,000\nHi\n", bom: true));

        Assert.NotNull(enc);
        Assert.Equal("utf-8", enc.Name);
        Assert.Equal(3, enc.BomLength);
    }

    [Fact]
    public void Utf16_boms_pick_the_byte_order()
    {
        SrtEncoding? le = SrtEncoding.Detect(Fixtures.Utf16Le("1\n00:00:01,000 --> 00:00:02,000\nHi\n"));
        SrtEncoding? be = SrtEncoding.Detect(Fixtures.Utf16Be("1\n00:00:01,000 --> 00:00:02,000\nHi\n"));

        Assert.Equal("utf-16le", le?.Name);
        Assert.Equal("utf-16be", be?.Name);
        Assert.Equal(2, le?.BomLength);
        Assert.Equal(2, be?.BomLength);
    }

    [Fact]
    public void Ascii_is_treated_as_utf8_like_jellyfin_does()
    {
        SrtEncoding? enc = SrtEncoding.Detect(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English))));

        Assert.Equal("utf-8", enc?.Name);
        Assert.Equal(0, enc?.BomLength);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Windows_1250_text_is_decoded_back_to_the_same_characters(bool polish)
    {
        string[] texts = polish ? Fixtures.Polish : Fixtures.Czech;
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(texts)));

        SrtEncoding? enc = SrtEncoding.Detect(data);

        Assert.NotNull(enc);
        Assert.Equal(Fixtures.Render(Fixtures.Cues(texts)), enc.Encoding.GetString(data));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detection_agrees_with_the_detector_jellyfin_uses(bool polish)
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(polish ? Fixtures.Polish : Fixtures.Czech)));

        SrtEncoding? ours = SrtEncoding.Detect(data);
        DetectionDetail? jellyfin = CharsetDetector.DetectFromBytes(data).Detected;

        Assert.NotNull(ours);
        Assert.NotNull(jellyfin);
        Assert.Equal(jellyfin.EncodingName.ToLowerInvariant(), ours.Name);
    }

    [Fact]
    public void The_strict_encoding_throws_instead_of_substituting()
    {
        SrtEncoding enc = SrtEncoding.Detect(Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish))))!;

        Assert.Throws<EncoderFallbackException>(() => enc.Encoding.GetBytes("Привет"));
    }

    [Fact]
    public void Utf32_boms_are_not_supported()
    {
        byte[] le = { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00 };
        byte[] be = { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41 };

        Assert.Null(SrtEncoding.Detect(le));
        Assert.Null(SrtEncoding.Detect(be));
    }

    [Fact]
    public void The_code_pages_provider_can_be_registered_repeatedly()
    {
        EncodingSupport.EnsureCodePages();
        EncodingSupport.EnsureCodePages();

        Assert.Equal(1250, Encoding.GetEncoding(1250).CodePage);
    }

    [Fact]
    public void Detect_does_not_modify_its_input()
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish)));
        byte[] copy = data.ToArray();

        _ = SrtEncoding.Detect(data);

        Assert.Equal(copy, data);
    }
}
