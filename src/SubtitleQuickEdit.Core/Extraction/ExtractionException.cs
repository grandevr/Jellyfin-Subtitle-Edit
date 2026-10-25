using System;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// Thrown when an extraction or a deletion is refused. Nothing has been changed when this is thrown, except where
/// <see cref="ExtractionError.StorageFailed"/> says otherwise in its message.
/// </summary>
public sealed class ExtractionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractionException"/> class.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <param name="message">A message fit to show to the user.</param>
    /// <param name="currentHash">For <see cref="ExtractionError.FileExists"/> and <see cref="ExtractionError.FileChanged"/>, the existing file's hash.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public ExtractionException(ExtractionError error, string message, string? currentHash = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        CurrentHash = currentHash;
    }

    /// <summary>
    /// Gets the reason.
    /// </summary>
    public ExtractionError Error { get; }

    /// <summary>
    /// Gets the existing file's hash, when there is one.
    /// </summary>
    public string? CurrentHash { get; }
}
