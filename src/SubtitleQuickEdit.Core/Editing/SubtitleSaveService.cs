using System;
using System.Collections.Generic;
using System.Linq;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;

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
    /// <exception cref="SrtEditException">An edit is invalid or conflicts with the file's text. Nothing was changed.</exception>
    /// <exception cref="SubtitleWriteException">The file changed since it was loaded, or writing failed.</exception>
    public SaveResult Save(SaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Edits.Count == 0)
        {
            throw new SrtEditException(SrtEditError.InvalidEdit, "There are no edits to save.");
        }

        TrackInspection inspection = SrtTrackInspector.Inspect(request.Path);
        if (inspection.Document is null)
        {
            throw new SubtitleNotEditableException(inspection.Reason ?? "The subtitle cannot be edited.");
        }

        SrtDocument doc = inspection.Document;
        string baseHash = request.BaseHash.Trim().ToLowerInvariant();
        if (!string.Equals(doc.Hash, baseHash, StringComparison.Ordinal))
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.Conflict,
                "The subtitle file changed on disk since it was loaded.",
                currentHash: doc.Hash);
        }

        SrtApplyResult applied = doc.Apply(request.Edits, request.RuntimeMs);
        backups.BeforeSave(request.ItemId, request.StreamIndex, request.Path, doc.Data.ToArray());
        WriteOutcome outcome = writer.Write(request.Path, applied.Content, baseHash);

        List<int> saved = applied.EditedCuePositions.Concat(applied.AddedCuePositions.Values).Distinct().OrderBy(p => p).ToList();
        return new SaveResult(applied.Hash, saved, applied.AddedCuePositions, outcome.Method, outcome.Notes);
    }
}
