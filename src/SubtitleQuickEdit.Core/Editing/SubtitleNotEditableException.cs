using System;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// Thrown when a subtitle file cannot be edited at all (not an .srt, missing, unreadable, not parseable).
/// </summary>
public sealed class SubtitleNotEditableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleNotEditableException"/> class.
    /// </summary>
    /// <param name="reason">Why, in words fit to show to the user.</param>
    public SubtitleNotEditableException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>
    /// Gets why the subtitle is not editable.
    /// </summary>
    public string Reason { get; }
}
