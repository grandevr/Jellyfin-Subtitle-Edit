namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// Where a submission stands.
/// </summary>
public enum SubmissionState
{
    /// <summary>Waiting for an admin.</summary>
    Pending,

    /// <summary>Every edit was written to the file.</summary>
    Approved,

    /// <summary>Some edits were written; the rest were not chosen or no longer matched the file.</summary>
    PartlyApproved,

    /// <summary>An admin refused it; nothing was written.</summary>
    Rejected,

    /// <summary>The user took it back before an admin decided; nothing was written.</summary>
    Withdrawn,
}
