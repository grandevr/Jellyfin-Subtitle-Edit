namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// Why a submission request was refused. The plugin maps these to HTTP status codes.
/// </summary>
public enum SubmissionError
{
    /// <summary>No such submission, or not one the caller may see. HTTP 404.</summary>
    NotFound,

    /// <summary>The submission was already decided or withdrawn. HTTP 409.</summary>
    NotPending,

    /// <summary>The user already has the most pending submissions allowed. HTTP 429.</summary>
    TooManyPending,

    /// <summary>The note or the reason is too long. HTTP 400.</summary>
    TextTooLong,

    /// <summary>The chosen edits are not valid indexes of the submission. HTTP 400.</summary>
    InvalidChoice,

    /// <summary>The submission could not be read or written in the plugin's data folder. HTTP 500.</summary>
    StorageFailed,
}
