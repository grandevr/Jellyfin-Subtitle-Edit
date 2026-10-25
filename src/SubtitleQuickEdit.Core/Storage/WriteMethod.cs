namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// How a write ended up on disk.
/// </summary>
public enum WriteMethod
{
    /// <summary>A temp file with matching owner, group and mode was renamed over the original.</summary>
    AtomicRename,

    /// <summary>The group or owner could not be matched, so the verified content was copied over the original in place.</summary>
    InPlaceCopy,

    /// <summary>A new file was created from a verified temp file; no existing file was touched.</summary>
    Created,
}
