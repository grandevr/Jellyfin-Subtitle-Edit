using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of every error answer: a stable code and a message fit to show to the user.
/// </summary>
public class ErrorResponse
{
    /// <summary>
    /// Gets or sets the error code, for example <c>FileChanged</c>, <c>TextMismatch</c>, <c>NotWritable</c>.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cue position the problem concerns, if any.
    /// </summary>
    [JsonPropertyName("cueIndex")]
    public int? CueIndex { get; set; }

    /// <summary>
    /// Gets or sets, for <c>FileChanged</c>, the file's current hash.
    /// </summary>
    [JsonPropertyName("currentHash")]
    public string? CurrentHash { get; set; }
}
