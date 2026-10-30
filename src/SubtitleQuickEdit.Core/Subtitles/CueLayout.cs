namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Character offsets of the parts of a cue in the decoded text (after the BOM).
/// </summary>
/// <param name="BlockStart">First character of the cue: the number line, or the time line when there is none.</param>
/// <param name="NumberStart">First digit of the number line, or -1 when there is no number line.</param>
/// <param name="NumberEnd">End (exclusive) of the digits of the number line, or -1.</param>
/// <param name="TimeLineStart">First character of the time line.</param>
/// <param name="TextStart">First character of the text; for an empty cue, where text would start.</param>
/// <param name="TextEnd">End (exclusive) of the text, before its final line terminator.</param>
/// <param name="TextEmpty">True when the cue has no text lines.</param>
/// <param name="TimeLineTerminated">True when the time line ends with a line terminator.</param>
/// <param name="PrecededByBlankLine">True when a blank line (or the start of the file) precedes the cue.</param>
/// <param name="TimesStart">First character of the start time on the time line (SRT, WebVTT), or -1.</param>
/// <param name="TimesEnd">End (exclusive) of the end time on the time line (SRT, WebVTT), or -1. Cue settings after it are kept.</param>
internal readonly record struct CueLayout(
    int BlockStart,
    int NumberStart,
    int NumberEnd,
    int TimeLineStart,
    int TextStart,
    int TextEnd,
    bool TextEmpty,
    bool TimeLineTerminated,
    bool PrecededByBlankLine,
    int TimesStart = -1,
    int TimesEnd = -1);
