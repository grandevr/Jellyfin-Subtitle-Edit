namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// The kind of a submitted edit.
/// </summary>
public enum SubmittedEditKind
{
    /// <summary>New text for an existing line.</summary>
    Edit,

    /// <summary>A new line.</summary>
    Add,
}
