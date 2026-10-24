namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// How many backups of a subtitle file are kept.
/// </summary>
public enum BackupMode
{
    /// <summary>Only the original, from before the first save, is kept.</summary>
    OriginalOnly,

    /// <summary>The original plus the last N versions from before each later save.</summary>
    OriginalPlusLastN,
}
