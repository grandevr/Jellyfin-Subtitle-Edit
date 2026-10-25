using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of a rejection.
/// </summary>
public class RejectBody
{
    /// <summary>
    /// Gets or sets an optional reason, shown to the user.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
