using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// Whether a subtitle file can be edited, and if so its parsed content.
/// </summary>
/// <param name="Document">The parsed file, or null when it is not editable.</param>
/// <param name="Reason">Why it is not editable, or null when it is.</param>
public sealed record TrackInspection(ISubtitleDocument? Document, string? Reason)
{
    /// <summary>
    /// Gets a value indicating whether the file can be edited.
    /// </summary>
    public bool Editable => Document is not null;
}
