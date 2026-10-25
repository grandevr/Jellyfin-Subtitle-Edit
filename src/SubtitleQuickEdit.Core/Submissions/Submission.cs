using System;
using System.Collections.Generic;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// A user's proposed changes to one subtitle track, waiting for or decided by an admin. Stored as one JSON file.
/// </summary>
public sealed record Submission
{
    /// <summary>
    /// Gets the submission id: 32 lowercase hex characters.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the Jellyfin id of the user who sent it.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Gets the user's name when it was sent.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// Gets the library item id (hex).
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Gets the subtitle stream index.
    /// </summary>
    public int StreamIndex { get; init; }

    /// <summary>
    /// Gets the SHA-256 (hex) of the file the edits were made against.
    /// </summary>
    public required string BaseHash { get; init; }

    /// <summary>
    /// Gets the edits, in the order they were sent.
    /// </summary>
    public IReadOnlyList<SubmittedEdit> Edits { get; init; } = [];

    /// <summary>
    /// Gets the user's note, or null.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Gets when it was sent.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Gets where it stands.
    /// </summary>
    public SubmissionState State { get; init; }

    /// <summary>
    /// Gets when it was decided or withdrawn, or null while pending.
    /// </summary>
    public DateTimeOffset? DecidedUtc { get; init; }

    /// <summary>
    /// Gets the name of the admin who decided it (or of the user who withdrew it), or null while pending.
    /// </summary>
    public string? DecidedBy { get; init; }

    /// <summary>
    /// Gets the admin's reason for a rejection, or null.
    /// </summary>
    public string? Reason { get; init; }
}
