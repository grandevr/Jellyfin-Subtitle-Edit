using System;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// Why a set of edits was refused. The plugin maps these to HTTP status codes.
/// </summary>
public enum SrtEditError
{
    /// <summary>The edit itself is invalid (empty text, bad times, duplicate edit, ...). HTTP 400.</summary>
    InvalidEdit,

    /// <summary>The cue position does not exist in the file. HTTP 400.</summary>
    CueNotFound,

    /// <summary>The cue's current text differs from the edit's old text. HTTP 409.</summary>
    TextMismatch,

    /// <summary>A character cannot be represented in the file's encoding. HTTP 422.</summary>
    Unrepresentable,

    /// <summary>The result failed the post-edit self-check; nothing must be written. HTTP 500.</summary>
    VerificationFailed,
}

/// <summary>
/// Thrown when edits cannot be applied. Nothing has been changed when this is thrown.
/// </summary>
public sealed class SrtEditException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SrtEditException"/> class.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <param name="message">A message fit to show to the user.</param>
    /// <param name="cuePosition">The 1-based cue position the problem concerns, if any.</param>
    public SrtEditException(SrtEditError error, string message, int? cuePosition = null)
        : base(message)
    {
        Error = error;
        CuePosition = cuePosition;
    }

    /// <summary>
    /// Gets the reason.
    /// </summary>
    public SrtEditError Error { get; }

    /// <summary>
    /// Gets the 1-based cue position the problem concerns, if any.
    /// </summary>
    public int? CuePosition { get; }

    /// <summary>
    /// Gets the client id of the added line the problem concerns, if it concerns one.
    /// </summary>
    public string? ClientId { get; init; }
}
