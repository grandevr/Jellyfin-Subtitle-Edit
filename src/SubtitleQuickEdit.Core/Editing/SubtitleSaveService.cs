using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// Saves edits to a subtitle file, applying the spec's save rules in order: the file must be an editable .srt; its
/// hash must still be the client's base hash; every edit must be valid and match the file's current text; the
/// original is backed up; the new content is written with <see cref="SafeFileWriter"/>. Jellyfin reads external
/// .srt files on every request, so nothing needs refreshing afterwards.
/// </summary>
public sealed class SubtitleSaveService
{
    private readonly SafeFileWriter writer;
    private readonly BackupStore backups;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSaveService"/> class.
    /// </summary>
    /// <param name="writer">The file writer.</param>
    /// <param name="backups">The backup store.</param>
    public SubtitleSaveService(SafeFileWriter writer, BackupStore backups)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(backups);
        this.writer = writer;
        this.backups = backups;
    }

    /// <summary>
    /// Saves the edits.
    /// </summary>
    /// <param name="request">What to save.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="SubtitleNotEditableException">The file cannot be edited.</exception>
    /// <exception cref="SubtitleEditException">An edit is invalid or conflicts with the file's text. Nothing was changed.</exception>
    /// <exception cref="SubtitleWriteException">The file changed since it was loaded, or writing failed.</exception>
    public SaveResult Save(SaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Edits.Count == 0)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "There are no edits to save.");
        }

        TrackInspection inspection = SubtitleTrackInspector.Inspect(request.Path);
        if (inspection.Document is null)
        {
            throw new SubtitleNotEditableException(inspection.Reason ?? "The subtitle cannot be edited.");
        }

        ISubtitleDocument doc = inspection.Document;
        string baseHash = request.BaseHash.Trim().ToLowerInvariant();
        if (!string.Equals(doc.Hash, baseHash, StringComparison.Ordinal))
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.Conflict,
                "The subtitle file changed on disk since it was loaded.",
                currentHash: doc.Hash);
        }

        SubtitleApplyResult applied = doc.Apply(request.Edits, request.RuntimeMs);
        byte[] current = doc.Content.ToArray();
        backups.BeforeSave(request.ItemId, request.StreamIndex, request.Path, current);
        if (request.KeepRestorePoint)
        {
            backups.KeepRestorePoint(request.ItemId, request.StreamIndex, request.Path, current);
        }

        WriteOutcome outcome = writer.Write(request.Path, applied.Content, baseHash);

        List<int> saved = applied.EditedCuePositions.Concat(applied.AddedCuePositions.Values).Distinct().OrderBy(p => p).ToList();
        return new SaveResult(applied.Hash, saved, applied.AddedCuePositions, outcome.Method, outcome.Notes);
    }

    /// <summary>
    /// Puts a subtitle back to a version kept in the backups (its original, an earlier save or a restore point), found
    /// by the hash of its content. The current content is backed up first, like before any save, and the kept version
    /// is written back byte for byte.
    /// </summary>
    /// <param name="request">What to restore.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="RestorePointNotFoundException">No kept version has that content. Nothing was changed.</exception>
    /// <exception cref="SubtitleWriteException">The file changed since it was loaded, or reading or writing failed.</exception>
    public RestoreResult Restore(RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string baseHash = request.BaseHash.Trim().ToLowerInvariant();
        string targetHash = request.TargetHash.Trim().ToLowerInvariant();
        byte[] current;
        try
        {
            current = File.ReadAllBytes(request.Path);
        }
        catch (IOException ex)
        {
            throw ReadFailure("the subtitle file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw ReadFailure("the subtitle file", ex);
        }

        string currentHash = SubtitleHash.Compute(current);
        if (!string.Equals(currentHash, baseHash, StringComparison.Ordinal))
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.Conflict,
                "The subtitle file changed on disk since it was loaded.",
                currentHash: currentHash);
        }

        if (string.Equals(currentHash, targetHash, StringComparison.Ordinal))
        {
            return new RestoreResult(currentHash, false, null, Array.Empty<string>());
        }

        byte[] content;
        try
        {
            content = backups.FindByHash(request.ItemId, request.StreamIndex, request.Path, targetHash)
                ?? throw new RestorePointNotFoundException(
                    "That earlier version of the subtitle is not in the backups any more, so it cannot be restored.");
        }
        catch (IOException ex)
        {
            throw ReadFailure("the backups", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw ReadFailure("the backups", ex);
        }

        backups.BeforeSave(request.ItemId, request.StreamIndex, request.Path, current);
        WriteOutcome outcome = writer.Write(request.Path, content, baseHash);
        return new RestoreResult(targetHash, true, outcome.Method, outcome.Notes);
    }

    private static SubtitleWriteException ReadFailure(string what, Exception ex) =>
        new(SubtitleWriteError.Io, "Could not read " + what + ": " + ex.Message, innerException: ex);
}
