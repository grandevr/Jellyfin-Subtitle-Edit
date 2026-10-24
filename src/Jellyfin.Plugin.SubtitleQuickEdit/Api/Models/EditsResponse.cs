using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The result of a successful save.
/// </summary>
public class EditsResponse
{
    /// <summary>
    /// Gets or sets the SHA-256 of the file as saved; use it as the next <c>baseHash</c>.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the 1-based positions, in the saved file, of every edited or added cue.
    /// </summary>
    [JsonPropertyName("savedCueIndexes")]
    public IReadOnlyList<int> SavedCueIndexes { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets, for each added cue's client id, its position (and number) in the saved file.
    /// </summary>
    [JsonPropertyName("addedCues")]
    public IReadOnlyDictionary<string, int> AddedCues { get; set; } = new Dictionary<string, int>();

    /// <summary>
    /// Gets or sets how the file was replaced: <c>AtomicRename</c> or <c>InPlaceCopy</c>.
    /// </summary>
    [JsonPropertyName("writeMethod")]
    public string WriteMethod { get; set; } = string.Empty;
}
