using System;
using System.IO;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// <see cref="IDurability"/> for platforms where a folder cannot be fsynced: files are still flushed to disk.
/// </summary>
public sealed class NoDirectorySyncDurability : IDurability
{
    /// <inheritdoc />
    public void SyncFile(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void SyncDirectory(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
    }
}
