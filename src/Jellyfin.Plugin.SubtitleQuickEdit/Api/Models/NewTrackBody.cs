using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of a request that starts a new subtitle track.
/// </summary>
public class NewTrackBody
{
    /// <summary>
    /// Gets or sets the language for the file name (<c>en</c>, <c>eng</c>, ...), or null for none.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the lines of the new track, each an <c>add</c> as in an Edits request.
    /// </summary>
    [JsonPropertyName("edits")]
    public IReadOnlyList<EditDto> Edits { get; set; } = Array.Empty<EditDto>();
}
