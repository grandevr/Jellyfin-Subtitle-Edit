using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The outcome of an approval.
/// </summary>
public class ApproveResponse
{
    /// <summary>
    /// Gets or sets the submission as it now is. When none of the chosen changes could be applied it is still
    /// pending, with the conflicting changes marked.
    /// </summary>
    [JsonPropertyName("submission")]
    public SubmissionDto Submission { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether anything was written to the subtitle.
    /// </summary>
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    /// <summary>
    /// Gets or sets the file's new hash, when something was written.
    /// </summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>
    /// Gets or sets how the file was written, when something was written.
    /// </summary>
    [JsonPropertyName("writeMethod")]
    public string? WriteMethod { get; set; }
}
