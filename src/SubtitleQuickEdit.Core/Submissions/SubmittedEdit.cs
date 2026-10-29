namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// One edit of a submission, as stored. Text edits (which may also change the line's times) carry the line's position,
/// times and text as the user saw them, so the edit can be found again if the file changes before an admin approves it.
/// </summary>
public sealed record SubmittedEdit
{
    /// <summary>
    /// Gets the kind of edit.
    /// </summary>
    public SubmittedEditKind Kind { get; init; }

    /// <summary>
    /// Gets the 1-based position of the edited line when it was submitted (text edits).
    /// </summary>
    public int? CuePosition { get; init; }

    /// <summary>
    /// Gets the start time of the edited line when it was submitted, in milliseconds (text edits).
    /// </summary>
    public long? CueStartMs { get; init; }

    /// <summary>
    /// Gets the end time of the edited line when it was submitted, in milliseconds (text edits; null in submissions
    /// made before 1.3).
    /// </summary>
    public long? CueEndMs { get; init; }

    /// <summary>
    /// Gets the proposed start time of the edited line, or null when its times stay (text edits).
    /// </summary>
    public long? NewStartMs { get; init; }

    /// <summary>
    /// Gets the proposed end time of the edited line, or null when its times stay (text edits).
    /// </summary>
    public long? NewEndMs { get; init; }

    /// <summary>
    /// Gets the line's text when it was submitted (text edits). Line breaks are <c>\n</c>.
    /// </summary>
    public string? OldText { get; init; }

    /// <summary>
    /// Gets the submitted text (text edits). Line breaks are <c>\n</c>.
    /// </summary>
    public string? NewText { get; init; }

    /// <summary>
    /// Gets the client's id of a new line (new lines).
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Gets the start time of a new line in milliseconds (new lines).
    /// </summary>
    public long? StartMs { get; init; }

    /// <summary>
    /// Gets the end time of a new line in milliseconds (new lines).
    /// </summary>
    public long? EndMs { get; init; }

    /// <summary>
    /// Gets the text of a new line (new lines). Line breaks are <c>\n</c>.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets what happened to the edit.
    /// </summary>
    public EditOutcome Outcome { get; init; }

    /// <summary>
    /// Gets why the edit was not applied, when it was a conflict.
    /// </summary>
    public string? Message { get; init; }
}
