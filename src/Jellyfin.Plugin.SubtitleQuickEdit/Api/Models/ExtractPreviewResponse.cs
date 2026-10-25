using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// Where an embedded track would be extracted to, and what is there now.
/// </summary>
public class ExtractPreviewResponse
{
    /// <summary>
    /// Gets or sets the name of the file the track would be written to, next to the video.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the format of the extracted file: <c>srt</c> or <c>ass</c>.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of lines in the track.
    /// </summary>
    [JsonPropertyName("cueCount")]
    public int CueCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a file of that name already exists.
    /// </summary>
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the existing file's hash, or null.
    /// </summary>
    [JsonPropertyName("existingHash")]
    public string? ExistingHash { get; set; }

    /// <summary>
    /// Gets or sets the existing file's subtitle stream index in the library, or null when it is not in the library (yet).
    /// </summary>
    [JsonPropertyName("existingStreamIndex")]
    public int? ExistingStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the existing file was made by an earlier extraction.
    /// </summary>
    [JsonPropertyName("existingIsExtracted")]
    public bool ExistingIsExtracted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may replace the existing file (admins).
    /// </summary>
    [JsonPropertyName("canReplace")]
    public bool CanReplace { get; set; }

    /// <summary>
    /// Gets or sets the track's lines, for the side-by-side comparison; only when a file exists and the caller may replace it.
    /// </summary>
    [JsonPropertyName("cues")]
    public IReadOnlyList<CueDto>? Cues { get; set; }

    /// <summary>
    /// Gets or sets the existing file's lines, as for <see cref="Cues"/>; null also when it cannot be parsed.
    /// </summary>
    [JsonPropertyName("existingCues")]
    public IReadOnlyList<CueDto>? ExistingCues { get; set; }

    /// <summary>
    /// Gets or sets why the existing file's lines cannot be shown, when they cannot.
    /// </summary>
    [JsonPropertyName("existingReason")]
    public string? ExistingReason { get; set; }
}
