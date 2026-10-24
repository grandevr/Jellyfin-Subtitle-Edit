using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// Applies edits to a parsed .srt document by splicing bytes into the original content.
/// </summary>
internal static class SrtEditor
{
    internal static SrtApplyResult Apply(SrtDocument doc, IReadOnlyList<SrtEdit> edits, long? runtimeMs)
    {
        if (edits.Count == 0)
        {
            return new SrtApplyResult(
                doc.Data.ToArray(),
                doc.Hash,
                new Dictionary<string, int>(),
                Array.Empty<int>());
        }

        var textEdits = new Dictionary<int, (string Old, string New)>();
        var adds = new List<(SrtAddCue Cue, string Text, int Order)>();
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SrtEdit edit in edits)
        {
            switch (edit)
            {
                case SrtTextEdit te:
                    ValidateTextEdit(doc, te, textEdits);
                    break;
                case SrtAddCue add:
                    try
                    {
                        ValidateAdd(add, runtimeMs, clientIds);
                        adds.Add((add, NormalizeAndValidateText(add.Text, null), adds.Count));
                    }
                    catch (SrtEditException ex) when (ex.Error == SrtEditError.InvalidEdit && ex.ClientId is null && !string.IsNullOrEmpty(add.ClientId))
                    {
                        // Name the added line, so the client can mark the right row.
                        throw new SrtEditException(ex.Error, ex.Message, ex.CuePosition) { ClientId = add.ClientId };
                    }

                    break;
                default:
                    throw new SrtEditException(SrtEditError.InvalidEdit, "Unknown edit type.");
            }
        }

        string eol = doc.LineEnding;
        var patches = new List<Patch>();

        // Text replacements.
        foreach (KeyValuePair<int, (string Old, string New)> pair in textEdits)
        {
            SrtCue cue = doc.Cues[pair.Key - 1];
            if (string.Equals(pair.Value.Old, pair.Value.New, StringComparison.Ordinal))
            {
                continue;
            }

            CueLayout l = cue.Layout;
            if (l.TextEmpty)
            {
                byte[] bytes = Encode(doc, l.TimeLineTerminated ? pair.Value.New + "\n" : "\n" + pair.Value.New, eol, pair.Key);
                patches.Add(new Patch(doc.ByteOffset(l.TextStart), 0, bytes, 0));
            }
            else
            {
                byte[] bytes = Encode(doc, pair.Value.New, eol, pair.Key);
                int from = doc.ByteOffset(l.TextStart);
                patches.Add(new Patch(from, doc.ByteOffset(l.TextEnd) - from, bytes, 0));
            }
        }

        // Where each added cue goes: before the first cue that starts later, otherwise at the end.
        var sortedAdds = adds.OrderBy(a => a.Cue.StartMs).ThenBy(a => a.Order).ToList();
        var addsBefore = new Dictionary<int, List<(SrtAddCue Cue, string Text)>>();
        foreach ((SrtAddCue cue, string text, int _) in sortedAdds)
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

            if (!addsBefore.TryGetValue(k, out List<(SrtAddCue Cue, string Text)>? list))
            {
                list = new List<(SrtAddCue Cue, string Text)>();
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
            if (addsBefore.TryGetValue(k, out List<(SrtAddCue Cue, string Text)>? group))
            {
                var blocks = new List<string>();
                foreach ((SrtAddCue cue, string text) in group)
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
                    patches.Add(new Patch(doc.ByteOffset(target.BlockStart), 0, Encode(doc, inserted, null, null), 1));
                }
                else
                {
                    int tail = doc.TailTerminators;
                    int prefix = Math.Max(0, 2 - tail);
                    int suffix = Math.Min(tail, 2);
                    string inserted = Repeat(eol, prefix) + joined + Repeat(eol, suffix);
                    patches.Add(new Patch(doc.Data.Length, 0, Encode(doc, inserted, null, null), 1));
                }
            }

            if (k == doc.Cues.Count)
            {
                break;
            }

