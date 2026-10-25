using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// A submission, as the client shows it.
/// </summary>
public class SubmissionDto
{
    /// <summary>
    /// Gets or sets the submission id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the submitter's id.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the submitter's name.
    /// </summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's name (with the series and episode number for an episode), or null when the item is gone.
    /// </summary>
    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the subtitle stream index.
    /// </summary>
    [JsonPropertyName("streamIndex")]
    public int StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the subtitle stream's language and title, or null when the stream is gone.
    /// </summary>
    [JsonPropertyName("streamLabel")]
    public string? StreamLabel { get; set; }

    /// <summary>
    /// Gets or sets the hash of the file the changes were made against.
    /// </summary>
    [JsonPropertyName("baseHash")]
    public string BaseHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the submitter's note.
    /// </summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets when it was submitted.
    /// </summary>
    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    /// <summary>
    /// Gets or sets the state: <c>Pending</c>, <c>Approved</c>, <c>PartlyApproved</c>, <c>Rejected</c> or <c>Withdrawn</c>.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was decided or withdrawn.
    /// </summary>
    [JsonPropertyName("decided")]
    public DateTimeOffset? Decided { get; set; }

    /// <summary>
    /// Gets or sets who decided or withdrew it.
    /// </summary>
    [JsonPropertyName("decidedBy")]
    public string? DecidedBy { get; set; }

    /// <summary>
    /// Gets or sets the admin's reason for a rejection.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the changes.
    /// </summary>
    [JsonPropertyName("edits")]
    public IReadOnlyList<SubmittedEditDto> Edits { get; set; } = Array.Empty<SubmittedEditDto>();
}
