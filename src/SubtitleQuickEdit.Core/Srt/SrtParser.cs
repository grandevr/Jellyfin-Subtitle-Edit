using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// Lenient .srt parser. It works on the decoded text but records, for every part of every cue, where it
/// sits in the original bytes.
/// </summary>
internal static class SrtParser
{
    private enum LineKind
    {
        Blank,
        Digits,
        Time,
        Other,
    }

    internal static SrtParseResult Parse(byte[] data)
    {
        if (data.Length == 0)
        {
            return SrtParseResult.Fail("The file is empty.");
        }

        if (data.Length > SubtitleLimits.MaxFileBytes)
        {
            return SrtParseResult.Fail("The file is too large to edit.");
        }

        SubtitleEncoding? encoding = SubtitleEncoding.Detect(data);
        if (encoding is null)
        {
            return SrtParseResult.Fail("The text encoding could not be determined or is not supported.");
        }

        ReadOnlySpan<byte> body = data.AsSpan(encoding.BomLength);
        string text;
        try
        {
            text = encoding.Encoding.GetString(body);
            byte[] again = encoding.Encoding.GetBytes(text);
            if (!again.AsSpan().SequenceEqual(body))
            {
                return SrtParseResult.Fail("The file does not survive a decode and re-encode in its detected encoding (" + encoding.Name + ").");
            }
        }
        catch (DecoderFallbackException)
        {
            return SrtParseResult.Fail("The file contains bytes that are not valid in its detected encoding (" + encoding.Name + ").");
        }
        catch (EncoderFallbackException)
        {
            return SrtParseResult.Fail("The file does not survive a decode and re-encode in its detected encoding (" + encoding.Name + ").");
        }

        int[] byteOffsets = BuildByteOffsets(text, encoding.Encoding);
        if (byteOffsets[text.Length] != body.Length)
        {
            return SrtParseResult.Fail("The file's byte layout could not be mapped reliably.");
        }

        List<Line> lines = ScanLines(text, out string lineEnding);
        LineKind[] kinds = new LineKind[lines.Count];
        long[] starts = new long[lines.Count];
        long[] ends = new long[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            kinds[i] = Classify(text, lines[i], out starts[i], out ends[i]);
        }

        var cues = new List<SubtitleCue>();
        int index = 0;
        int lastContentLine = -1;
        while (index < lines.Count)
        {
            if (kinds[index] == LineKind.Blank)
            {
                index++;
                continue;
            }

            int numberLine = -1;
            int timeLine;
            if (kinds[index] == LineKind.Time)
            {
                timeLine = index;
            }
            else if (kinds[index] == LineKind.Digits && index + 1 < lines.Count && kinds[index + 1] == LineKind.Time)
            {
                numberLine = index;
                timeLine = index + 1;
            }
            else
            {
                return SrtParseResult.Fail(
                    string.Create(CultureInfo.InvariantCulture, $"Unexpected text on line {index + 1}, outside any cue."));
            }

            int end = timeLine + 1;
            while (end < lines.Count && kinds[end] != LineKind.Blank && !StartsCue(kinds, end))
            {
                end++;
            }

            bool precededByBlank = cues.Count == 0 || (index > 0 && kinds[index - 1] == LineKind.Blank);
            Line time = lines[timeLine];
            bool timeTerminated = time.TermEnd > time.End;
            bool textEmpty = end == timeLine + 1;
            int textStart;
            int textEnd;
            string cueText;
            if (textEmpty)
            {
                textStart = timeTerminated ? time.TermEnd : time.End;
                textEnd = textStart;
                cueText = string.Empty;
            }
            else
            {
                textStart = lines[timeLine + 1].Start;
                textEnd = lines[end - 1].End;
                cueText = SubtitleText.NormalizeBreaks(text.Substring(textStart, textEnd - textStart));
            }

            int numberStart = -1;
            int numberEnd = -1;
            int? number = null;
            if (numberLine >= 0)
            {
                Line nl = lines[numberLine];
                int a = nl.Start;
                int b = nl.End;
                while (a < b && char.IsWhiteSpace(text[a]))
                {
                    a++;
                }

                while (b > a && char.IsWhiteSpace(text[b - 1]))
                {
                    b--;
                }

                numberStart = a;
                numberEnd = b;
                if (int.TryParse(text.AsSpan(a, b - a), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                {
                    number = parsed;
                }
            }

            cues.Add(new SubtitleCue
            {
                Position = cues.Count + 1,
                Number = number,
                StartMs = starts[timeLine],
                EndMs = ends[timeLine],
                Text = cueText,
                Layout = new CueLayout(
                    BlockStart: numberLine >= 0 ? lines[numberLine].Start : time.Start,
                    NumberStart: numberStart,
                    NumberEnd: numberEnd,
                    TimeLineStart: time.Start,
                    TextStart: textStart,
                    TextEnd: textEnd,
                    TextEmpty: textEmpty,
                    TimeLineTerminated: timeTerminated,
                    PrecededByBlankLine: precededByBlank),
            });
            lastContentLine = end - 1;
            index = end;
        }

        if (cues.Count == 0)
        {
            return SrtParseResult.Fail("The file contains no cues.");
        }

        int tail = 0;
        if (lines[lastContentLine].TermEnd > lines[lastContentLine].End)
        {
            tail++;
        }

        for (int i = lastContentLine + 1; i < lines.Count; i++)
        {
            if (lines[i].TermEnd > lines[i].End)
            {
                tail++;
            }
        }

        return SrtParseResult.Ok(SrtDocument.Create(data, encoding, lineEnding, cues, byteOffsets, tail));
    }

    /// <summary>
    /// Replaces any line break style with a single <c>\n</c>.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    internal static string NormalizeBreaks(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool StartsCue(LineKind[] kinds, int i) =>
        kinds[i] == LineKind.Time
        || (kinds[i] == LineKind.Digits && i + 1 < kinds.Length && kinds[i + 1] == LineKind.Time);

    private static LineKind Classify(string text, Line line, out long startMs, out long endMs)
    {
        startMs = 0;
        endMs = 0;
        ReadOnlySpan<char> span = text.AsSpan(line.Start, line.End - line.Start);
        if (span.IsWhiteSpace())
        {
            return LineKind.Blank;
        }

        ReadOnlySpan<char> trimmed = span.Trim();
        bool digits = true;
        foreach (char c in trimmed)
        {
            if (c is < '0' or > '9')
            {
                digits = false;
                break;
            }
        }

        if (digits)
        {
            return LineKind.Digits;
        }

        if (span.Contains("-->", StringComparison.Ordinal))
        {
            Match match = SrtPatterns.TimeLine().Match(text, line.Start, line.End - line.Start);
            if (match.Success)
            {
                startMs = ToMs(match, 1);
                endMs = ToMs(match, 5);
                return LineKind.Time;
            }
        }

        return LineKind.Other;
    }

    private static long ToMs(Match match, int firstGroup)
    {
        long h = long.Parse(match.Groups[firstGroup].Value, CultureInfo.InvariantCulture);
        long m = long.Parse(match.Groups[firstGroup + 1].Value, CultureInfo.InvariantCulture);
        long s = long.Parse(match.Groups[firstGroup + 2].Value, CultureInfo.InvariantCulture);
        string fraction = match.Groups[firstGroup + 3].Value.PadRight(3, '0');
        long ms = long.Parse(fraction, CultureInfo.InvariantCulture);
        return (h * 3_600_000) + (m * 60_000) + (s * 1000) + ms;
    }

    private static List<Line> ScanLines(string text, out string lineEnding)
    {
        var lines = new List<Line>();
        int crlf = 0;
        int lf = 0;
        int cr = 0;
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] != '\r' && text[i] != '\n')
            {
                i++;
            }

            int end = i;
            if (i < text.Length)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i += 2;
                    crlf++;
                }
                else if (text[i] == '\r')
                {
                    i++;
                    cr++;
                }
                else
                {
                    i++;
                    lf++;
                }
            }

            lines.Add(new Line(start, end, i));
        }

        if (crlf > lf && crlf >= cr)
        {
            lineEnding = "\r\n";
        }
        else if (cr > lf && cr > crlf)
        {
            lineEnding = "\r";
        }
        else
        {
            lineEnding = "\n";
        }

        return lines;
    }

    private static int[] BuildByteOffsets(string text, Encoding encoding)
    {
        bool utf8 = encoding is UTF8Encoding;
        bool utf16 = encoding is UnicodeEncoding;
        bool singleByte = encoding.IsSingleByte;
        var map = new int[text.Length + 1];
        int position = 0;
        int i = 0;
        while (i < text.Length)
        {
            map[i] = position;
            int chars = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            int bytes;
            if (utf16)
            {
                bytes = 2 * chars;
            }
            else if (singleByte)
            {
                bytes = 1;
            }
            else if (utf8)
            {
                char c = text[i];
                bytes = chars == 2 ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            }
            else
            {
                bytes = encoding.GetByteCount(text.AsSpan(i, chars));
            }

            if (chars == 2)
            {
                map[i + 1] = position;
            }

            position += bytes;
            i += chars;
        }

        map[text.Length] = position;
        return map;
    }

    private readonly record struct Line(int Start, int End, int TermEnd);
}
