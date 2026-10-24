using System;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Why a write was refused or failed.
/// </summary>
public enum SubtitleWriteError
{
    /// <summary>The file changed since the client loaded it. HTTP 409. Nothing was written.</summary>
    Conflict,

    /// <summary>The folder or file is not writable by the process. Nothing was written.</summary>
    NotWritable,

    /// <summary>A verification step failed. A recoverable copy may be left behind (see <see cref="SubtitleWriteException.LeftoverPath"/>).</summary>
    VerificationFailed,

    /// <summary>A leftover file from an earlier interrupted save cannot be resolved automatically.</summary>
    LeftoverUnrecoverable,

    /// <summary>Another I/O problem.</summary>
    Io,
}

/// <summary>
/// Thrown by <see cref="SafeFileWriter"/>.
/// </summary>
public sealed class SubtitleWriteException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleWriteException"/> class.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <param name="message">A message fit to show to the user.</param>
    /// <param name="currentHash">For conflicts, the file's current hash.</param>
    /// <param name="leftoverPath">A file left on disk that holds the intended content or needs attention.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public SubtitleWriteException(
        SubtitleWriteError error,
        string message,
        string? currentHash = null,
        string? leftoverPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        CurrentHash = currentHash;
        LeftoverPath = leftoverPath;
    }

    /// <summary>
    /// Gets the reason.
    /// </summary>
    public SubtitleWriteError Error { get; }

    /// <summary>
    /// Gets the file's current hash for <see cref="SubtitleWriteError.Conflict"/>.
    /// </summary>
    public string? CurrentHash { get; }

    /// <summary>
    /// Gets the path of a leftover file that holds the intended content or needs attention, if any.
    /// </summary>
    public string? LeftoverPath { get; }
}
