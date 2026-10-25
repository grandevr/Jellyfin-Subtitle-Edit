namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// What happened to one edit of a submission.
/// </summary>
public enum EditOutcome
{
    /// <summary>Not decided yet.</summary>
    Pending,

    /// <summary>Written to the file.</summary>
    Applied,

    /// <summary>The line it changes was changed or removed since it was submitted, so it was not written.</summary>
    Conflict,

    /// <summary>The admin approved other edits of the submission but not this one.</summary>
    NotChosen,
}
