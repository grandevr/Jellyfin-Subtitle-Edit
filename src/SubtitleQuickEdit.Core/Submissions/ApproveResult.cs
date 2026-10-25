using SubtitleQuickEdit.Core.Editing;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// The outcome of an approval.
/// </summary>
/// <param name="Submission">The submission as stored afterwards: decided, or still pending when no chosen edit could be applied.</param>
/// <param name="Save">The save, or null when nothing was written (every chosen edit was a conflict).</param>
public sealed record ApproveResult(Submission Submission, SaveResult? Save);
