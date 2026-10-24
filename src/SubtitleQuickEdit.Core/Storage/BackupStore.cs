using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SubtitleQuickEdit.Core.Srt;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Keeps copies of subtitle files from before they are edited, in a folder outside the media and outside the
/// versioned plugin folder.
/// <para>
/// Files are named <c>{itemId}_{streamIndex}_{pathId}_{utc timestamp}_original.srt</c> for the content from before the
/// very first save of a subtitle, and <c>…_save.srt</c> for the content from before each later save (only in
/// <see cref="BackupMode.OriginalPlusLastN"/>). An original backup is never overwritten or removed.
/// </para>
/// <para>
/// <c>…_restore.srt</c> files are restore points: the content at the start of an editing session, kept whatever the
/// mode (the newest three per subtitle) so that the editor's "Revert to original" can go back to it.
/// </para>
/// </summary>
public sealed partial class BackupStore
{
    private const string OriginalKind = "original";
    private const string SaveKind = "save";
    private const string RestorePointKind = "restore";
    private const int RestorePointsKept = 3;

    private readonly string folder;
    private readonly BackupMode mode;
    private readonly int keepLastSaves;
    private readonly IDurability durability;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupStore"/> class.
    /// </summary>
    /// <param name="folder">The backup folder; created when needed.</param>
    /// <param name="mode">Which backups to keep.</param>
    /// <param name="keepLastSaves">For <see cref="BackupMode.OriginalPlusLastN"/>, how many earlier versions to keep (at least 1).</param>
    /// <param name="durability">How backup files are fsynced; defaults to the current platform's.</param>
    public BackupStore(string folder, BackupMode mode, int keepLastSaves, IDurability? durability = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        this.folder = folder;
        this.mode = mode;
        this.keepLastSaves = Math.Max(1, keepLastSaves);
        this.durability = durability ?? PlatformServices.Current.Durability;
    }

    /// <summary>
    /// Backs up the current content of a subtitle just before it is changed: always the original on the first save,
    /// and (in <see cref="BackupMode.OriginalPlusLastN"/>) the previous version on later saves.
    /// </summary>
    /// <param name="itemId">The library item id (hex, as in Jellyfin's item ids).</param>
    /// <param name="streamIndex">The subtitle stream index.</param>
    /// <param name="subtitlePath">The subtitle file's path; part of the identity, so a different file never inherits another's original.</param>
    /// <param name="currentContent">The exact bytes about to be replaced.</param>
    /// <returns>The backup files created (empty when nothing needed backing up).</returns>
    /// <exception cref="SubtitleWriteException">The backup could not be written. The save must not go ahead.</exception>
    public IReadOnlyList<string> BeforeSave(string itemId, int streamIndex, string subtitlePath, byte[] currentContent)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        ArgumentNullException.ThrowIfNull(currentContent);
        string key = Key(itemId, streamIndex, subtitlePath);
        try
        {
            Directory.CreateDirectory(folder);
            var created = new List<string>();
            if (!Directory.EnumerateFiles(folder, key + "_*_" + OriginalKind + ".srt").Any())
            {
                created.Add(WriteNew(key, OriginalKind, currentContent));
            }
            else if (mode == BackupMode.OriginalPlusLastN)
            {
                created.Add(WriteNew(key, SaveKind, currentContent));
                Prune(key, SaveKind, keepLastSaves);
            }

            return created;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw NotWritable(ex);
        }
        catch (IOException ex)
        {
            throw IoFailure(ex);
        }
    }

    /// <summary>
    /// Keeps a restore point: the content of a subtitle at the start of an editing session, whatever the backup mode.
    /// Only the newest three restore points of a subtitle are kept.
    /// </summary>
    /// <param name="itemId">The library item id (hex, as in Jellyfin's item ids).</param>
    /// <param name="streamIndex">The subtitle stream index.</param>
    /// <param name="subtitlePath">The subtitle file's path.</param>
    /// <param name="content">The exact bytes to keep.</param>
    /// <returns>The restore point file.</returns>
    /// <exception cref="SubtitleWriteException">The restore point could not be written. The save must not go ahead.</exception>
    public string KeepRestorePoint(string itemId, int streamIndex, string subtitlePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        ArgumentNullException.ThrowIfNull(content);
        string key = Key(itemId, streamIndex, subtitlePath);
        try
        {
            Directory.CreateDirectory(folder);
            string path = WriteNew(key, RestorePointKind, content);
            Prune(key, RestorePointKind, RestorePointsKept);
            return path;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw NotWritable(ex);
        }
        catch (IOException ex)
        {
            throw IoFailure(ex);
        }
    }

    /// <summary>
    /// Finds a kept version of a subtitle (an original, an earlier save or a restore point) by the SHA-256 of its
    /// content, newest first.
    /// </summary>
    /// <param name="itemId">The library item id (hex, as in Jellyfin's item ids).</param>
    /// <param name="streamIndex">The subtitle stream index.</param>
    /// <param name="subtitlePath">The subtitle file's path.</param>
    /// <param name="hash">SHA-256 (hex) of the wanted content.</param>
    /// <returns>The content, or null when no kept version has that hash.</returns>
    public byte[]? FindByHash(string itemId, int streamIndex, string subtitlePath, string hash)
    {
        ArgumentNullException.ThrowIfNull(subtitlePath);
        ArgumentNullException.ThrowIfNull(hash);
        string key = Key(itemId, streamIndex, subtitlePath);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        string wanted = hash.Trim().ToLowerInvariant();
        IEnumerable<string> files = Directory.EnumerateFiles(folder, key + "_*.srt")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal);
        foreach (string file in files)
        {
            byte[] content = File.ReadAllBytes(file);
            if (string.Equals(SrtDocument.ComputeHash(content), wanted, StringComparison.Ordinal))
            {
                return content;
            }
        }

        return null;
    }

    [GeneratedRegex("^[0-9a-fA-F-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();

    private static string Key(string itemId, int streamIndex, string subtitlePath)
    {
        if (!SafeId().IsMatch(itemId))
        {
            throw new ArgumentException("The item id must be hexadecimal.", nameof(itemId));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{itemId.ToLowerInvariant()}_{streamIndex}_{PathId(subtitlePath)}");
    }

    private static string PathId(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Convert.ToHexStringLower(hash)[..8];
    }

    private string WriteNew(string key, string kind, byte[] content)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        for (int attempt = 0; ; attempt++)
        {
            string suffix = attempt == 0 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, key + "_" + stamp + suffix + "_" + kind + ".srt");
            try
            {
                // CreateNew: an existing backup, original or not, is never overwritten.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(content);
                durability.SyncFile(stream);
                return path;
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
                // Same millisecond: try the next name.
            }
        }
    }

    private void Prune(string key, string kind, int keep)
    {
        List<string> kept = Directory.EnumerateFiles(folder, key + "_*_" + kind + ".srt")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        foreach (string old in kept.Skip(keep))
        {
            File.Delete(old);
        }
    }

    private SubtitleWriteException NotWritable(UnauthorizedAccessException ex) => new(
        SubtitleWriteError.NotWritable,
        "The backup folder '" + folder + "' is not writable by the Jellyfin process (running as '" + Environment.UserName
            + "'). Nothing was saved.",
        innerException: ex);

    private SubtitleWriteException IoFailure(IOException ex) => new(
        SubtitleWriteError.Io,
        "Could not write a backup in '" + folder + "': " + ex.Message + " Nothing was saved.",
        innerException: ex);
}
