using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// One edit in a save request: <c>type</c> is <c>edit</c> (replace a cue's text, and its times when <c>startMs</c> and
/// <c>endMs</c> are given) or <c>add</c> (a new cue).
/// </summary>
public class EditDto
{
    /// <summary>
    /// Gets or sets the edit type: <c>edit</c> or <c>add</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the cue's 1-based position.
    /// </summary>
    [JsonPropertyName("cueIndex")]
    public int? CueIndex { get; set; }

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the text the client saw.
    /// </summary>
    [JsonPropertyName("oldText")]
    public string? OldText { get; set; }

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the replacement text.
    /// </summary>
    [JsonPropertyName("newText")]
    public string? NewText { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, an id chosen by the client.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the start time in milliseconds; for <c>edit</c>, the new start time, or null to
    /// keep the cue's times.
    /// </summary>
    [JsonPropertyName("startMs")]
    public long? StartMs { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the end time in milliseconds; for <c>edit</c>, the new end time, or null to keep the
    /// cue's times.
    /// </summary>
    [JsonPropertyName("endMs")]
    public long? EndMs { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the cue text.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
