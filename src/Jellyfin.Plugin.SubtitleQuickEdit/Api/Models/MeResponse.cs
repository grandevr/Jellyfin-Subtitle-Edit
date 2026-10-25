using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// What the caller may do with the editor, so the client can show the right mode.
/// </summary>
public class MeResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the caller is an administrator (may review submissions).
    /// </summary>
    [JsonPropertyName("isAdmin")]
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may save edits directly.
    /// </summary>
    [JsonPropertyName("canEdit")]
    public bool CanEdit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may submit edits for review.
    /// </summary>
    [JsonPropertyName("canSubmit")]
    public bool CanSubmit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether user submissions are on (admins use it to show the review list).
    /// </summary>
    [JsonPropertyName("submissionsOn")]
    public bool SubmissionsOn { get; set; }

    /// <summary>
    /// Gets or sets, for admins, how many submissions wait for review; null for everyone else.
    /// </summary>
    [JsonPropertyName("pendingReviews")]
    public int? PendingReviews { get; set; }

    /// <summary>
    /// Gets or sets the longest note or reason allowed, in characters.
    /// </summary>
    [JsonPropertyName("maxNoteLength")]
    public int MaxNoteLength { get; set; }
}
