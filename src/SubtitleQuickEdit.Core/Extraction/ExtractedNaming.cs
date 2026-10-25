using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// Names the file an embedded subtitle track is extracted to, the way Jellyfin finds external subtitles:
/// <c>{video name}.{language}[.default][.forced][.sdh].{ext}</c> in the video's folder.
/// </summary>
public static class ExtractedNaming
{
    private const int MaxFileNameBytes = 255;

    // ISO 639-2 codes (both the B and the T forms) of the languages that have an ISO 639-1 code, for the
    // languages a library is likely to hold. A three-letter code that is not here is kept as it is; Jellyfin
    // reads those too.
    private static readonly FrozenDictionary<string, string> TwoLetter = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["afr"] = "af", ["alb"] = "sq", ["sqi"] = "sq", ["ara"] = "ar", ["arm"] = "hy", ["hye"] = "hy",
        ["aze"] = "az", ["baq"] = "eu", ["eus"] = "eu", ["bel"] = "be", ["ben"] = "bn", ["bos"] = "bs",
        ["bul"] = "bg", ["bur"] = "my", ["mya"] = "my", ["cat"] = "ca", ["chi"] = "zh", ["zho"] = "zh",
        ["cze"] = "cs", ["ces"] = "cs", ["dan"] = "da", ["dut"] = "nl", ["nld"] = "nl", ["eng"] = "en",
        ["epo"] = "eo", ["est"] = "et", ["fin"] = "fi", ["fre"] = "fr", ["fra"] = "fr", ["geo"] = "ka",
        ["kat"] = "ka", ["ger"] = "de", ["deu"] = "de", ["gle"] = "ga", ["glg"] = "gl", ["gre"] = "el",
        ["ell"] = "el", ["guj"] = "gu", ["heb"] = "he", ["hin"] = "hi", ["hrv"] = "hr", ["hun"] = "hu",
        ["ice"] = "is", ["isl"] = "is", ["ind"] = "id", ["ita"] = "it", ["jpn"] = "ja", ["kan"] = "kn",
        ["kaz"] = "kk", ["khm"] = "km", ["kor"] = "ko", ["lao"] = "lo", ["lat"] = "la", ["lav"] = "lv",
        ["lit"] = "lt", ["ltz"] = "lb", ["mac"] = "mk", ["mkd"] = "mk", ["mal"] = "ml", ["mar"] = "mr",
        ["may"] = "ms", ["msa"] = "ms", ["mlt"] = "mt", ["mon"] = "mn", ["nep"] = "ne", ["nob"] = "nb",
        ["nno"] = "nn", ["nor"] = "no", ["pan"] = "pa", ["per"] = "fa", ["fas"] = "fa", ["pol"] = "pl",
        ["por"] = "pt", ["rum"] = "ro", ["ron"] = "ro", ["rus"] = "ru", ["sin"] = "si", ["slo"] = "sk",
        ["slk"] = "sk", ["slv"] = "sl", ["spa"] = "es", ["srp"] = "sr", ["swa"] = "sw", ["swe"] = "sv",
        ["tam"] = "ta", ["tel"] = "te", ["tgl"] = "tl", ["tha"] = "th", ["tur"] = "tr", ["ukr"] = "uk",
        ["urd"] = "ur", ["uzb"] = "uz", ["vie"] = "vi", ["wel"] = "cy", ["cym"] = "cy",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // "Undetermined", "multiple", "no linguistic content" and "uncoded": no language goes in the name.
    private static readonly FrozenSet<string> NoLanguage = new[] { "und", "mul", "zxx", "mis" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Turns a track's language into the code for the file name: the two-letter code when there is one,
    /// otherwise the track's own three-letter code.
    /// </summary>
    /// <param name="language">The track's language as Jellyfin reports it (<c>eng</c>, <c>ger</c>, <c>en</c>, <c>pt-BR</c>, ...), or null.</param>
    /// <returns>The code, lower case, or null when the track has no usable language.</returns>
    public static string? LanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string code = language.Trim().ToLowerInvariant();
        int dash = code.IndexOfAny(['-', '_']);
        if (dash >= 0)
        {
            code = code[..dash];
        }

        if (code.Length is < 2 or > 3 || !IsAsciiLetters(code) || NoLanguage.Contains(code))
        {
            return null;
        }

        return code.Length == 3 && TwoLetter.TryGetValue(code, out string? two) ? two : code;
    }

    /// <summary>
    /// Gets the extension the extracted file gets, from the file Jellyfin extracted the track to: ASS and SSA
    /// are kept as they are, SubRip and WebVTT keep theirs.
    /// </summary>
    /// <param name="extractedPath">The file Jellyfin extracted the track to.</param>
    /// <returns><c>.ass</c>, <c>.ssa</c>, <c>.srt</c> or <c>.vtt</c>, or null for any other kind of file.</returns>
    public static string? Extension(string extractedPath)
    {
        ArgumentNullException.ThrowIfNull(extractedPath);
        string extension = Path.GetExtension(extractedPath).ToLowerInvariant();
        return extension is ".ass" or ".ssa" or ".srt" or ".vtt" ? extension : null;
    }

    /// <summary>
    /// Builds the path of the extracted file, next to the video.
    /// </summary>
    /// <param name="videoPath">The video file.</param>
    /// <param name="language">The track's language, or null.</param>
    /// <param name="disposition">The track's flags.</param>
    /// <param name="extension">The extension from <see cref="Extension"/>.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">The extension is not one <see cref="Extension"/> gives, or the name would be too long.</exception>
    public static string TargetPath(string videoPath, string? language, TrackDisposition disposition, string extension)
    {
        ArgumentNullException.ThrowIfNull(videoPath);
        ArgumentNullException.ThrowIfNull(extension);
        if (extension is not (".ass" or ".ssa" or ".srt" or ".vtt"))
        {
            throw new ArgumentException("The extension must be .ass, .ssa, .srt or .vtt.", nameof(extension));
        }

        var name = new StringBuilder(Path.GetFileNameWithoutExtension(videoPath));
        string? code = LanguageCode(language);
        if (code is not null)
        {
            name.Append('.').Append(code);
        }

        if (disposition.HasFlag(TrackDisposition.Default))
        {
            name.Append(".default");
        }

        if (disposition.HasFlag(TrackDisposition.Forced))
        {
            name.Append(".forced");
        }

        if (disposition.HasFlag(TrackDisposition.HearingImpaired))
        {
            name.Append(".sdh");
        }

        name.Append(extension);
        string file = name.ToString();
        if (Encoding.UTF8.GetByteCount(file) > MaxFileNameBytes)
        {
            throw new ArgumentException("The video's file name is too long to add a subtitle name to it.", nameof(videoPath));
        }

        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(videoPath))!, file);
    }

    private static bool IsAsciiLetters(string code)
    {
        foreach (char c in code)
        {
            if (c is < 'a' or > 'z')
            {
                return false;
            }
        }

        return true;
    }
}
