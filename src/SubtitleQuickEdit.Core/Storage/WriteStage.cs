namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// The points of a write at which <see cref="IWriteObserver"/> is told what happened. Tests throw from
/// these to simulate a crash.
/// </summary>
public enum WriteStage
{
    /// <summary>The temp file is fully written.</summary>
    TempWritten,

    /// <summary>The temp file was read back and its hash matches.</summary>
    TempVerified,

    /// <summary>Everything is ready; the next step changes the real file.</summary>
    BeforeRename,

    /// <summary>The temp file was renamed to its in-place marker name; the original is about to be overwritten.</summary>
    InPlaceStarted,

    /// <summary>One chunk of the in-place copy was written.</summary>
    InPlaceChunkWritten,
}
