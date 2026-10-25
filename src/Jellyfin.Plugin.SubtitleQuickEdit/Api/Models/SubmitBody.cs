using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of a submission: the same edits as a save, plus an optional note for the admin.
/// </summary>
public class SubmitBody
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
    /// Gets or sets an optional note for the admin.
    /// </summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
