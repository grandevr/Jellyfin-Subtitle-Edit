using System;
using System.Collections.Generic;
using System.Globalization;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// The edit checks every format shares: which cue a text edit names, whether its old text still matches, and whether
/// an added line's times are usable.
/// </summary>
internal static class CueRules
{
    /// <summary>
    /// Checks a text edit against the document and records it in <paramref name="seen"/>.
    /// </summary>
    /// <param name="cues">The document's cues.</param>
    /// <param name="edit">The edit.</param>
    /// <param name="newText">The edit's new text, already normalized and checked for the format.</param>
    /// <param name="seen">The text edits so far, by cue position: old and new text.</param>
    internal static void AcceptTextEdit(
        IReadOnlyList<SubtitleCue> cues,
        SubtitleTextEdit edit,
        string newText,
        Dictionary<int, (string Old, string New)> seen)
    {
        string oldText = SubtitleText.NormalizeBreaks(edit.OldText ?? string.Empty);
        SubtitleCue cue = cues[edit.CuePosition - 1];
        if (!string.Equals(oldText, cue.Text, StringComparison.Ordinal))
        {
            throw new SubtitleEditException(
                SubtitleEditError.TextMismatch,
                string.Create(CultureInfo.InvariantCulture, $"Cue {edit.CuePosition} has changed since it was loaded."),
                edit.CuePosition);
        }

        seen[edit.CuePosition] = (cue.Text, newText);
    }

    /// <summary>
    /// Checks that a text edit names an existing cue, once.
    /// </summary>
    /// <param name="cues">The document's cues.</param>
    /// <param name="edit">The edit.</param>
    /// <param name="seen">The text edits so far, by cue position.</param>
    internal static void CheckTarget(IReadOnlyList<SubtitleCue> cues, SubtitleTextEdit edit, Dictionary<int, (string Old, string New)> seen)
    {
        if (edit.CuePosition < 1 || edit.CuePosition > cues.Count)
        {
            throw new SubtitleEditException(
                SubtitleEditError.CueNotFound,
                string.Create(CultureInfo.InvariantCulture, $"Cue {edit.CuePosition} does not exist; the file has {cues.Count} cues."),
                edit.CuePosition);
        }

        if (seen.ContainsKey(edit.CuePosition))
        {
            throw new SubtitleEditException(
                SubtitleEditError.InvalidEdit,
                string.Create(CultureInfo.InvariantCulture, $"Cue {edit.CuePosition} is edited more than once in the same request."),
                edit.CuePosition);
        }
    }

    /// <summary>
    /// Checks an added line's client id and times.
    /// </summary>
    /// <param name="add">The added line.</param>
    /// <param name="runtimeMs">The item's runtime, or null to skip that check.</param>
    /// <param name="clientIds">The client ids seen so far in the request.</param>
    /// <param name="maxTimeMs">The largest time the format can hold.</param>
    /// <param name="maxTimeMessage">The message when the line ends after <paramref name="maxTimeMs"/>.</param>
    internal static void CheckAdd(SubtitleAddCue add, long? runtimeMs, HashSet<string> clientIds, long maxTimeMs, string maxTimeMessage)
    {
        if (string.IsNullOrEmpty(add.ClientId))
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "An added line needs a client id.");
        }

        if (!clientIds.Add(add.ClientId))
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "Two added lines share the client id '" + add.ClientId + "'.");
        }

        if (add.StartMs < 0)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A new line cannot start before 00:00:00,000.");
        }

        if (add.EndMs <= add.StartMs)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A new line must end after it starts.");
        }

        if (add.EndMs - add.StartMs < SubtitleLimits.MinCueDurationMs)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A new line must be at least 100 ms long.");
        }

        if (add.EndMs > maxTimeMs)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, maxTimeMessage);
        }

        if (runtimeMs.HasValue && add.EndMs > runtimeMs.Value)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A new line must not end after the end of the video.");
        }
    }

    /// <summary>
    /// Normalizes line breaks and checks the parts of a cue's text that every format shares: not empty, no blank
    /// line, no control characters.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="cuePosition">The cue position for messages, if any.</param>
    /// <param name="lineCheck">A format's own check of each line; it throws to refuse one. May be null.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    internal static string NormalizeAndCheckText(string value, int? cuePosition, Action<string, int?>? lineCheck)
    {
        string text = SubtitleText.NormalizeBreaks(value);
        if (text.Length == 0)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "A cue must keep at least one character.", cuePosition);
        }

        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new SubtitleEditException(
                    SubtitleEditError.InvalidEdit,
                    "A cue's text must not contain empty lines: a blank line would end the cue.",
                    cuePosition);
            }

            lineCheck?.Invoke(line, cuePosition);
            foreach (char c in line)
            {
                if (c < ' ' && c != '\t')
                {
                    throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "The text contains a control character.", cuePosition);
                }
            }
        }

        return text;
    }

    /// <summary>
    /// Wraps an add's check so a refusal names the added line, so the client can mark the right row.
    /// </summary>
    /// <param name="add">The added line.</param>
    /// <param name="check">The checks to run.</param>
    /// <returns>What <paramref name="check"/> returns.</returns>
    internal static string ForAdd(SubtitleAddCue add, Func<string> check)
    {
        try
        {
            return check();
        }
        catch (SubtitleEditException ex) when (ex.Error == SubtitleEditError.InvalidEdit && ex.ClientId is null && !string.IsNullOrEmpty(add.ClientId))
        {
            throw new SubtitleEditException(ex.Error, ex.Message, ex.CuePosition) { ClientId = add.ClientId };
        }
    }
}
