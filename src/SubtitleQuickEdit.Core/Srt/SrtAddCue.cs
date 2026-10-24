namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// Adds a new cue, inserted in start-time order.
/// </summary>
/// <param name="ClientId">An id chosen by the client, echoed back in the result.</param>
/// <param name="StartMs">Start time in milliseconds.</param>
/// <param name="EndMs">End time in milliseconds.</param>
/// <param name="Text">The cue text. Line breaks are <c>\n</c>.</param>
public sealed record SrtAddCue(string ClientId, long StartMs, long EndMs, string Text) : SrtEdit;
