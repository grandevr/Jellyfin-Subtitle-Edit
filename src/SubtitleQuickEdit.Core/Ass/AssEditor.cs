using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Ass;

/// <summary>
/// Applies edits to a parsed ASS/SSA document by splicing bytes into the original content. A text edit replaces only
/// a Dialogue line's Text field. An added line copies every field but Start, End and Text from the Dialogue line it
/// follows: the one that starts closest before it (ASS files need not be in time order, so this is by time, not by
/// position).
/// </summary>
internal static class AssEditor
{
    internal static SubtitleApplyResult Apply(AssDocument doc, IReadOnlyList<SubtitleEdit> edits, long? runtimeMs)
    {
        if (edits.Count == 0)
        {
            return new SubtitleApplyResult(doc.Data.ToArray(), doc.Hash, new Dictionary<string, int>(), Array.Empty<int>());
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
                    if (doc.Cues[te.CuePosition - 1].ReadOnly)
                    {
                        throw new SubtitleEditException(
                            SubtitleEditError.InvalidEdit,
                            string.Create(CultureInfo.InvariantCulture, $"Line {te.CuePosition} is a drawing, which cannot be edited here."),
                            te.CuePosition);
                    }

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
        foreach (KeyValuePair<int, (string Old, string New)> pair in textEdits)
        {
            if (string.Equals(pair.Value.Old, pair.Value.New, StringComparison.Ordinal))
            {
                continue;
            }

            CueLayout l = doc.Cues[pair.Key - 1].Layout;
            int from = doc.ByteOffset(l.TextStart);
            byte[] bytes = ByteSplicer.Encode(doc.Encoding, AssDocument.ToField(pair.Value.New), null, pair.Key);
            patches.Add(new BytePatch(from, doc.ByteOffset(l.TextEnd) - from, bytes, 0));
        }

        // Where each added line goes: after the Dialogue line that starts closest before it, or at the same time (the
        // later one in the file on a tie); before the first Dialogue line (anchor -1) when none starts that early.
        var addsAfter = new Dictionary<int, List<(long Start, long End, string Text, string ClientId)>>();
        foreach ((SubtitleAddCue cue, string text, int _) in adds.OrderBy(a => a.Cue.StartMs).ThenBy(a => a.Order))
        {
            long start = AssDocument.RoundTime(cue.StartMs);
            int anchor = -1;
            for (int i = 0; i < doc.Cues.Count; i++)
            {
                if (doc.Cues[i].StartMs <= start && (anchor < 0 || doc.Cues[i].StartMs >= doc.Cues[anchor].StartMs))
                {
                    anchor = i;
                }
            }

            if (!addsAfter.TryGetValue(anchor, out List<(long Start, long End, string Text, string ClientId)>? list))
            {
                list = new List<(long Start, long End, string Text, string ClientId)>();
                addsAfter[anchor] = list;
            }

            list.Add((start, AssDocument.RoundTime(cue.EndMs), text, cue.ClientId));
        }

        var addedPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        var newPositionOfOriginal = new int[doc.Cues.Count];
        var expected = new List<(long Start, long End, string Text)>();
        int ordinal = 0;
        for (int k = -1; k < doc.Cues.Count; k++)
        {
            if (k >= 0)
            {
                ordinal++;
                SubtitleCue original = doc.Cues[k];
                newPositionOfOriginal[k] = ordinal;
                string finalText = textEdits.TryGetValue(original.Position, out (string Old, string New) te) ? te.New : original.Text;
                expected.Add((original.StartMs, original.EndMs, finalText));
            }

            if (!addsAfter.TryGetValue(k, out List<(long Start, long End, string Text, string ClientId)>? group))
            {
                continue;
            }

            AssEvent template = doc.Events[Math.Max(k, 0)];
            var lines = new List<string>();
            foreach ((long start, long end, string text, string clientId) in group)
            {
                ordinal++;
                addedPositions[clientId] = ordinal;
                expected.Add((start, end, text));
                string[] fields = (string[])template.Fields.Clone();
                fields[doc.StartField] = AssDocument.FormatTime(start);
                fields[doc.EndField] = AssDocument.FormatTime(end);
                lines.Add(template.Prefix + string.Join(',', fields) + "," + AssDocument.ToField(text));
            }

            string block = string.Join(eol, lines);
            if (k < 0)
            {
                patches.Add(new BytePatch(doc.ByteOffset(template.LineStart), 0, ByteSplicer.Encode(doc.Encoding, block + eol, null, null), 1));
            }
            else
            {
                string inserted = template.Terminated ? block + eol : eol + block;
                patches.Add(new BytePatch(doc.ByteOffset(template.TermEnd), 0, ByteSplicer.Encode(doc.Encoding, inserted, null, null), 1));
            }
        }

        byte[] content = ByteSplicer.Splice(doc.Data, patches);
        Verify(doc, content, expected);

        var edited = textEdits.Keys.OrderBy(p => p).Select(p => newPositionOfOriginal[p - 1]).ToList();
        return new SubtitleApplyResult(content, SubtitleHash.Compute(content), addedPositions, edited);
    }

    /// <summary>
    /// Normalizes line breaks and checks that text is something a Text field can hold: not empty and no control
    /// characters. Empty lines are allowed (<c>\N\N</c> is common in ASS); commas are fine, as Text is the last field.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="cuePosition">The cue position for messages, if any.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    internal static string NormalizeAndValidateText(string value, int? cuePosition)
    {
        string text = SubtitleText.NormalizeBreaks(value);
        if (text.Length == 0)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A cue must keep at least one character.", cuePosition);
        }

        foreach (char c in text)
        {
            if (c < ' ' && c != '\t' && c != '\n')
            {
                throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "The text contains a control character.", cuePosition);
            }
        }

        return text;
    }

    // An added line's checks: times, then its text.
    private static string CheckAdd(SubtitleAddCue add, long? runtimeMs, HashSet<string> clientIds)
    {
        CueRules.CheckAdd(add, runtimeMs, clientIds, AssDocument.MaxTimeMs, "A new line must end by 99:59:59.99.");
        return NormalizeAndValidateText(add.Text, null);
    }

    private static void Verify(AssDocument doc, byte[] content, List<(long Start, long End, string Text)> expected)
    {
        AssParseResult result = AssParser.Parse(content);
        if (result.Document is null)
        {
            throw new SubtitleEditException(
                SubtitleEditError.VerificationFailed,
                "The edited file could not be read back (" + result.FailureReason + "). Nothing was saved.");
        }

        AssDocument after = result.Document;
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
                "The edited file would not have the expected lines. Nothing was saved.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            SubtitleCue cue = after.Cues[i];
            if (cue.StartMs != expected[i].Start
                || cue.EndMs != expected[i].End
                || !string.Equals(cue.Text, expected[i].Text, StringComparison.Ordinal))
            {
                throw new SubtitleEditException(
                    SubtitleEditError.VerificationFailed,
                    string.Create(CultureInfo.InvariantCulture, $"Line {i + 1} would not read back as expected. Nothing was saved."),
                    i + 1);
            }
        }
    }
}
