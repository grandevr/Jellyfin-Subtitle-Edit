using System;
using System.Collections.Generic;
using System.Globalization;
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
        DecodedText? decoded = DecodedText.Decode(data, out string? failure);
        if (decoded is null)
        {
            return SrtParseResult.Fail(failure!);
        }

        string text = decoded.Text;
        SubtitleEncoding encoding = decoded.Encoding;
        IReadOnlyList<TextLine> lines = decoded.Lines;
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
            TextLine time = lines[timeLine];
            Match times = SrtPatterns.TimeLine().Match(text, time.Start, time.End - time.Start);
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
                TextLine nl = lines[numberLine];
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
                    PrecededByBlankLine: precededByBlank,
                    TimesStart: times.Groups[1].Index,
                    TimesEnd: times.Groups[8].Index + times.Groups[8].Length),
            });
            lastContentLine = end - 1;
            index = end;
        }

        if (cues.Count == 0)
        {
            return SrtParseResult.Fail("The file contains no cues.");
        }

        int tail = decoded.TailTerminators(lastContentLine);
        return SrtParseResult.Ok(SrtDocument.Create(data, encoding, decoded.LineEnding, cues, decoded.ByteOffsets, tail));
    }

    private static bool StartsCue(LineKind[] kinds, int i) =>
        kinds[i] == LineKind.Time
        || (kinds[i] == LineKind.Digits && i + 1 < kinds.Length && kinds[i + 1] == LineKind.Time);

    private static LineKind Classify(string text, TextLine line, out long startMs, out long endMs)
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
}
