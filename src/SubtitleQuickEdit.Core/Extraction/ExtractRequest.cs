using System;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// An embedded track to write next to its video.
/// </summary>
public sealed record ExtractRequest
{
    /// <summary>
    /// Gets the library item id (hex).
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Gets the index of the embedded stream.
    /// </summary>
    public int SourceStreamIndex { get; init; }

    /// <summary>
    /// Gets the video file.
    /// </summary>
    public required string VideoPath { get; init; }

    /// <summary>
    /// Gets the file Jellyfin extracted the track to; its extension decides the format.
    /// </summary>
    public required string ExtractedPath { get; init; }

    /// <summary>
    /// Gets the track's content, as Jellyfin extracted it.
    /// </summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>
    /// Gets the track's language, or null.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the track's default, forced and hearing-impaired flags.
    /// </summary>
    public TrackDisposition Disposition { get; init; }

    /// <summary>
    /// Gets a value indicating whether an existing file of the target name is to be replaced.
    /// </summary>
    public bool Replace { get; init; }

    /// <summary>
    /// Gets the SHA-256 (hex) the caller saw for the file to be replaced. Required with <see cref="Replace"/>.
    /// </summary>
    public string? ExpectedExistingHash { get; init; }

    /// <summary>
    /// Gets the stream index of the file to be replaced, or -1 when it is not in the library. Names its backup.
    /// </summary>
    public int ExistingStreamIndex { get; init; } = -1;

    /// <summary>
    /// Gets the Jellyfin id of the user.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Gets the user's name.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user is an admin.
    /// </summary>
    public bool IsAdmin { get; init; }
}
