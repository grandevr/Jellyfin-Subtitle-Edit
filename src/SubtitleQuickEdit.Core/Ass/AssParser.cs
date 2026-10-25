using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Ass;

/// <summary>
/// ASS/SSA parser. It reads the [Events] section's Format line and Dialogue lines, and records where each line's
/// Text field sits in the original bytes. Everything else in the file is only skipped over.
/// </summary>
internal static class AssParser
{
    // The Format of Advanced SubStation Alpha events, used when the [Events] section has no Format line.
    private static readonly string[] DefaultFormat = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    internal static AssParseResult Parse(byte[] data)
    {
        DecodedText? decoded = DecodedText.Decode(data, out string? failure);
        if (decoded is null)
        {
            return AssParseResult.Fail(failure!);
        }

        string text = decoded.Text;
        bool scriptInfo = false;
        bool inEvents = false;
        string[]? format = null;
        var cues = new List<SubtitleCue>();
        var events = new List<AssEvent>();
        for (int i = 0; i < decoded.Lines.Count; i++)
        {
            TextLine line = decoded.Lines[i];
            string content = decoded.Of(line).ToString();
            Match section = AssPatterns.Section().Match(content);
            if (section.Success)
            {
                string name = section.Groups[1].Value.Trim();
                scriptInfo |= name.Equals("Script Info", StringComparison.OrdinalIgnoreCase);
                inEvents = name.Equals("Events", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            int colon = content.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string key = content[..colon].Trim();
            if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                format = content[(colon + 1)..].Split(',').Select(f => f.Trim()).ToArray();
                if (!format[^1].Equals("Text", StringComparison.OrdinalIgnoreCase))
                {
                    return AssParseResult.Fail("The [Events] Format line does not end with Text, so lines cannot be edited safely.");
                }

                continue;
            }

            if (!key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] fields = format ?? DefaultFormat;
            int startField = Index(fields, "Start");
            int endField = Index(fields, "End");
            if (startField < 0 || endField < 0)
            {
                return AssParseResult.Fail("The [Events] Format line has no Start or End field.");
            }

            // The first field starts after the colon and any spaces; Text is everything after the last separating comma.
            int first = line.Start + colon + 1;
            while (first < line.End && text[first] == ' ')
            {
                first++;
            }

            var values = new string[fields.Length - 1];
            int cursor = first;
            for (int f = 0; f < values.Length; f++)
            {
                int comma = text.IndexOf(',', cursor, line.End - cursor);
                if (comma < 0)
                {
                    return AssParseResult.Fail(
                        string.Create(CultureInfo.InvariantCulture, $"Line {i + 1} has fewer fields than the Format line."));
                }

                values[f] = text[cursor..comma];
                cursor = comma + 1;
            }

            if (!TryTime(values[startField], out long startMs) || !TryTime(values[endField], out long endMs))
            {
                return AssParseResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Line {i + 1} has a time that cannot be read."));
            }

            string field = text[cursor..line.End];
            int styleField = Index(fields, "Style");
            bool terminated = line.TermEnd > line.End;
            cues.Add(new SubtitleCue
            {
                Position = cues.Count + 1,
                StartMs = startMs,
                EndMs = endMs,
                Text = AssDocument.FromField(field),
                Style = styleField >= 0 ? values[styleField].Trim() : null,
                ReadOnly = AssPatterns.Drawing().IsMatch(field),
                Layout = new CueLayout(
                    BlockStart: line.Start,
                    NumberStart: -1,
                    NumberEnd: -1,
                    TimeLineStart: line.Start,
                    TextStart: cursor,
                    TextEnd: line.End,
                    TextEmpty: cursor == line.End,
                    TimeLineTerminated: terminated,
                    PrecededByBlankLine: false),
            });
            events.Add(new AssEvent(line.Start, line.TermEnd, terminated, text[line.Start..first], values));
        }

        if (!scriptInfo)
        {
            return AssParseResult.Fail("The file has no [Script Info] section, so it is not an ASS or SSA file.");
        }

        if (cues.Count == 0)
        {
            return AssParseResult.Fail("The file contains no Dialogue lines.");
        }

        string[] used = format ?? DefaultFormat;
        return AssParseResult.Ok(AssDocument.Create(data, decoded, cues, events, Index(used, "Start"), Index(used, "End")));
    }

    private static int Index(string[] fields, string name) =>
        Array.FindIndex(fields, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryTime(string value, out long ms)
    {
        ms = 0;
        Match match = AssPatterns.Time().Match(value);
        if (!match.Success)
        {
            return false;
        }

        long h = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        long m = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        long s = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        string fraction = match.Groups[4].Value;
        long part = long.Parse(fraction, CultureInfo.InvariantCulture) * (fraction.Length == 1 ? 100 : fraction.Length == 2 ? 10 : 1);
        ms = (h * 3_600_000) + (m * 60_000) + (s * 1000) + part;
        return true;
    }
}
