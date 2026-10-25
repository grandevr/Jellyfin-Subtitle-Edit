using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// Keeps submissions as one JSON file each (<c>{id}.json</c>) in the plugin's data folder, outside the plugin's own
/// folder, so they survive a plugin update and a rollback. Files are written to a temporary name and renamed, so a
/// crash never leaves half a file. A file that cannot be read is skipped, never deleted.
/// </summary>
public sealed partial class SubmissionStore
{
    private static readonly ConcurrentDictionary<string, Lock> Locks = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubmissionStore"/> class.
    /// </summary>
    /// <param name="folder">The folder that holds the submission files. Created on the first write.</param>
    public SubmissionStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        this.folder = Path.GetFullPath(folder);
        SyncRoot = Locks.GetOrAdd(this.folder, _ => new Lock());
    }

    /// <summary>
    /// Gets the lock that every read-modify-write of this folder holds, shared by all stores of the same folder.
    /// </summary>
    internal Lock SyncRoot { get; }

    /// <summary>
    /// Makes a new submission id.
    /// </summary>
    /// <returns>32 lowercase hex characters.</returns>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Says whether a string is a well-formed submission id (so it can safely be part of a file name).
    /// </summary>
    /// <param name="id">The candidate.</param>
    /// <returns>True for 32 lowercase hex characters.</returns>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>
    /// Writes a submission, replacing the stored one with the same id.
    /// </summary>
    /// <param name="submission">The submission.</param>
    /// <exception cref="SubmissionException">The file could not be written.</exception>
    public void Save(Submission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!IsValidId(submission.Id))
        {
            throw new ArgumentException("The submission id must be 32 lowercase hex characters.", nameof(submission));
        }

        string path = PathOf(submission.Id);
        string temp = Path.Combine(folder, "." + submission.Id + ".json.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(submission, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            throw new SubmissionException(
                SubmissionError.StorageFailed, "Could not save the submission in '" + folder + "': " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temp);
            throw new SubmissionException(
                SubmissionError.StorageFailed,
                "The folder '" + folder + "' is not writable by the Jellyfin process (running as '" + Environment.UserName + "').",
                ex);
        }
    }

    /// <summary>
    /// Reads one submission.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <returns>The submission, or null when the id is malformed, there is no such file, or it cannot be read.</returns>
    public Submission? Find(string id)
    {
        return IsValidId(id) ? Read(PathOf(id)) : null;
    }

    /// <summary>
    /// Reads every readable submission, newest first.
    /// </summary>
    /// <returns>The submissions.</returns>
    public IReadOnlyList<Submission> All()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var all = new List<Submission>();
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            Submission? submission = Read(path);
            if (submission is not null)
            {
                all.Add(submission);
            }
        }

        return all.OrderByDescending(s => s.CreatedUtc).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Deletes the oldest decided and withdrawn submissions beyond the newest <paramref name="keep"/>. Pending ones
    /// are never deleted.
    /// </summary>
    /// <param name="keep">How many decided submissions to keep.</param>
    /// <returns>How many were deleted.</returns>
    public int PruneDecided(int keep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        List<Submission> old = All()
            .Where(s => s.State != SubmissionState.Pending)
            .OrderByDescending(s => s.DecidedUtc ?? s.CreatedUtc)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (Submission submission in old)
        {
            TryDelete(PathOf(submission.Id));
        }

        return old.Count;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private static Submission? Read(string path)
    {
        try
        {
            Submission? submission = JsonSerializer.Deserialize<Submission>(File.ReadAllBytes(path), JsonOptions);
            return submission is not null && IsValidId(submission.Id)
                && string.Equals(Path.GetFileNameWithoutExtension(path), submission.Id, StringComparison.Ordinal)
                ? submission
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
            // Left behind; it is skipped or overwritten later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private string PathOf(string id) => Path.Combine(folder, id + ".json");
}
