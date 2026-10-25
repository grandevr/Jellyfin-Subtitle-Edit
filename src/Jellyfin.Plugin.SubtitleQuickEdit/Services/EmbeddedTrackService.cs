using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using SubtitleQuickEdit.Core.Extraction;
using SubtitleQuickEdit.Core.Subtitles;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// Reads embedded text subtitle tracks through Jellyfin's own subtitle extraction, and refreshes an item so that
/// Jellyfin picks up a subtitle file that was added next to it or removed.
/// </summary>
public sealed class EmbeddedTrackService
{
    private static readonly TimeSpan PickUpWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PickUpPoll = TimeSpan.FromMilliseconds(500);

    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ISubtitleEncoder subtitleEncoder;
    private readonly IProviderManager providerManager;
    private readonly IFileSystem fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedTrackService"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="subtitleEncoder">Jellyfin's subtitle encoder, which extracts embedded tracks into its cache.</param>
    /// <param name="providerManager">The provider manager, for refreshing items.</param>
    /// <param name="fileSystem">The file system, for the refresh's directory service.</param>
    public EmbeddedTrackService(
        IMediaSourceManager mediaSourceManager,
        ISubtitleEncoder subtitleEncoder,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        this.mediaSourceManager = mediaSourceManager;
        this.subtitleEncoder = subtitleEncoder;
        this.providerManager = providerManager;
        this.fileSystem = fileSystem;
    }

    /// <summary>
    /// Says whether a stream is an embedded text track that can be extracted.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <returns>True for embedded text tracks (not image tracks such as PGS or VobSub).</returns>
    public static bool IsExtractable(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Type == MediaStreamType.Subtitle && !stream.IsExternal && stream.IsTextSubtitleStream;
    }

    /// <summary>
    /// Gets the extension the extracted file will have, from the track's codec, the way Jellyfin extracts it: ASS and
    /// SSA as they are, every other text codec as SubRip.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The extension.</returns>
    public static string ExtensionFor(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Codec?.ToLowerInvariant() switch
        {
            "ass" => ".ass",
            "ssa" => ".ssa",
            _ => ".srt",
        };
    }

    /// <summary>
    /// Gets the track's default, forced and hearing-impaired flags.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The flags.</returns>
    public static TrackDisposition DispositionOf(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return (stream.IsDefault ? TrackDisposition.Default : TrackDisposition.None)
            | (stream.IsForced ? TrackDisposition.Forced : TrackDisposition.None)
            | (stream.IsHearingImpaired ? TrackDisposition.HearingImpaired : TrackDisposition.None);
    }

    /// <summary>
    /// Gets the local video file of an item, or null when it has none (a remote stream, a placeholder).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The path, or null.</returns>
    public static string? LocalVideoPath(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? path = item.Path;
        return string.IsNullOrEmpty(path) || path.Contains("://", StringComparison.Ordinal) || !Path.IsPathRooted(path) || !File.Exists(path)
            ? null
            : path;
    }

    /// <summary>
    /// Has Jellyfin extract an embedded track into its cache (or reuse what it extracted before) and reads it.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="stream">The embedded stream.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>The cache file's path and content; the content is cut one byte past the size limit, so a too large track can be told.</returns>
    /// <exception cref="InvalidOperationException">The item has no local media source with that stream.</exception>
    public async Task<(string Path, byte[] Content)> ReadAsync(BaseItem item, MediaStream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stream);
        string id = item.Id.ToString("N", CultureInfo.InvariantCulture);
        IReadOnlyList<MediaSourceInfo> sources = mediaSourceManager.GetStaticMediaSources(item, false, null);
        MediaSourceInfo source = sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) && HasStream(s, stream.Index))
            ?? sources.FirstOrDefault(s => HasStream(s, stream.Index))
            ?? throw new InvalidOperationException("The item has no media source with that subtitle stream.");
        if (source.Protocol != MediaProtocol.File)
        {
            throw new InvalidOperationException("The video is not a local file.");
        }

        MediaStream sourceStream = source.MediaStreams.First(s => s.Index == stream.Index);
        string path = await subtitleEncoder.GetSubtitleFilePath(sourceStream, source, cancellationToken).ConfigureAwait(false);

        int limit = SubtitleLimits.MaxFileBytes + 1;
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (file.ConfigureAwait(false))
        {
            var buffer = new byte[(int)Math.Min(file.Length, limit)];
            await file.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return (path, buffer);
        }
    }

    /// <summary>
    /// Refreshes an item so that Jellyfin picks up a subtitle file added next to it or removed, then waits a little for
    /// a file to show up as one of its streams.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="subtitlePath">A subtitle file that should now be one of its streams, or null after a removal.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>The file's stream index, or null when it is not one of the item's streams yet.</returns>
    public async Task<int?> RefreshAsync(BaseItem item, string? subtitlePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem));
        await providerManager.RefreshFullItem(item, options, cancellationToken).ConfigureAwait(false);
        if (subtitlePath is null)
        {
            return null;
        }

        DateTime until = DateTime.UtcNow + PickUpWait;
        while (true)
        {
            int? index = FindExternal(item, subtitlePath);
            if (index is not null || DateTime.UtcNow >= until)
            {
                return index;
            }

            await Task.Delay(PickUpPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the external subtitle stream of an item that is a given file.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="subtitlePath">The file.</param>
    /// <returns>Its stream index, or null.</returns>
    public int? FindExternal(BaseItem item, string subtitlePath)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(subtitlePath);
        return mediaSourceManager.GetMediaStreams(item.Id)
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && string.Equals(s.Path, subtitlePath, StringComparison.Ordinal))
            .Select(s => (int?)s.Index)
            .FirstOrDefault();
    }

    private static bool HasStream(MediaSourceInfo source, int index) => source.MediaStreams?.Any(s => s.Index == index) == true;
}
