namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// A text subtitle file format the editor knows.
/// </summary>
public enum SubtitleFormat
{
    /// <summary>SubRip, <c>.srt</c>.</summary>
    Srt,

    /// <summary>WebVTT, <c>.vtt</c>.</summary>
    WebVtt,

    /// <summary>Advanced SubStation Alpha or SubStation Alpha, <c>.ass</c> or <c>.ssa</c>.</summary>
    Ass,
}
