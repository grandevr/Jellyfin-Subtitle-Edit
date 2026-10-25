using System;
using System.Collections.Generic;
using System.IO;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Submissions;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// Writes an embedded subtitle track, as Jellyfin extracted it, to a new file next to its video so that it can be
/// edited, and keeps a record of it. The video itself is never changed.
/// <para>
/// A file of the same name is never overwritten unless an admin asks for it with the hash of the content they saw;
/// the replaced content goes to the backups first.
/// </para>
/// </summary>
public sealed class SubtitleExtractor
{
    private const UnixFileMode NoExecute = ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
        | UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit);

    private readonly SafeFileWriter writer;
    private readonly BackupStore backups;
    private readonly ExtractedStore store;
    private readonly IFileMetadata metadata;
    private readonly Func<DateTimeOffset> utcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleExtractor"/> class.
    /// </summary>
    /// <param name="writer">The file writer.</param>
    /// <param name="backups">Where a replaced or deleted file is kept.</param>
    /// <param name="store">The records of extracted files.</param>
    /// <param name="metadata">Ownership access, to give the new file the video's owner and group; defaults to the current platform's.</param>
    /// <param name="utcNow">The clock; defaults to the system clock.</param>
    public SubtitleExtractor(
        SafeFileWriter writer,
        BackupStore backups,
        ExtractedStore store,
        IFileMetadata? metadata = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(store);
        this.writer = writer;
        this.backups = backups;
        this.store = store;
        this.metadata = metadata ?? PlatformServices.Current.Metadata;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Works out where a track would be written and checks its content, without writing anything.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ExtractionException">The track cannot be extracted.</exception>
    public ExtractPlan Plan(ExtractRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string extension = ExtractedNaming.Extension(request.ExtractedPath)
            ?? throw new ExtractionException(ExtractionError.Unsupported, "Only SubRip, WebVTT and ASS/SSA tracks can be made editable.");
        SubtitleFormat format = SubtitleFormats.FromPath(extension)!.Value;

        if (request.Content.Length > SubtitleLimits.MaxFileBytes)
        {
            throw new ExtractionException(ExtractionError.TooLarge, "The subtitle track is too large to edit.");
        }

        TrackInspection inspection = SubtitleTrackInspector.InspectContent(format, request.Content.ToArray());
        if (!inspection.Editable)
        {
            throw new ExtractionException(ExtractionError.NotParsable, "The subtitle track cannot be edited: " + inspection.Reason);
        }

        string target;
        try
        {
            target = ExtractedNaming.TargetPath(request.VideoPath, request.Language, request.Disposition, extension);
        }
        catch (ArgumentException ex)
        {
            throw new ExtractionException(ExtractionError.Unsupported, ex.Message, innerException: ex);
        }

        byte[]? existing = ReadExisting(target);
        return new ExtractPlan(
            target,
            format,
            request.Content,
            existing is null ? (ReadOnlyMemory<byte>?)null : existing,
            existing is null ? null : SubtitleHash.Compute(existing),
            FirstOrNull(ReadRecords(target)));
    }

    /// <summary>
    /// Writes the track next to its video and records it.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ExtractionException">The extraction was refused. Nothing was written.</exception>
    /// <exception cref="SubtitleWriteException">The file could not be written.</exception>
    public ExtractResult Extract(ExtractRequest request)
    {
        ExtractPlan plan = Plan(request);
        var notes = new List<string>();
        string? backup = null;
        WriteOutcome outcome;

        if (plan.ExistingContent is null)
        {
            try
            {
                outcome = writer.Create(plan.TargetPath, request.Content, OwnershipFor(request.VideoPath));
            }
            catch (SubtitleWriteException ex) when (ex.Error == SubtitleWriteError.AlreadyExists)
            {
                throw Exists(plan.TargetPath, ex.CurrentHash);
            }
        }
        else
        {
            if (!request.Replace)
            {
                throw Exists(plan.TargetPath, plan.ExistingHash);
            }

            if (!request.IsAdmin)
            {
                throw new ExtractionException(
                    ExtractionError.ReplaceNotAllowed, "Only an administrator can replace an existing subtitle file.", plan.ExistingHash);
            }

            if (!string.Equals(request.ExpectedExistingHash, plan.ExistingHash, StringComparison.Ordinal))
            {
                throw Changed(plan.ExistingHash);
            }

            backup = backups.KeepReplaced(request.ItemId, request.ExistingStreamIndex, plan.TargetPath, plan.ExistingContent.Value.ToArray());
            try
            {
                outcome = writer.Write(plan.TargetPath, request.Content, plan.ExistingHash!);
            }
            catch (SubtitleWriteException ex) when (ex.Error == SubtitleWriteError.Conflict)
            {
                throw Changed(ex.CurrentHash);
            }
        }

        notes.AddRange(outcome.Notes);
        var record = new ExtractedFile
        {
            Id = SubmissionStore.NewId(),
            ItemId = request.ItemId,
            SourceStreamIndex = request.SourceStreamIndex,
            Path = plan.TargetPath,
            Hash = outcome.NewHash,
            Replaced = plan.ExistingContent is not null,
            UserId = request.UserId,
            UserName = request.UserName,
            CreatedUtc = utcNow(),
        };

        try
        {
            foreach (ExtractedFile old in ReadRecords(plan.TargetPath))
            {
                store.Remove(old.Id);
            }

            store.Save(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The subtitle file is written and the library will show it; only the list of extracted files misses it.
            notes.Add("The file was written, but it could not be added to the list of extracted files: " + ex.Message);
        }

        return new ExtractResult(record, backup, notes);
    }

    /// <summary>
    /// Deletes an extracted file (its last content goes to the backups first) and its record.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <param name="streamIndex">The file's stream index in the library, or -1. Names its backup.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ExtractionException">There is no such record, or the list could not be changed.</exception>
    /// <exception cref="SubtitleWriteException">The backup could not be written; the file was not deleted.</exception>
    public DeleteExtractedResult Delete(string id, int streamIndex)
    {
        ExtractedFile record = store.Find(id) ?? throw new ExtractionException(ExtractionError.NotFound, "There is no such extracted file.");
        string? backup = null;
        byte[]? content = ReadExisting(record.Path);
        try
        {
            if (content is not null)
            {
                backup = backups.KeepDeleted(record.ItemId, streamIndex, record.Path, content);
                File.Delete(record.Path);
            }

            store.Remove(record.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExtractionException(ExtractionError.StorageFailed, "Could not delete '" + record.Path + "': " + ex.Message, innerException: ex);
        }

        return new DeleteExtractedResult(record, backup);
    }

    private static ExtractionException Exists(string target, string? hash) => new(
        ExtractionError.FileExists,
        "A subtitle file named '" + Path.GetFileName(target) + "' already exists next to the video.",
        hash);

    private static ExtractionException Changed(string? hash) => new(
        ExtractionError.FileChanged, "The existing subtitle file changed since it was shown. Look at it again.", hash);

    private static ExtractedFile? FirstOrNull(IReadOnlyList<ExtractedFile> records) => records.Count > 0 ? records[0] : null;

    private static byte[]? ReadExisting(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExtractionException(
                ExtractionError.StorageFailed, "The existing file '" + path + "' cannot be read: " + ex.Message, innerException: ex);
        }
    }

    private IReadOnlyList<ExtractedFile> ReadRecords(string target)
    {
        try
        {
            return store.ForPath(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExtractionException(ExtractionError.StorageFailed, "Could not read the list of extracted files: " + ex.Message, innerException: ex);
        }
    }

    // The video's owner and group, and its mode without execute or special bits, so the new file is as visible to
    // other tools (Bazarr, a file share) as the video is.
    private FileOwnership? OwnershipFor(string videoPath)
    {
        FileOwnership? video = metadata.Get(videoPath);
        return video is null ? null : video.Value with { Mode = video.Value.Mode & NoExecute };
    }
}
