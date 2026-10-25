using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The cues of a subtitle file and the hash edits must be based on.
/// </summary>
public class CuesResponse
{
    /// <summary>
    /// Gets or sets the SHA-256 of the file (lower-case hex). Send it back as <c>baseHash</c> when saving.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the detected text encoding, for example <c>utf-8</c> or <c>windows-1250</c>.
    /// </summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's format: <c>srt</c>, <c>vtt</c> or <c>ass</c>.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "srt";

    /// <summary>
    /// Gets or sets how precisely the file stores times, in milliseconds: 1, or 10 for ASS/SSA.
    /// </summary>
    [JsonPropertyName("timePrecisionMs")]
    public int TimePrecisionMs { get; set; } = 1;

    /// <summary>
    /// Gets or sets the cues in file order.
    /// </summary>
    [JsonPropertyName("cues")]
    public IReadOnlyList<CueDto> Cues { get; set; } = Array.Empty<CueDto>();
}
