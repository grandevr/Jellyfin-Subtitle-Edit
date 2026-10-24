using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The result of a successful restore.
/// </summary>
public class RestoreResponse
{
    /// <summary>
    /// Gets or sets the SHA-256 of the file now; use it as the next <c>baseHash</c>.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the file was written (false when it already had that content).
    /// </summary>
    [JsonPropertyName("restored")]
    public bool Restored { get; set; }

    /// <summary>
    /// Gets or sets how the file was replaced, or null when nothing was written.
    /// </summary>
    [JsonPropertyName("writeMethod")]
    public string? WriteMethod { get; set; }
}
