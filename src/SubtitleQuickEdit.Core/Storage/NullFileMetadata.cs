namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// <see cref="IFileMetadata"/> for platforms without Unix ownership: nothing to read, nothing to match, so
/// the writer always takes the plain rename path. Calls no native code.
/// </summary>
public sealed class NullFileMetadata : IFileMetadata
{
    /// <inheritdoc />
    public FileOwnership? Get(string path) => null;

    /// <inheritdoc />
    public bool TryMatch(string path, FileOwnership target) => true;
}
