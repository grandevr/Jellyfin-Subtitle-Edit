using System;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// Thrown when a submission request is refused. Nothing has been changed when this is thrown.
/// </summary>
public sealed class SubmissionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubmissionException"/> class.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <param name="message">A message fit to show to the user.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public SubmissionException(SubmissionError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Gets the reason.
    /// </summary>
    public SubmissionError Error { get; }
}
