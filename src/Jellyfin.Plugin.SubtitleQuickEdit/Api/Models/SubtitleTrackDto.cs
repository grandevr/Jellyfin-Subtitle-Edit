using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// One subtitle stream of an item and whether it can be edited.
/// </summary>
public class SubtitleTrackDto
{
    /// <summary>
    /// Gets or sets the stream index (the index Jellyfin uses for the item's streams).
    /// </summary>
    [JsonPropertyName("streamIndex")]
    public int StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the language code, if known.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the track title, if any.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the track is an external file (as opposed to embedded in the video).
    /// </summary>
    [JsonPropertyName("external")]
    public bool External { get; set; }

    /// <summary>
    /// Gets or sets the subtitle format, for example <c>srt</c>, <c>ass</c> or the codec name for embedded tracks.
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the track can be edited.
    /// </summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; set; }

    /// <summary>
    /// Gets or sets why the track cannot be edited, when it cannot.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
