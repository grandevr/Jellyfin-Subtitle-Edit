using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// Where a new subtitle track would be written, and whether a file of that name is already there.
/// </summary>
public class NewTrackPreviewResponse
{
    /// <summary>
    /// Gets or sets the name of the file the track would be written to, next to the video.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language code that goes in the file name, or null when the name has none.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a file of that name already exists.
    /// </summary>
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the existing file's subtitle stream index, or null when it is not one of the item's tracks.
    /// </summary>
    [JsonPropertyName("existingStreamIndex")]
    public int? ExistingStreamIndex { get; set; }
}
