using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of an extract request.
/// </summary>
public class ExtractBody
{
    /// <summary>
    /// Gets or sets a value indicating whether an existing file of the same name is to be replaced (admins only).
    /// </summary>
    [JsonPropertyName("replace")]
    public bool Replace { get; set; }

    /// <summary>
    /// Gets or sets the hash of the existing file as the preview showed it; needed with replace.
    /// </summary>
    [JsonPropertyName("expectedExistingHash")]
    public string? ExpectedExistingHash { get; set; }
}
