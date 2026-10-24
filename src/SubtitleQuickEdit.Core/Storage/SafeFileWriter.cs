using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SubtitleQuickEdit.Core.Srt;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Replaces a subtitle file safely.
/// <para>
/// The new content goes to a hidden temp file (<c>.name.sqe-id-hash.tmp</c>) in the same folder and is
/// verified. When its owner, group and mode can be made equal to the original's it is renamed over the
/// original. Otherwise it is renamed to <c>.inplace</c> and copied over the original in place; the
/// <c>.inplace</c> file is deleted only after the original's hash is verified, so a crash mid-copy leaves
/// the intended content on disk and the next write resumes it.
/// </para>
/// </summary>
public sealed class SafeFileWriter
{
    private const string TempExtension = ".tmp";
    private const string InPlaceExtension = ".inplace";
    private const string Marker = ".sqe-";
    private const int HashChars = 32;
    private const int NameIdChars = 8;
    private const int MaxNameChars = 100;

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);

    private readonly IFileMetadata metadata;
    private readonly IWriteObserver? observer;
    private readonly int copyChunkBytes;
    private readonly IDurability durability;

    /// <summary>
    /// Initializes a new instance of the <see cref="SafeFileWriter"/> class.
    /// </summary>
    /// <param name="metadata">Ownership access; defaults to the current platform's (<see cref="PlatformServices.Current"/>).</param>
    /// <param name="observer">An optional progress observer.</param>
    /// <param name="copyChunkBytes">The chunk size of the in-place copy.</param>
    /// <param name="durability">How files and folders are fsynced; defaults to the current platform's.</param>
    public SafeFileWriter(
        IFileMetadata? metadata = null,
        IWriteObserver? observer = null,
        int copyChunkBytes = 64 * 1024,
        IDurability? durability = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(copyChunkBytes, 1);
        this.metadata = metadata ?? PlatformServices.Current.Metadata;
        this.observer = observer;
        this.copyChunkBytes = copyChunkBytes;
        this.durability = durability ?? PlatformServices.Current.Durability;
    }

    /// <summary>
    /// Tells whether a file name is one of this writer's temp or in-place files, so other code can ignore it.
    /// </summary>
    /// <param name="fileName">The file name (no folder).</param>
    /// <returns>True for temp and in-place files.</returns>
    public static bool IsWriterFile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.StartsWith('.')
            && fileName.Contains(Marker, StringComparison.Ordinal)
            && (fileName.EndsWith(TempExtension, StringComparison.Ordinal)
                || fileName.EndsWith(InPlaceExtension, StringComparison.Ordinal));
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="newContent"/>, provided the file still hashes to
    /// <paramref name="expectedBaseHash"/>.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <param name="newContent">The complete new content.</param>
    /// <param name="expectedBaseHash">SHA-256 (lower-case hex) of the content the edit was made against.</param>
    /// <returns>How the write went.</returns>
    /// <exception cref="SubtitleWriteException">The write was refused or failed.</exception>
    public WriteOutcome Write(string path, ReadOnlyMemory<byte> newContent, string expectedBaseHash)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedBaseHash);

        string full = Path.GetFullPath(path);
        lock (Gates.GetOrAdd(full, static _ => new object()))
        {
            var notes = new List<string>();
            RecoverLocked(full, notes);

            string currentHash = HashOf(ReadTarget(full));
            if (!string.Equals(currentHash, expectedBaseHash, StringComparison.Ordinal))
            {
                throw Conflict(currentHash);
            }

            byte[] content = newContent.ToArray();
            string newHash = SrtDocument.ComputeHash(content);
            FileOwnership? original = metadata.Get(full);
            string temp = WriterPath(full, newHash, TempExtension);

            WriteTemp(full, temp, content);
            try
            {
                Notify(WriteStage.TempWritten);
                if (!string.Equals(HashOf(File.ReadAllBytes(temp)), newHash, StringComparison.Ordinal))
                {
                    throw new SubtitleWriteException(
                        SubtitleWriteError.VerificationFailed,
                        "The temporary file did not read back correctly. The subtitle file was not changed.");
                }

                Notify(WriteStage.TempVerified);
                bool matched = original is null || metadata.TryMatch(temp, original.Value);

                string recheck = HashOf(ReadTarget(full));
                if (!string.Equals(recheck, expectedBaseHash, StringComparison.Ordinal))
                {
                    throw Conflict(recheck);
                }

                if (!matched)
                {
                    EnsureOriginalWritable(full);
                }

                Notify(WriteStage.BeforeRename);
                if (matched)
                {
                    File.Move(temp, full, overwrite: true);
                    SyncFolder(full, notes);
                }
                else
                {
                    string inPlace = WriterPath(full, newHash, InPlaceExtension);
                    File.Move(temp, inPlace);
                    SyncFolder(full, notes); // the recovery file must be durable before the original is touched
                    Notify(WriteStage.InPlaceStarted);
                    CopyInPlace(full, inPlace, content);
                    FinishInPlace(full, inPlace, newHash, notes);
                    return new WriteOutcome(WriteMethod.InPlaceCopy, newHash, notes);
                }
            }
            catch (SubtitleWriteException)
            {
                DeleteQuietly(temp);
                throw;
            }
            catch (IOException ex)
            {
                DeleteQuietly(temp);
                throw new SubtitleWriteException(SubtitleWriteError.Io, "Could not write the subtitle file: " + ex.Message, innerException: ex);
            }

            string written = HashOf(ReadTarget(full));
            if (!string.Equals(written, newHash, StringComparison.Ordinal))
            {
                throw new SubtitleWriteException(
                    SubtitleWriteError.VerificationFailed,
                    "The subtitle file did not read back correctly after it was replaced.");
            }

            return new WriteOutcome(WriteMethod.AtomicRename, newHash, notes);
        }
    }

    /// <summary>
    /// Resolves leftovers from an interrupted save of <paramref name="path"/>: removes stale temp files and
    /// resumes or cleans up an interrupted in-place copy. <see cref="Write"/> does this itself first.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <returns>What was done, fit for the log. Empty when there was nothing to do.</returns>
    /// <exception cref="SubtitleWriteException">A leftover cannot be resolved automatically.</exception>
    public IReadOnlyList<string> RecoverLeftovers(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = Path.GetFullPath(path);
        lock (Gates.GetOrAdd(full, static _ => new object()))
        {
            var notes = new List<string>();
            RecoverLocked(full, notes);
            return notes;
        }
    }

    private static SubtitleWriteException Conflict(string currentHash) =>
        new(SubtitleWriteError.Conflict, "The subtitle file changed on disk since it was loaded.", currentHash: currentHash);

    private static string HashOf(byte[] content) => SrtDocument.ComputeHash(content);

    private static byte[] ReadTarget(string full)
    {
        try
        {
            return File.ReadAllBytes(full);
        }
        catch (IOException ex)
        {
            throw new SubtitleWriteException(SubtitleWriteError.Io, "Could not read the subtitle file: " + ex.Message, innerException: ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.NotWritable,
                "The subtitle file '" + full + "' cannot be read by the Jellyfin process.",
                innerException: ex);
        }
    }

    private static string NameId(string full)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexStringLower(hash)[..NameIdChars];
    }

    private static string WriterPath(string full, string contentHash, string extension)
    {
        string dir = Path.GetDirectoryName(full)!;
        string name = Path.GetFileName(full);
        if (name.Length > MaxNameChars)
        {
            name = name[..MaxNameChars];
        }

        string file = string.Create(
            CultureInfo.InvariantCulture,
            $".{name}{Marker}{NameId(full)}-{contentHash[..HashChars]}{extension}");
        return Path.Combine(dir, file);
    }

    private static IEnumerable<string> FindLeftovers(string full, string extension)
    {
        string dir = Path.GetDirectoryName(full)!;
        string needle = Marker + NameId(full) + "-";
        foreach (string candidate in Directory.EnumerateFiles(dir, "*" + extension))
        {
            string file = Path.GetFileName(candidate);
            if (file.Contains(needle, StringComparison.Ordinal) && IsWriterFile(file))
            {
                yield return candidate;
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is cleaned up by the next save.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }

    private void WriteTemp(string full, string temp, byte[] content)
    {
        try
        {
            using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(content);
            durability.SyncFile(stream);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.NotWritable,
                "The folder '" + Path.GetDirectoryName(full) + "' is not writable by the Jellyfin process (running as '"
                    + Environment.UserName + "'). Give that user write access to the folder.",
                innerException: ex);
        }
        catch (IOException ex)
        {
            DeleteQuietly(temp);
            throw new SubtitleWriteException(SubtitleWriteError.Io, "Could not write a temporary file next to the subtitle: " + ex.Message, innerException: ex);
        }
    }

    private static void EnsureOriginalWritable(string full)
    {
        try
        {
            using var probe = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.NotWritable,
                "The subtitle file '" + full + "' is not writable by the Jellyfin process (running as '"
                    + Environment.UserName + "') and its owner or group cannot be matched by a new file.",
                innerException: ex);
        }
    }

    private void Notify(WriteStage stage) => observer?.OnStage(stage);

    private void CopyInPlace(string full, string inPlace, byte[] content)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(0);
            int offset = 0;
            while (offset < content.Length)
            {
                int count = Math.Min(copyChunkBytes, content.Length - offset);
                stream.Write(content, offset, count);
                offset += count;
                Notify(WriteStage.InPlaceChunkWritten);
            }

            durability.SyncFile(stream);
        }
        catch (IOException ex)
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.Io,
                "Copying over the subtitle file was interrupted. The complete new content is saved in '" + inPlace
                    + "' and will be restored by the next save.",
                leftoverPath: inPlace,
                innerException: ex);
        }
    }

    private void SyncFolder(string full, List<string> notes)
    {
        string dir = Path.GetDirectoryName(full)!;
        try
        {
            durability.SyncDirectory(dir);
        }
        catch (IOException ex)
        {
            // The data is already in place; failing the save now would not undo it. Say so instead.
            notes.Add("Could not fsync the folder '" + dir + "' (the file itself was written): " + ex.Message);
        }
    }

    private void FinishInPlace(string full, string inPlace, string expectedHash, List<string> notes)
    {
        string now = HashOf(ReadTarget(full));
        if (!string.Equals(now, expectedHash, StringComparison.Ordinal))
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.VerificationFailed,
                "The subtitle file did not read back correctly after the in-place copy. The complete new content is kept in '"
                    + inPlace + "'.",
                leftoverPath: inPlace);
        }

        File.Delete(inPlace);
        SyncFolder(full, notes);
    }

    private void RecoverLocked(string full, List<string> notes)
    {
        string dir = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(dir))
        {
            return;
        }

        // Temp files were never applied: the original is untouched, so they are just stale.
        foreach (string stale in FindLeftovers(full, TempExtension))
        {
            File.Delete(stale);
            notes.Add("Removed stale temporary file '" + Path.GetFileName(stale) + "' left by an interrupted save.");
        }

        var inPlaceFiles = new List<string>(FindLeftovers(full, InPlaceExtension));
        if (inPlaceFiles.Count == 0)
        {
            return;
        }

        if (inPlaceFiles.Count > 1)
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.LeftoverUnrecoverable,
                "More than one in-place recovery file exists next to '" + full + "'; resolve them by hand.",
                leftoverPath: inPlaceFiles[0]);
        }

        string inPlace = inPlaceFiles[0];
        byte[] intended = File.ReadAllBytes(inPlace);
        string intendedHash = HashOf(intended);
        string nameHash = ExtractHash(Path.GetFileName(inPlace));
        if (!intendedHash.StartsWith(nameHash, StringComparison.Ordinal))
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.LeftoverUnrecoverable,
                "The recovery file '" + inPlace + "' is damaged (its content does not match its name); resolve it by hand.",
                leftoverPath: inPlace);
        }

        string targetHash = File.Exists(full) ? HashOf(File.ReadAllBytes(full)) : string.Empty;
        if (!string.Equals(targetHash, intendedHash, StringComparison.Ordinal))
        {
            CopyInPlace(full, inPlace, intended);
            FinishInPlace(full, inPlace, intendedHash, notes);
            notes.Add("Restored '" + Path.GetFileName(full) + "' from an interrupted in-place save.");
        }
        else
        {
            File.Delete(inPlace);
            SyncFolder(full, notes);
            notes.Add("Removed recovery file '" + Path.GetFileName(inPlace) + "'; its save had already completed.");
        }
    }

    private static string ExtractHash(string fileName)
    {
        int dash = fileName.LastIndexOf('-');
        int dot = fileName.LastIndexOf('.');
        return dash >= 0 && dot > dash ? fileName[(dash + 1)..dot] : string.Empty;
    }
}
