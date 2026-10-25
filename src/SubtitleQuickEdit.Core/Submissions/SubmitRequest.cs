using System.Collections.Generic;
using SubtitleQuickEdit.Core.Srt;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// A user's request to submit edits for review.
/// </summary>
/// <param name="UserId">The Jellyfin id of the user.</param>
/// <param name="UserName">The user's name, kept for the review page.</param>
/// <param name="ItemId">The library item id (hex).</param>
/// <param name="StreamIndex">The subtitle stream index.</param>
/// <param name="Path">The subtitle file, already resolved by the caller from the item's own streams.</param>
/// <param name="BaseHash">SHA-256 (hex) of the file as the user saw it.</param>
/// <param name="Edits">The edits, in the same shape as a save.</param>
/// <param name="Note">An optional note for the admin.</param>
/// <param name="RuntimeMs">The item's runtime in milliseconds, or null when unknown.</param>
/// <param name="MaxPending">How many pending submissions a user may have.</param>
public sealed record SubmitRequest(
    string UserId,
    string UserName,
    string ItemId,
    int StreamIndex,
    string Path,
    string BaseHash,
    IReadOnlyList<SrtEdit> Edits,
    string? Note,
    long? RuntimeMs,
    int MaxPending);
