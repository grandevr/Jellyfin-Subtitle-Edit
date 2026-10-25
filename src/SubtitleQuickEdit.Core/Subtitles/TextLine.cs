namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// One line of a decoded file, as character offsets.
/// </summary>
/// <param name="Start">First character.</param>
/// <param name="End">End (exclusive) of the text, before the terminator.</param>
/// <param name="TermEnd">End (exclusive) of the terminator; equal to <paramref name="End"/> on a last line without one.</param>
internal readonly record struct TextLine(int Start, int End, int TermEnd);
