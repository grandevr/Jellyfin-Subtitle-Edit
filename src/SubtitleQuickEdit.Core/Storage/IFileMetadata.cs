namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Reads and copies file ownership. Abstracted so tests can simulate a group the process cannot join.
/// </summary>
public interface IFileMetadata
{
    /// <summary>
    /// Reads a file's owner, group and mode.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The ownership, or null when the platform does not support it.</returns>
    FileOwnership? Get(string path);

    /// <summary>
    /// Makes a file's owner, group and mode equal to <paramref name="target"/>.
    /// </summary>
    /// <param name="path">The file to change.</param>
    /// <param name="target">The wanted ownership.</param>
    /// <returns>True when the owner, group and mode now all match; false when the owner or group could not be matched.</returns>
    bool TryMatch(string path, FileOwnership target);
}
