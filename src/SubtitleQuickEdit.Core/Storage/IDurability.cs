using System.IO;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Forces written data to stable storage. Abstracted so tests can record when it happens and so platforms
/// that cannot fsync a folder can skip that part.
/// </summary>
public interface IDurability
{
    /// <summary>
    /// Flushes a file's data (and metadata) to disk before the stream is closed.
    /// </summary>
    /// <param name="stream">The open stream.</param>
    void SyncFile(FileStream stream);

    /// <summary>
    /// Flushes a folder's entries to disk, so a rename or delete in it survives a power cut.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <exception cref="IOException">The folder could not be synced.</exception>
    void SyncDirectory(string folder);
}
