using System;
using System.Text;
using UtfUnknown;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// The encoding of a subtitle file: a strict .NET encoding (it throws on bytes or characters it cannot
/// handle instead of substituting) plus the length of the byte-order mark at the start of the file.
/// </summary>
public sealed class SubtitleEncoding
{
    private SubtitleEncoding(Encoding encoding, string name, int bomLength)
    {
        Encoding = encoding;
        Name = name;
        BomLength = bomLength;
    }

    /// <summary>
    /// Gets the strict encoding.
    /// </summary>
    public Encoding Encoding { get; }

    /// <summary>
    /// Gets the lower-case name, e.g. <c>utf-8</c>, <c>utf-16le</c>, <c>windows-1250</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the number of byte-order-mark bytes at the start of the file (0 when there is none).
    /// </summary>
    public int BomLength { get; }

    /// <summary>
    /// Detects the encoding of a subtitle file the way Jellyfin does: a byte-order mark wins, otherwise
    /// the UTF.Unknown detector decides. ASCII is treated as UTF-8, as Jellyfin does.
    /// </summary>
    /// <param name="data">The file content.</param>
    /// <returns>The encoding, or null when it cannot be determined or is not supported.</returns>
    public static SubtitleEncoding? Detect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        EncodingSupport.EnsureCodePages();

        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Utf8(3);
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0)
        {
            return null; // UTF-32 LE: not supported.
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF)
        {
            return null; // UTF-32 BE: not supported.
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return new SubtitleEncoding(new UnicodeEncoding(false, false, true), "utf-16le", 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return new SubtitleEncoding(new UnicodeEncoding(true, false, true), "utf-16be", 2);
        }

        DetectionDetail? detected = CharsetDetector.DetectFromBytes(data).Detected;
        if (detected is null)
        {
            return IsValidUtf8(data) ? Utf8(0) : null;
        }

        string name = detected.EncodingName.ToLowerInvariant();
        if (name is "utf-8" or "ascii" or "us-ascii")
        {
            return Utf8(0);
        }

        if (name == "utf-16le")
        {
            return new SubtitleEncoding(new UnicodeEncoding(false, false, true), name, 0);
        }

        if (name == "utf-16be")
        {
            return new SubtitleEncoding(new UnicodeEncoding(true, false, true), name, 0);
        }

        Encoding? encoding = detected.Encoding;
        if (encoding is null || encoding.CodePage is 12000 or 12001)
        {
            return null;
        }

        var strict = (Encoding)encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        return new SubtitleEncoding(strict, name, 0);
    }

    private static SubtitleEncoding Utf8(int bomLength) => new(new UTF8Encoding(false, true), "utf-8", bomLength);

    private static bool IsValidUtf8(byte[] data)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
