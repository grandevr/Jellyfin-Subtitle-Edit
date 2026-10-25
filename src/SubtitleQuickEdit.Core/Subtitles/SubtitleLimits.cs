namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Limits that apply to every subtitle format.
/// </summary>
public static class SubtitleLimits
{
    /// <summary>
    /// Files larger than this are not editable.
    /// </summary>
    public const int MaxFileBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The shortest cue the editor accepts, in milliseconds.
    /// </summary>
    public const long MinCueDurationMs = 100;
}
