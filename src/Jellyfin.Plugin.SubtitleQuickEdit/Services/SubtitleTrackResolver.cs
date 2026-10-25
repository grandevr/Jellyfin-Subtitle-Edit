using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using SubtitleQuickEdit.Core.Subtitles;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// Finds library items and turns an item's subtitle stream into a file path from Jellyfin's own library data. The
/// client never sends a path.
/// </summary>
public sealed class SubtitleTrackResolver
{
    private readonly ILibraryManager libraryManager;
    private readonly IMediaSourceManager mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleTrackResolver"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    public SubtitleTrackResolver(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
    {
        this.libraryManager = libraryManager;
        this.mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Finds a video item (an episode, movie, ...) by id.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The item, or null when there is none or it is not a video.</returns>
    public BaseItem? FindVideo(Guid itemId) => libraryManager.GetItemById(itemId) as Video;

    /// <summary>
    /// Gets the item's subtitle streams.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The subtitle streams, in index order.</returns>
    public IReadOnlyList<MediaStream> GetSubtitleStreams(BaseItem item) =>
        mediaSourceManager.GetMediaStreams(item.Id).Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();

    /// <summary>
    /// Resolves a subtitle stream of an item to an external subtitle file in a format the editor handles, or says why
    /// that is not possible.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="streamIndex">The stream index.</param>
    /// <returns>The resolution.</returns>
    public TrackResolution Resolve(BaseItem item, int streamIndex)
    {
        MediaStream? stream = GetSubtitleStreams(item).FirstOrDefault(s => s.Index == streamIndex);
        if (stream is null)
        {
            return new TrackResolution(null, null, 404, "StreamNotFound", "The item has no subtitle stream with that index.");
        }

        if (!stream.IsExternal)
        {
            return new TrackResolution(stream, null, 400, "NotEditable", "Embedded subtitle tracks cannot be edited.");
        }

        string? path = stream.Path;
        if (string.IsNullOrEmpty(path) || path.Contains("://", StringComparison.Ordinal) || !Path.IsPathRooted(path))
        {
            return new TrackResolution(stream, null, 400, "NotEditable", "The subtitle is not a local file.");
        }

        if (!SubtitleFormats.IsEditable(path))
        {
            return new TrackResolution(stream, null, 400, "NotEditable", SubtitleFormats.UnsupportedMessage);
        }

        return new TrackResolution(stream, path, 200, null, null);
    }
}
