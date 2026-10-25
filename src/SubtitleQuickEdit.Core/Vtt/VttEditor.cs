using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Vtt;

/// <summary>
/// Applies edits to a parsed WebVTT document by splicing bytes into the original content. Cue identifiers are
/// optional in WebVTT, so nothing is renumbered: added cues get no identifier.
/// </summary>
internal static class VttEditor
{
    internal static SubtitleApplyResult Apply(VttDocument doc, IReadOnlyList<SubtitleEdit> edits, long? runtimeMs)
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
            if (string.Equals(pair.Value.Old, pair.Value.New, StringComparison.Ordinal))
            {
                continue;
            }

            CueLayout l = doc.Cues[pair.Key - 1].Layout;
            if (l.TextEmpty)
            {
                string inserted = l.TimeLineTerminated ? pair.Value.New + "\n" : "\n" + pair.Value.New;
                patches.Add(new BytePatch(doc.ByteOffset(l.TextStart), 0, ByteSplicer.Encode(doc.Encoding, inserted, eol, pair.Key), 0));
            }
            else
            {
                int from = doc.ByteOffset(l.TextStart);
                byte[] bytes = ByteSplicer.Encode(doc.Encoding, pair.Value.New, eol, pair.Key);
                patches.Add(new BytePatch(from, doc.ByteOffset(l.TextEnd) - from, bytes, 0));
            }
        }

        // Where each added cue goes: before the first cue that starts later, otherwise at the end.
        var addsBefore = new SortedDictionary<int, List<(SubtitleAddCue Cue, string Text)>>();
        foreach ((SubtitleAddCue cue, string text, int _) in adds.OrderBy(a => a.Cue.StartMs).ThenBy(a => a.Order))
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

        // Final order: the expected cues after the edit, and each added cue's position.
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
                        VttDocument.FormatTime(cue.StartMs) + " --> " + VttDocument.FormatTime(cue.EndMs) + eol
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
                    string inserted = ByteSplicer.Repeat(eol, 2 - tail) + joined + ByteSplicer.Repeat(eol, Math.Min(tail, 2));
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
        }

        byte[] content = ByteSplicer.Splice(doc.Data, patches);
        Verify(doc, content, expected);

        var edited = textEdits.Keys.OrderBy(p => p).Select(p => newPositionOfOriginal[p - 1]).ToList();
        return new SubtitleApplyResult(content, SubtitleHash.Compute(content), addedPositions, edited);
    }

    /// <summary>
    /// Normalizes line breaks and checks that text is something a WebVTT cue can hold: besides the shared rules, no
    /// line may contain <c>--&gt;</c>, which would start a new cue.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="cuePosition">The cue position for messages, if any.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    internal static string NormalizeAndValidateText(string value, int? cuePosition) =>
        CueRules.NormalizeAndCheckText(value, cuePosition, (line, position) =>
        {
            if (line.Contains("-->", StringComparison.Ordinal))
            {
                throw new SubtitleEditException(
                    SubtitleEditError.InvalidEdit,
                    "A line of text must not contain \"-->\": in a WebVTT file it would start a new cue.",
                    position);
            }
        });

    // An added line's checks: times, then its text.
    private static string CheckAdd(SubtitleAddCue add, long? runtimeMs, HashSet<string> clientIds)
    {
        CueRules.CheckAdd(add, runtimeMs, clientIds, VttDocument.MaxTimeMs, "A new line must end by 99:59:59.999.");
        return NormalizeAndValidateText(add.Text, null);
    }

    private static void Verify(VttDocument doc, byte[] content, List<(long Start, long End, string Text)> expected)
    {
        VttParseResult result = VttParser.Parse(content);
        if (result.Document is null)
        {
            throw new SubtitleEditException(
                SubtitleEditError.VerificationFailed,
                "The edited file could not be read back (" + result.FailureReason + "). Nothing was saved.");
        }

        VttDocument after = result.Document;
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
                "The edited file would not have the expected cues. Nothing was saved.");
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
                    string.Create(CultureInfo.InvariantCulture, $"Cue {i + 1} would not read back as expected. Nothing was saved."),
                    i + 1);
            }
        }
    }
}
