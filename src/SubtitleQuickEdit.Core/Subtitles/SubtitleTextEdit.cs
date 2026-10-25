namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Replaces the text of an existing cue.
/// </summary>
/// <param name="CuePosition">The cue's 1-based position in the file (not its number line).</param>
/// <param name="OldText">The text the client saw; must match the file's current text.</param>
/// <param name="NewText">The replacement text. Line breaks are <c>\n</c>.</param>
public sealed record SubtitleTextEdit(int CuePosition, string OldText, string NewText) : SubtitleEdit;
