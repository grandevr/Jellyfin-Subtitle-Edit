using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of a save request.
/// </summary>
public class EditsRequest
{
    /// <summary>
    /// Gets or sets the hash returned by the Cues call that the edits were made against.
    /// </summary>
    [JsonPropertyName("baseHash")]
    public string? BaseHash { get; set; }

    /// <summary>
    /// Gets or sets the edits.
    /// </summary>
    [JsonPropertyName("edits")]
    public IReadOnlyList<EditDto> Edits { get; set; } = Array.Empty<EditDto>();

    /// <summary>
    /// Gets or sets a value indicating whether the file as it is before this save is kept as a restore point (the
    /// client sets it on the first save of an editing session, so "Revert to original" can go back to it).
    /// </summary>
    [JsonPropertyName("keepRestorePoint")]
    public bool KeepRestorePoint { get; set; }
}
