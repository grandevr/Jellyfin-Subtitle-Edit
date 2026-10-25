using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// The result of resolving an item's subtitle stream to a file.
/// </summary>
/// <param name="Stream">The stream, when found.</param>
/// <param name="Path">The subtitle file's path, when the stream is an external file in an editable format.</param>
/// <param name="Status">HTTP status to answer with when there is a problem; 200 when resolved.</param>
/// <param name="Code">Error code when there is a problem.</param>
/// <param name="Message">Error message when there is a problem.</param>
public sealed record TrackResolution(MediaStream? Stream, string? Path, int Status, string? Code, string? Message)
{
    /// <summary>
    /// Gets a value indicating whether the stream resolved to a subtitle file path.
    /// </summary>
    public bool Resolved => Path is not null;
}
