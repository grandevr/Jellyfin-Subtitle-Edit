using System;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// Thrown when no kept version of a subtitle has the content a restore asks for (it was never kept, or was pruned).
/// </summary>
public sealed class RestorePointNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RestorePointNotFoundException"/> class.
    /// </summary>
    /// <param name="message">What is missing, in words fit to show to the user.</param>
    public RestorePointNotFoundException(string message)
        : base(message)
    {
    }
}
