using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// One cue of a subtitle file.
/// </summary>
public class CueDto
{
    /// <summary>
    /// Gets or sets the cue's 1-based position in the file.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the start time in milliseconds.
    /// </summary>
    [JsonPropertyName("start")]
    public long Start { get; set; }

    /// <summary>
    /// Gets or sets the end time in milliseconds.
    /// </summary>
    [JsonPropertyName("end")]
    public long End { get; set; }

    /// <summary>
    /// Gets or sets the text, with <c>\n</c> as the line break.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
