using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// Applies edits to a parsed .srt document by splicing bytes into the original content.
/// </summary>
internal static class SrtEditor
{
    internal static SubtitleApplyResult Apply(SrtDocument doc, IReadOnlyList<SubtitleEdit> edits, long? runtimeMs)
    {
        if (edits.Count == 0)
        {
            return new SubtitleApplyResult(
                doc.Data.ToArray(),
                doc.Hash,
                new Dictionary<string, int>(),
                Array.Empty<int>());
        }

        var textEdits = new Dictionary<int, (string Old, string New)>();
        var adds = new List<(SubtitleAddCue Cue, string Text, int Order)>();
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SubtitleEdit edit in edits)
        {
            switch (edit)
            {
                case SubtitleTextEdit te:
                    CueRules.CheckTarget(doc.Cues, te, textEdits);
                    CueRules.AcceptTextEdit(doc.Cues, te, NormalizeAndValidateText(te.NewText, te.CuePosition), textEdits);
                    break;
                case SubtitleAddCue add:
                    adds.Add((add, CueRules.ForAdd(add, () => CheckAdd(add, runtimeMs, clientIds)), adds.Count));
                    break;
                default:
                    throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "Unknown edit type.");
            }
        }

        string eol = doc.LineEnding;
        var patches = new List<BytePatch>();

        // Text replacements.
        foreach (KeyValuePair<int, (string Old, string New)> pair in textEdits)
        {
            SubtitleCue cue = doc.Cues[pair.Key - 1];
            if (string.Equals(pair.Value.Old, pair.Value.New, StringComparison.Ordinal))
            {
                continue;
            }

            CueLayout l = cue.Layout;
            if (l.TextEmpty)
            {
                byte[] bytes = ByteSplicer.Encode(doc.Encoding, l.TimeLineTerminated ? pair.Value.New + "\n" : "\n" + pair.Value.New, eol, pair.Key);
                patches.Add(new BytePatch(doc.ByteOffset(l.TextStart), 0, bytes, 0));
            }
            else
            {
                byte[] bytes = ByteSplicer.Encode(doc.Encoding, pair.Value.New, eol, pair.Key);
                int from = doc.ByteOffset(l.TextStart);
                patches.Add(new BytePatch(from, doc.ByteOffset(l.TextEnd) - from, bytes, 0));
            }
        }

        // Where each added cue goes: before the first cue that starts later, otherwise at the end.
        var sortedAdds = adds.OrderBy(a => a.Cue.StartMs).ThenBy(a => a.Order).ToList();
        var addsBefore = new Dictionary<int, List<(SubtitleAddCue Cue, string Text)>>();
        foreach ((SubtitleAddCue cue, string text, int _) in sortedAdds)
        {
            int k = doc.Cues.Count;
            for (int i = 0; i < doc.Cues.Count; i++)
            {
                if (doc.Cues[i].StartMs > cue.StartMs)
                {
                    k = i;
                    break;
                }
            }

            if (!addsBefore.TryGetValue(k, out List<(SubtitleAddCue Cue, string Text)>? list))
            {
                list = new List<(SubtitleAddCue Cue, string Text)>();
                addsBefore[k] = list;
            }

            list.Add((cue, text));
        }

        // Final order and numbering.
        var addedPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        var newPositionOfOriginal = new int[doc.Cues.Count];
        var expected = new List<(long Start, long End, string Text)>();
        int ordinal = 0;
        for (int k = 0; k <= doc.Cues.Count; k++)
        {
            if (addsBefore.TryGetValue(k, out List<(SubtitleAddCue Cue, string Text)>? group))
            {
                var blocks = new List<string>();
                foreach ((SubtitleAddCue cue, string text) in group)
                {
                    ordinal++;
                    addedPositions[cue.ClientId] = ordinal;
                    expected.Add((cue.StartMs, cue.EndMs, text));
                    blocks.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{ordinal}{eol}{SrtDocument.FormatTime(cue.StartMs)} --> {SrtDocument.FormatTime(cue.EndMs)}{eol}")
                        + text.Replace("\n", eol, StringComparison.Ordinal));
                }

                string joined = string.Join(eol + eol, blocks);
                if (k < doc.Cues.Count)
                {
                    CueLayout target = doc.Cues[k].Layout;
                    string inserted = (target.PrecededByBlankLine ? string.Empty : eol) + joined + eol + eol;
                    patches.Add(new BytePatch(doc.ByteOffset(target.BlockStart), 0, ByteSplicer.Encode(doc.Encoding, inserted, null, null), 1));
                }
                else
                {
                    int tail = doc.TailTerminators;
                    int prefix = Math.Max(0, 2 - tail);
                    int suffix = Math.Min(tail, 2);
                    string inserted = ByteSplicer.Repeat(eol, prefix) + joined + ByteSplicer.Repeat(eol, suffix);
                    patches.Add(new BytePatch(doc.Data.Length, 0, ByteSplicer.Encode(doc.Encoding, inserted, null, null), 1));
                }
            }

            if (k == doc.Cues.Count)
            {
                break;
            }

            ordinal++;
            SubtitleCue original = doc.Cues[k];
            newPositionOfOriginal[k] = ordinal;
            string finalText = textEdits.TryGetValue(original.Position, out (string Old, string New) te) ? te.New : original.Text;
            expected.Add((original.StartMs, original.EndMs, finalText));
            if (adds.Count > 0 && original.Number != ordinal)
            {
                CueLayout l = original.Layout;
                string digits = ordinal.ToString(CultureInfo.InvariantCulture);
                if (l.NumberStart >= 0)
                {
                    int from = doc.ByteOffset(l.NumberStart);
                    patches.Add(new BytePatch(from, doc.ByteOffset(l.NumberEnd) - from, ByteSplicer.Encode(doc.Encoding, digits, null, null), 2));
                }
                else
                {
                    patches.Add(new BytePatch(doc.ByteOffset(l.TimeLineStart), 0, ByteSplicer.Encode(doc.Encoding, digits + eol, null, null), 2));
                }
            }
        }

        byte[] content = ByteSplicer.Splice(doc.Data, patches);
        Verify(doc, content, expected, adds.Count > 0);

        var edited = textEdits.Keys.OrderBy(p => p).Select(p => newPositionOfOriginal[p - 1]).ToList();
        return new SubtitleApplyResult(content, SubtitleHash.Compute(content), addedPositions, edited);
    }

    internal static SubtitleApplyResult Compose(IReadOnlyList<SubtitleAddCue> cues, long? runtimeMs)
    {
        if (cues.Count == 0)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A new subtitle needs at least one line.");
        }

        const string eol = "\r\n";
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<(SubtitleAddCue Cue, string Text, int Order)>();
        foreach (SubtitleAddCue add in cues)
        {
            lines.Add((add, CueRules.ForAdd(add, () => CheckAdd(add, runtimeMs, clientIds)), lines.Count));
        }

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var expected = new List<(long Start, long End, string Text)>();
        var text = new StringBuilder();
        foreach ((SubtitleAddCue cue, string body, int _) in lines.OrderBy(l => l.Cue.StartMs).ThenBy(l => l.Order))
        {
            int number = expected.Count + 1;
            positions[cue.ClientId] = number;
            expected.Add((cue.StartMs, cue.EndMs, body));
            text.Append(number.ToString(CultureInfo.InvariantCulture)).Append(eol)
                .Append(SrtDocument.FormatTime(cue.StartMs)).Append(" --> ").Append(SrtDocument.FormatTime(cue.EndMs)).Append(eol)
                .Append(body.Replace("\n", eol, StringComparison.Ordinal)).Append(eol).Append(eol);
        }

        byte[] content = new UTF8Encoding(false).GetBytes(text.ToString());
        SrtParseResult parsed = SrtParser.Parse(content);
        if (parsed.Document is null || parsed.Document.Cues.Count != expected.Count)
        {
            throw new SubtitleEditException(
                SubtitleEditError.VerificationFailed,
                "The new file would not have the expected lines (this can happen when a line's text ends in a bare number). Nothing was made.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            SubtitleCue cue = parsed.Document.Cues[i];
            if (cue.StartMs != expected[i].Start || cue.EndMs != expected[i].End || !string.Equals(cue.Text, expected[i].Text, StringComparison.Ordinal))
            {
                throw new SubtitleEditException(
                    SubtitleEditError.VerificationFailed,
                    string.Create(CultureInfo.InvariantCulture, $"Line {i + 1} would not read back as expected. Nothing was made."),
                    i + 1);
            }
        }

        return new SubtitleApplyResult(content, SubtitleHash.Compute(content), positions, Array.Empty<int>());
    }

    /// <summary>
    /// Normalizes line breaks and checks that text is something a cue can hold.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="cuePosition">The cue position for messages, if any.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    internal static string NormalizeAndValidateText(string value, int? cuePosition) =>
        CueRules.NormalizeAndCheckText(value, cuePosition, (line, position) =>
        {
            if (SrtPatterns.TimeLine().IsMatch(line))
            {
                throw new SubtitleEditException(
                    SubtitleEditError.InvalidEdit,
                    "A line of text must not look like a cue time line (hh:mm:ss,mmm --> hh:mm:ss,mmm).",
                    position);
            }
        });

    // An added line's checks: times, then its text.
    private static string CheckAdd(SubtitleAddCue add, long? runtimeMs, HashSet<string> clientIds)
    {
        CueRules.CheckAdd(add, runtimeMs, clientIds, SrtDocument.MaxTimeMs, "A new line ends after 99:59:59,999, which an .srt file cannot hold.");
        return NormalizeAndValidateText(add.Text, null);
    }

    private static void Verify(SrtDocument doc, byte[] content, List<(long Start, long End, string Text)> expected, bool renumbered)
    {
        SrtParseResult result = SrtParser.Parse(content);
        if (result.Document is null)
        {
            throw new SubtitleEditException(
                SubtitleEditError.VerificationFailed,
                "The edited file could not be read back (" + result.FailureReason + "). Nothing was saved.");
        }

        SrtDocument after = result.Document;
        if (!string.Equals(after.Encoding.Name, doc.Encoding.Name, StringComparison.Ordinal)
            || after.Encoding.BomLength != doc.Encoding.BomLength)
        {
            throw new SubtitleEditException(
                SubtitleEditError.VerificationFailed,
                "The edited file would be read in a different encoding than the original. Nothing was saved.");
        }

        if (after.Cues.Count != expected.Count)
        {
            throw new SubtitleEditException(
                SubtitleEditError.VerificationFailed,
                "The edited file would not have the expected cues (this can happen when a cue's text ends in a bare number). Nothing was saved.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            SubtitleCue cue = after.Cues[i];
            if (cue.StartMs != expected[i].Start
                || cue.EndMs != expected[i].End
                || !string.Equals(cue.Text, expected[i].Text, StringComparison.Ordinal)
                || (renumbered && cue.Number != cue.Position))
            {
                throw new SubtitleEditException(
                    SubtitleEditError.VerificationFailed,
                    string.Create(CultureInfo.InvariantCulture, $"Cue {i + 1} would not read back as expected. Nothing was saved."),
                    i + 1);
            }
        }
    }
}
