using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// One change in a submission: <c>type</c> is <c>edit</c> (a line's new text) or <c>add</c> (a new line).
/// </summary>
public class SubmittedEditDto
{
    /// <summary>
    /// Gets or sets the change type: <c>edit</c> or <c>add</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the line's 1-based position when it was submitted.
    /// </summary>
    [JsonPropertyName("cueIndex")]
    public int? CueIndex { get; set; }

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the line's start time in milliseconds.
    /// </summary>
    [JsonPropertyName("cueStart")]
    public long? CueStart { get; set; }

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the line's text when it was submitted.
    /// </summary>
    [JsonPropertyName("oldText")]
    public string? OldText { get; set; }

    /// <summary>
    /// Gets or sets, for <c>edit</c>, the proposed text.
    /// </summary>
    [JsonPropertyName("newText")]
    public string? NewText { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the client's id for the line.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the start time in milliseconds.
    /// </summary>
    [JsonPropertyName("startMs")]
    public long? StartMs { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the end time in milliseconds.
    /// </summary>
    [JsonPropertyName("endMs")]
    public long? EndMs { get; set; }

    /// <summary>
    /// Gets or sets, for <c>add</c>, the text.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets what happened to the change: <c>Pending</c>, <c>Applied</c>, <c>Conflict</c> or <c>NotChosen</c>.
    /// </summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets, for a conflict, why the change could not be applied.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