            ordinal++;
            SrtCue original = doc.Cues[k];
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
                    patches.Add(new Patch(from, doc.ByteOffset(l.NumberEnd) - from, Encode(doc, digits, null, null), 2));
                }
                else
                {
                    patches.Add(new Patch(doc.ByteOffset(l.TimeLineStart), 0, Encode(doc, digits + eol, null, null), 2));
                }
            }
        }

        byte[] content = Splice(doc, patches);
        Verify(doc, content, expected, adds.Count > 0);

        var edited = textEdits.Keys.OrderBy(p => p).Select(p => newPositionOfOriginal[p - 1]).ToList();
        return new SrtApplyResult(content, SrtDocument.ComputeHash(content), addedPositions, edited);
    }

    /// <summary>
    /// Normalizes line breaks and checks that text is something a cue can hold.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="cuePosition">The cue position for messages, if any.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    internal static string NormalizeAndValidateText(string value, int? cuePosition)
    {
        string text = SrtParser.NormalizeBreaks(value);
        if (text.Length == 0)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "A cue must keep at least one character.", cuePosition);
        }

        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new SrtEditException(
                    SrtEditError.InvalidEdit,
                    "A cue's text must not contain empty lines: a blank line would end the cue.",
                    cuePosition);
            }

            if (SrtPatterns.TimeLine().IsMatch(line))
            {
                throw new SrtEditException(
                    SrtEditError.InvalidEdit,
                    "A line of text must not look like a cue time line (hh:mm:ss,mmm --> hh:mm:ss,mmm).",
                    cuePosition);
            }

            foreach (char c in line)
            {
                if (c < ' ' && c != '\t')
                {
                    throw new SrtEditException(SrtEditError.InvalidEdit, "The text contains a control character.", cuePosition);
                }
            }
        }

        return text;
    }

    private static void ValidateTextEdit(SrtDocument doc, SrtTextEdit edit, Dictionary<int, (string Old, string New)> seen)
    {
        if (edit.CuePosition < 1 || edit.CuePosition > doc.Cues.Count)
        {
            throw new SrtEditException(
                SrtEditError.CueNotFound,
                string.Create(CultureInfo.InvariantCulture, $"Cue {edit.CuePosition} does not exist; the file has {doc.Cues.Count} cues."),
                edit.CuePosition);
        }

        if (seen.ContainsKey(edit.CuePosition))
        {
            throw new SrtEditException(
                SrtEditError.InvalidEdit,
                string.Create(CultureInfo.InvariantCulture, $"Cue {edit.CuePosition} is edited more than once in the same request."),
                edit.CuePosition);
        }

        string newText = NormalizeAndValidateText(edit.NewText, edit.CuePosition);
        string oldText = SrtParser.NormalizeBreaks(edit.OldText ?? string.Empty);
        SrtCue cue = doc.Cues[edit.CuePosition - 1];
        if (!string.Equals(oldText, cue.Text, StringComparison.Ordinal))
        {
            throw new SrtEditException(
                SrtEditError.TextMismatch,
                string.Create(CultureInfo.InvariantCulture, $"Cue {edit.CuePosition} has changed since it was loaded."),
                edit.CuePosition);
        }

        seen[edit.CuePosition] = (cue.Text, newText);
    }

    private static void ValidateAdd(SrtAddCue add, long? runtimeMs, HashSet<string> clientIds)
    {
        if (string.IsNullOrEmpty(add.ClientId))
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "An added line needs a client id.");
        }

        if (!clientIds.Add(add.ClientId))
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "Two added lines share the client id '" + add.ClientId + "'.");
        }

        if (add.StartMs < 0)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "A new line cannot start before 00:00:00,000.");
        }

        if (add.EndMs <= add.StartMs)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "A new line must end after it starts.");
        }

        if (add.EndMs - add.StartMs < SrtDocument.MinCueDurationMs)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "A new line must be at least 100 ms long.");
        }

        if (add.EndMs > SrtDocument.MaxTimeMs)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "A new line ends after 99:59:59,999, which an .srt file cannot hold.");
        }

        if (runtimeMs.HasValue && add.EndMs > runtimeMs.Value)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "A new line must not end after the end of the video.");
        }
    }

    private static byte[] Encode(SrtDocument doc, string text, string? eol, int? cuePosition)
    {
        string withBreaks = eol is null ? text : text.Replace("\n", eol, StringComparison.Ordinal);
        try
        {
            return doc.Encoding.Encoding.GetBytes(withBreaks);
        }
        catch (EncoderFallbackException ex)
        {
            string shown = ex.CharUnknownHigh != '\0'
                ? string.Create(CultureInfo.InvariantCulture, $"U+{char.ConvertToUtf32(ex.CharUnknownHigh, ex.CharUnknownLow):X4}")
                : string.Create(CultureInfo.InvariantCulture, $"'{ex.CharUnknown}' (U+{(int)ex.CharUnknown:X4})");
            throw new SrtEditException(
                SrtEditError.Unrepresentable,
                $"The character {shown} cannot be stored in this file's {doc.Encoding.Name} encoding.",
                cuePosition);
        }
    }

    private static string Repeat(string value, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        return count == 1 ? value : value + value;
    }

    private static byte[] Splice(SrtDocument doc, List<Patch> patches)
    {
        List<Patch> ordered = patches.OrderBy(p => p.Offset).ThenBy(p => p.Order).ToList();
        ReadOnlySpan<byte> original = doc.Data;
        using var output = new System.IO.MemoryStream(original.Length + 256);
        int cursor = 0;
        foreach (Patch patch in ordered)
        {
            if (patch.Offset < cursor)
            {
                throw new SrtEditException(SrtEditError.VerificationFailed, "Internal error: overlapping edits.");
            }

            output.Write(original[cursor..patch.Offset]);
            output.Write(patch.Bytes);
            cursor = patch.Offset + patch.Length;
        }

        output.Write(original[cursor..]);
        return output.ToArray();
    }

    private static void Verify(SrtDocument doc, byte[] content, List<(long Start, long End, string Text)> expected, bool renumbered)
    {
        SrtParseResult result = SrtParser.Parse(content);
        if (result.Document is null)
        {
            throw new SrtEditException(
                SrtEditError.VerificationFailed,
                "The edited file could not be read back (" + result.FailureReason + "). Nothing was saved.");
        }

        SrtDocument after = result.Document;
        if (!string.Equals(after.Encoding.Name, doc.Encoding.Name, StringComparison.Ordinal)
            || after.Encoding.BomLength != doc.Encoding.BomLength)
        {
            throw new SrtEditException(
                SrtEditError.VerificationFailed,
                "The edited file would be read in a different encoding than the original. Nothing was saved.");
        }

        if (after.Cues.Count != expected.Count)
        {
            throw new SrtEditException(
                SrtEditError.VerificationFailed,
                "The edited file would not have the expected cues (this can happen when a cue's text ends in a bare number). Nothing was saved.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            SrtCue cue = after.Cues[i];
            if (cue.StartMs != expected[i].Start
                || cue.EndMs != expected[i].End
                || !string.Equals(cue.Text, expected[i].Text, StringComparison.Ordinal)
                || (renumbered && cue.Number != cue.Position))
            {
                throw new SrtEditException(
                    SrtEditError.VerificationFailed,
                    string.Create(CultureInfo.InvariantCulture, $"Cue {i + 1} would not read back as expected. Nothing was saved."),
                    i + 1);
            }
        }
    }

    private readonly record struct Patch(int Offset, int Length, byte[] Bytes, int Order);
}
