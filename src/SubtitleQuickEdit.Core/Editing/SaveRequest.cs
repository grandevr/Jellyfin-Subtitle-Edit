using System.Collections.Generic;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// A request to save edits to one subtitle file.
/// </summary>
/// <param name="ItemId">The library item id, used for the backup name.</param>
/// <param name="StreamIndex">The subtitle stream index, used for the backup name.</param>
/// <param name="Path">The subtitle file, already resolved by the caller from the item's own streams.</param>
/// <param name="BaseHash">SHA-256 (hex) of the file as the client saw it.</param>
/// <param name="Edits">The edits.</param>
/// <param name="RuntimeMs">The item's runtime in milliseconds, or null when unknown.</param>
/// <param name="KeepRestorePoint">Whether to keep the content from before this save as a restore point (the first save of an editing session).</param>
public sealed record SaveRequest(
    string ItemId,
    int StreamIndex,
    string Path,
    string BaseHash,
    IReadOnlyList<SubtitleEdit> Edits,
    long? RuntimeMs,
    bool KeepRestorePoint = false);
