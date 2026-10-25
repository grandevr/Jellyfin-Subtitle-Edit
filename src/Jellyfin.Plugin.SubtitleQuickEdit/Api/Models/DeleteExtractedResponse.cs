using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The result of deleting an extracted file.
/// </summary>
public class DeleteExtractedResponse
{
    /// <summary>
    /// Gets or sets the name of the file that was deleted.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether its last content was kept in the backups (false when the file was already gone).
    /// </summary>
    [JsonPropertyName("backedUp")]
    public bool BackedUp { get; set; }
}
