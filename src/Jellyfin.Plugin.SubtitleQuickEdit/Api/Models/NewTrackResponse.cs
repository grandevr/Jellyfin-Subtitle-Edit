using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The result of starting a new subtitle track.
/// </summary>
public class NewTrackResponse
{
    /// <summary>
    /// Gets or sets the name of the file that was written, next to the video.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the new file's hash.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets each line's number in the new file, by its client id.
    /// </summary>
    [JsonPropertyName("addedCues")]
    public IReadOnlyDictionary<string, int> AddedCues { get; set; } = new Dictionary<string, int>();

    /// <summary>
    /// Gets or sets the new file's subtitle stream index, or null when the library has not picked it up yet.
    /// </summary>
    [JsonPropertyName("streamIndex")]
    public int? StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the library refresh is still running; list the item's subtitles again later.
    /// </summary>
    [JsonPropertyName("refreshPending")]
    public bool RefreshPending { get; set; }
}
