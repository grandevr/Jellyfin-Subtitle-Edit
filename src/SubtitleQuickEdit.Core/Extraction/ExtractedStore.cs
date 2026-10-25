using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SubtitleQuickEdit.Core.Submissions;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// Keeps the list of extracted files as one JSON file each (<c>{id}.json</c>) in the plugin's data folder, written to
/// a temporary name and renamed. A file that cannot be read is skipped, never deleted.
/// </summary>
public sealed class ExtractedStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractedStore"/> class.
    /// </summary>
    /// <param name="folder">The folder that holds the records. Created on the first write.</param>
    public ExtractedStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        this.folder = System.IO.Path.GetFullPath(folder);
    }

    /// <summary>
    /// Writes a record, replacing the stored one with the same id.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <exception cref="IOException">The record could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public void Save(ExtractedFile record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!SubmissionStore.IsValidId(record.Id))
        {
            throw new ArgumentException("The record id must be 32 lowercase hex characters.", nameof(record));
        }

        string temp = System.IO.Path.Combine(folder, "." + record.Id + ".json.tmp");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
            File.Move(temp, PathOf(record.Id), overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Reads one record.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <returns>The record, or null when the id is malformed, there is no such record, or it cannot be read.</returns>
    public ExtractedFile? Find(string id) => SubmissionStore.IsValidId(id) ? Read(PathOf(id)) : null;

    /// <summary>
    /// Reads every readable record, newest first.
    /// </summary>
    /// <returns>The records.</returns>
    public IReadOnlyList<ExtractedFile> All()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*.json")
            .Select(Read)
            .OfType<ExtractedFile>()
            .OrderByDescending(r => r.CreatedUtc)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Finds the records of a subtitle file.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <returns>Its records, newest first.</returns>
    public IReadOnlyList<ExtractedFile> ForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = System.IO.Path.GetFullPath(path);
        return All().Where(r => string.Equals(r.Path, full, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Deletes a record.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <returns>True when a record was deleted.</returns>
    /// <exception cref="IOException">The record exists but could not be deleted.</exception>
    public bool Remove(string id)
    {
        if (!SubmissionStore.IsValidId(id))
        {
            return false;
        }

        string path = PathOf(id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static ExtractedFile? Read(string path)
    {
        try
        {
            ExtractedFile? record = JsonSerializer.Deserialize<ExtractedFile>(File.ReadAllBytes(path), JsonOptions);
            return record is not null && SubmissionStore.IsValidId(record.Id)
                && string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), record.Id, StringComparison.Ordinal)
                ? record
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is overwritten by the next save of the same id.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private string PathOf(string id) => System.IO.Path.Combine(folder, id + ".json");
}
