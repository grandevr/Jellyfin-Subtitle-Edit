using System.Collections.Generic;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// An admin's approval of a submission.
/// </summary>
/// <param name="Id">The submission id.</param>
/// <param name="Path">The subtitle file of the submission's item and stream, resolved by the caller.</param>
/// <param name="AdminName">The admin's name, kept with the decision.</param>
/// <param name="Chosen">The 0-based indexes of the edits to apply, or null for all of them.</param>
/// <param name="RuntimeMs">The item's runtime in milliseconds, or null when unknown.</param>
public sealed record ApproveRequest(
    string Id,
    string Path,
    string AdminName,
    IReadOnlyList<int>? Chosen,
    long? RuntimeMs);
