using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Vtt;

/// <summary>
/// Lenient WebVTT parser. It reads the WEBVTT header, NOTE, STYLE and REGION blocks and cues, and records where each
/// cue's parts sit in the original bytes.
/// </summary>
internal static class VttParser
{
    internal static VttParseResult Parse(byte[] data)
    {
        DecodedText? decoded = DecodedText.Decode(data, out string? failure);
        if (decoded is null)
        {
            return VttParseResult.Fail(failure!);
        }

        string text = decoded.Text;
        IReadOnlyList<TextLine> lines = decoded.Lines;
        if (lines.Count == 0 || !VttPatterns.Signature().IsMatch(decoded.Of(lines[0]).ToString()))
        {
            return VttParseResult.Fail("The file does not start with WEBVTT, so it is not a WebVTT file.");
        }

        // The header: the signature line and any lines after it, up to a blank line or the first timing line.
        int index = 1;
        while (index < lines.Count && !IsBlank(decoded, lines[index]) && !IsTiming(decoded, lines[index], out _, out _))
        {
            index++;
        }

        int lastContentLine = index - 1;
        var cues = new List<SubtitleCue>();
        while (index < lines.Count)
        {
            if (IsBlank(decoded, lines[index]))
            {
                index++;
                continue;
            }

            int idLine = -1;
            int timeLine;
            if (IsTiming(decoded, lines[index], out _, out _))
            {
                timeLine = index;
            }
            else if (index + 1 < lines.Count && IsTiming(decoded, lines[index + 1], out _, out _))
            {
                idLine = index;
                timeLine = index + 1;
            }
            else if (VttPatterns.OtherBlock().IsMatch(decoded.Of(lines[index]).ToString()))
            {
                while (index < lines.Count && !IsBlank(decoded, lines[index]))
                {
                    index++;
                }

                lastContentLine = index - 1;
                continue;
            }
            else
            {
                return VttParseResult.Fail(
                    string.Create(CultureInfo.InvariantCulture, $"Unexpected text on line {index + 1}, outside any cue."));
            }

            // The payload runs to a blank line; a timing line inside it starts the next cue, as in browsers.
            int end = timeLine + 1;
            while (end < lines.Count && !IsBlank(decoded, lines[end]) && !IsTiming(decoded, lines[end], out _, out _))
            {
                end++;
            }

            IsTiming(decoded, lines[timeLine], out long startMs, out long endMs);
            int blockStart = idLine >= 0 ? idLine : timeLine;
            bool precededByBlank = blockStart > 0 && IsBlank(decoded, lines[blockStart - 1]);
            TextLine time = lines[timeLine];
            Match times = VttPatterns.TimingLine().Match(text, time.Start, time.End - time.Start);
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

            cues.Add(new SubtitleCue
            {
                Position = cues.Count + 1,
                StartMs = startMs,
                EndMs = endMs,
                Text = cueText,
                Layout = new CueLayout(
                    BlockStart: lines[blockStart].Start,
                    NumberStart: -1,
                    NumberEnd: -1,
                    TimeLineStart: time.Start,
                    TextStart: textStart,
                    TextEnd: textEnd,
                    TextEmpty: textEmpty,
                    TimeLineTerminated: timeTerminated,
                    PrecededByBlankLine: precededByBlank,
                    TimesStart: times.Groups[1].Success ? times.Groups[1].Index : times.Groups[2].Index,
                    TimesEnd: times.Groups[8].Index + times.Groups[8].Length),
            });
            lastContentLine = end - 1;
            index = end;
        }

        if (cues.Count == 0)
        {
            return VttParseResult.Fail("The file contains no cues.");
        }

        return VttParseResult.Ok(VttDocument.Create(data, decoded, cues, decoded.TailTerminators(lastContentLine)));
    }

    private static bool IsBlank(DecodedText decoded, TextLine line) => decoded.Of(line).IsWhiteSpace();

    private static bool IsTiming(DecodedText decoded, TextLine line, out long startMs, out long endMs)
    {
        startMs = 0;
        endMs = 0;
        if (!decoded.Of(line).Contains("-->", StringComparison.Ordinal))
        {
            return false;
        }

        Match match = VttPatterns.TimingLine().Match(decoded.Text, line.Start, line.End - line.Start);
        if (!match.Success)
        {
            return false;
        }

        startMs = ToMs(match, 1);
        endMs = ToMs(match, 5);
        return true;
    }

    private static long ToMs(Match match, int firstGroup)
    {
        string hours = match.Groups[firstGroup].Value;
        long h = hours.Length == 0 ? 0 : long.Parse(hours, CultureInfo.InvariantCulture);
        long m = long.Parse(match.Groups[firstGroup + 1].Value, CultureInfo.InvariantCulture);
        long s = long.Parse(match.Groups[firstGroup + 2].Value, CultureInfo.InvariantCulture);
        long ms = long.Parse(match.Groups[firstGroup + 3].Value, CultureInfo.InvariantCulture);
        return (h * 3_600_000) + (m * 60_000) + (s * 1000) + ms;
    }
}
