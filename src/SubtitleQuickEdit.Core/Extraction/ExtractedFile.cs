using System;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// A subtitle file that the plugin created from an embedded track. Stored as one JSON file.
/// </summary>
public sealed record ExtractedFile
{
    /// <summary>
    /// Gets the record id: 32 lowercase hex characters.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the library item id (hex).
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Gets the index of the embedded stream it was extracted from.
    /// </summary>
    public int SourceStreamIndex { get; init; }

    /// <summary>
    /// Gets the path of the file that was written.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the SHA-256 (hex) of the file as it was written. Later edits change the file, not this.
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Gets a value indicating whether an existing file was replaced (its old content is in the backups).
    /// </summary>
    public bool Replaced { get; init; }

    /// <summary>
    /// Gets the Jellyfin id of the user who extracted it.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Gets the user's name when it was extracted.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// Gets when it was extracted.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }
}
