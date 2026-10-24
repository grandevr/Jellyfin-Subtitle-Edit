using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of a restore request.
/// </summary>
public class RestoreBody
{
    /// <summary>
    /// Gets or sets the hash of the file as the client last saw it.
    /// </summary>
    [JsonPropertyName("baseHash")]
    public string? BaseHash { get; set; }

    /// <summary>
    /// Gets or sets the hash of the version to go back to (a hash the Cues call returned earlier).
    /// </summary>
    [JsonPropertyName("targetHash")]
    public string? TargetHash { get; set; }
}
