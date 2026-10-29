namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Replaces the text of an existing cue, and its times when <see cref="NewStartMs"/> and <see cref="NewEndMs"/> are
/// set. The cue keeps its place in the file; every other part of it (number, cue settings, ASS fields) is kept.
/// </summary>
/// <param name="CuePosition">The cue's 1-based position in the file (not its number line).</param>
/// <param name="OldText">The text the client saw; must match the file's current text.</param>
/// <param name="NewText">The replacement text (the old text to change only the times). Line breaks are <c>\n</c>.</param>
public sealed record SubtitleTextEdit(int CuePosition, string OldText, string NewText) : SubtitleEdit
{
    /// <summary>
    /// Gets the new start time in milliseconds, or null to keep the cue's times.
    /// </summary>
    public long? NewStartMs { get; init; }

    /// <summary>
    /// Gets the new end time in milliseconds, or null to keep the cue's times.
    /// </summary>
    public long? NewEndMs { get; init; }
}
