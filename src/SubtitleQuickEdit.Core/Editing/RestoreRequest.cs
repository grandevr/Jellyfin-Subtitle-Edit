namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// A request to put one subtitle file back to a version kept in the backups.
/// </summary>
/// <param name="ItemId">The library item id, used for the backup names.</param>
/// <param name="StreamIndex">The subtitle stream index, used for the backup names.</param>
/// <param name="Path">The subtitle file, already resolved by the caller from the item's own streams.</param>
/// <param name="BaseHash">SHA-256 (hex) of the file as the client last saw it.</param>
/// <param name="TargetHash">SHA-256 (hex) of the version to go back to.</param>
public sealed record RestoreRequest(
    string ItemId,
    int StreamIndex,
    string Path,
    string BaseHash,
    string TargetHash);
