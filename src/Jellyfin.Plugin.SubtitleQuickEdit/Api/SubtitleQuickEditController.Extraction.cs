using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;
using Jellyfin.Plugin.SubtitleQuickEdit.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Extraction;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api;

/// <summary>
/// The extraction routes: an embedded text track is written, as Jellyfin extracts it, to a new file next to the video,
/// which can then be edited like any external subtitle. The video is never changed.
/// </summary>
public partial class SubtitleQuickEditController
{
    /// <summary>
    /// Shows where an embedded track would be extracted to and what is there now. For an admin, when a file of that
    /// name exists, both sets of lines are included for a side-by-side comparison. Everyone who may use the editor.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="streamIndex">The embedded subtitle stream index.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <response code="200">The preview.</response>
    /// <response code="400">The stream is not an embedded text track of a local video.</response>
    /// <response code="403">The caller may not use the editor.</response>
    /// <response code="404">No such item or stream.</response>
    /// <response code="413">The track is too large to edit.</response>
    /// <response code="422">The track cannot be parsed.</response>
    /// <returns>The target file name, what exists there, and for admins both sets of lines.</returns>
    [HttpGet("Items/{itemId}/Subtitles/{streamIndex}/Extract")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ExtractPreviewResponse>> GetExtractPreview(
        [FromRoute] Guid itemId,
        [FromRoute] int streamIndex,
        CancellationToken cancellationToken)
    {
        (BaseItem? item, EditorRights rights, ObjectResult? denied) = Authorize(itemId, Need.Read);
        if (item is null)
        {
            return denied!;
        }

        (ExtractRequest? request, ObjectResult? problem) = await PrepareExtractAsync(item, rights, streamIndex, null, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return problem!;
        }

        ExtractPlan plan;
        try
        {
            plan = Extractor().Plan(request);
        }
        catch (ExtractionException ex)
        {
            return ExtractionProblem(ex, item.Id, streamIndex);
        }

        ISubtitleDocument doc = SubtitleTrackInspector.InspectContent(plan.Format, plan.Content.ToArray()).Document!;
        var response = new ExtractPreviewResponse
        {
            FileName = Path.GetFileName(plan.TargetPath),
            Format = SubtitleFormats.Name(plan.Format),
            CueCount = doc.Cues.Count,
            Exists = plan.ExistingContent is not null,
            ExistingHash = plan.ExistingHash,
            ExistingStreamIndex = plan.ExistingContent is null ? null : embedded.FindExternal(item, plan.TargetPath),
            ExistingIsExtracted = plan.ExistingRecord is not null,
            CanReplace = rights.IsAdmin,
        };

        if (response.Exists && rights.IsAdmin)
        {
            response.Cues = ToCueDtos(doc);
            TrackInspection existing = SubtitleTrackInspector.InspectContent(plan.Format, plan.ExistingContent!.Value.ToArray());
            response.ExistingCues = existing.Document is null ? null : ToCueDtos(existing.Document);
            response.ExistingReason = existing.Reason;
        }

        return response;
    }

    /// <summary>
    /// Extracts an embedded text track to a new file next to the video and has Jellyfin pick it up. A file of the same
    /// name is never overwritten, except by an admin who asks for it with the hash the preview showed; the old file
    /// is kept in the backups. Everyone who may use the editor; a submitter's extraction is written at once and listed
    /// for the admins.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="streamIndex">The embedded subtitle stream index.</param>
    /// <param name="cancellationToken">Cancels the extraction and the refresh.</param>
    /// <response code="200">Written. streamIndex is the new track, or null while the library refresh is still running.</response>
    /// <response code="400">The request is invalid, or the stream is not an embedded text track of a local video.</response>
    /// <response code="403">The caller may not use the editor, or may not replace an existing file.</response>
    /// <response code="404">No such item or stream.</response>
    /// <response code="409">A file of that name exists (FileExists), or it changed since the preview (FileChanged).</response>
    /// <response code="413">The track is too large to edit.</response>
    /// <response code="422">The track cannot be parsed.</response>
    /// <response code="500">The file could not be written (the body says why).</response>
    /// <returns>The new file.</returns>
    [HttpPost("Items/{itemId}/Subtitles/{streamIndex}/Extract")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ExtractResponse>> PostExtract(
        [FromRoute] Guid itemId,
        [FromRoute] int streamIndex,
        CancellationToken cancellationToken)
    {
        (BaseItem? item, EditorRights rights, ObjectResult? denied) = Authorize(itemId, Need.Read);
        if (item is null)
        {
            return denied!;
        }

        if (Request.ContentLength > MaxRequestBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "RequestTooLarge", "The request is larger than 1 MB.");
        }

        (ExtractBody? body, ObjectResult? unreadable) = await ReadRequestAsync<ExtractBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return unreadable!;
        }

        if (body.Replace && !IsHash(body.ExpectedExistingHash))
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", "Replacing needs expectedExistingHash (SHA-256, hex).");
        }

        (ExtractRequest? request, ObjectResult? problem) = await PrepareExtractAsync(item, rights, streamIndex, body, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return problem!;
        }

        SubtitleExtractor extractor = Extractor();
        ExtractResult result;
        try
        {
            ExtractPlan plan = extractor.Plan(request);
            int existingIndex = plan.ExistingContent is null ? -1 : embedded.FindExternal(item, plan.TargetPath) ?? -1;
            result = extractor.Extract(request with { ExistingStreamIndex = existingIndex });
        }
        catch (ExtractionException ex)
        {
            return ExtractionProblem(ex, item.Id, streamIndex);
        }
        catch (SubtitleWriteException ex)
        {
            return WriteError(ex, item.Id, streamIndex);
        }

        logger.LogInformation(
            "{User} extracted embedded subtitle stream {StreamIndex} of item {ItemId} to {File}{Replaced}.",
            rights.UserName,
            streamIndex,
            item.Id,
            result.Record.Path,
            result.Record.Replaced ? " (replacing the file there, kept in the backups)" : string.Empty);
        foreach (string note in result.Notes)
        {
            logger.LogWarning("Subtitle Editor: {Note}", note);
        }

        if (!rights.IsAdmin)
        {
            await AnnounceExtractAsync(rights, item, Path.GetFileName(result.Record.Path)).ConfigureAwait(false);
        }

        int? newIndex = null;
        bool pending = false;
        try
        {
            newIndex = await embedded.RefreshAsync(item, result.Record.Path, cancellationToken).ConfigureAwait(false);
            pending = newIndex is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The file is written; Jellyfin's next library scan picks it up even when this refresh failed.
            logger.LogWarning(ex, "Subtitle Editor could not refresh item {ItemId} after extracting a subtitle.", item.Id);
            pending = true;
        }

        return new ExtractResponse
        {
            FileName = Path.GetFileName(result.Record.Path),
            Hash = result.Record.Hash,
            Replaced = result.Record.Replaced,
            StreamIndex = newIndex,
            RefreshPending = pending,
        };
    }

    /// <summary>
    /// Lists the subtitle files made from embedded tracks, newest first. Admins only.
    /// </summary>
    /// <response code="200">The list, newest first.</response>
    /// <response code="403">Not an administrator.</response>
    /// <returns>Each file with who made it and whether it was edited since.</returns>
    [HttpGet("Extracted")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<ExtractedFileDto>> GetExtracted()
    {
        var items = new Dictionary<string, BaseItem?>(StringComparer.Ordinal);
        var list = new List<ExtractedFileDto>();
        foreach (ExtractedFile record in ExtractedRecords().All())
        {
            if (!items.TryGetValue(record.ItemId, out BaseItem? item))
            {
                item = Guid.TryParse(record.ItemId, out Guid id) ? resolver.FindVideo(id) : null;
                items[record.ItemId] = item;
            }

            string? currentHash = HashOrNull(record.Path);
            list.Add(new ExtractedFileDto
            {
                Id = record.Id,
                ItemId = record.ItemId,
                ItemName = item is null ? null : ItemName(item),
                SourceStreamIndex = record.SourceStreamIndex,
                FileName = Path.GetFileName(record.Path),
                StreamIndex = item is null ? null : embedded.FindExternal(item, record.Path),
                Exists = currentHash is not null,
                Edited = currentHash is not null && !string.Equals(currentHash, record.Hash, StringComparison.Ordinal),
                Replaced = record.Replaced,
                UserId = record.UserId,
                UserName = record.UserName,
                Created = record.CreatedUtc,
            });
        }

        return list;
    }

    /// <summary>
    /// Deletes a subtitle file made from an embedded track. Its last content, edits included, is kept in the backups,
    /// and the embedded track is still there. Admins only.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <param name="cancellationToken">Cancels the library refresh.</param>
    /// <response code="200">Deleted.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such extracted file.</response>
    /// <response code="500">The file could not be backed up or deleted (the body says why).</response>
    /// <returns>What was deleted.</returns>
    [HttpDelete("Extracted/{id}")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteExtractedResponse>> DeleteExtracted([FromRoute] string id, CancellationToken cancellationToken)
    {
        ExtractedFile? record = ExtractedRecords().Find(id);
        if (record is null)
        {
            return Error(StatusCodes.Status404NotFound, "ExtractedNotFound", "There is no such extracted file.");
        }

        BaseItem? item = Guid.TryParse(record.ItemId, out Guid itemId) ? resolver.FindVideo(itemId) : null;
        int streamIndex = item is null ? -1 : embedded.FindExternal(item, record.Path) ?? -1;
        DeleteExtractedResult result;
        try
        {
            result = Extractor().Delete(record.Id, streamIndex);
        }
        catch (ExtractionException ex)
        {
            return ExtractionProblem(ex, itemId, record.SourceStreamIndex);
        }
        catch (SubtitleWriteException ex)
        {
            return WriteError(ex, itemId, streamIndex);
        }

        logger.LogInformation(
            "{User} deleted the extracted subtitle {File} of item {ItemId}; its last content is in {Backup}.",
            User.Identity?.Name,
            record.Path,
            record.ItemId,
            result.BackupPath ?? "(nothing: the file was already gone)");

        if (item is not null)
        {
            try
            {
                await embedded.RefreshAsync(item, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Subtitle Editor could not refresh item {ItemId} after deleting a subtitle.", item.Id);
            }
        }

        return new DeleteExtractedResponse { FileName = Path.GetFileName(record.Path), BackedUp = result.BackupPath is not null };
    }

    private static ExtractedStore ExtractedRecords() =>
        new(Plugin.Instance?.ExtractedFolder ?? Path.Combine(Path.GetTempPath(), "SubtitleQuickEdit", "extracted"));

    private static SubtitleExtractor Extractor() => new(new SafeFileWriter(), NewBackupStore(), ExtractedRecords());

    private static string? HashOrNull(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? SubtitleHash.Compute(System.IO.File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the external subtitle stream that extracting <paramref name="stream"/> would write, when that file exists.
    /// </summary>
    private int? ExistingExtraction(BaseItem item, MediaStream stream, string videoPath)
    {
        try
        {
            string target = ExtractedNaming.TargetPath(videoPath, stream.Language, EmbeddedTrackService.DispositionOf(stream), EmbeddedTrackService.ExtensionFor(stream));
            return embedded.FindExternal(item, target);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks the stream and has Jellyfin extract it, for a preview or an extraction.
    /// </summary>
    private async Task<(ExtractRequest? Request, ObjectResult? Problem)> PrepareExtractAsync(
        BaseItem item,
        EditorRights rights,
        int streamIndex,
        ExtractBody? body,
        CancellationToken cancellationToken)
    {
        MediaStream? stream = resolver.GetSubtitleStreams(item).FirstOrDefault(s => s.Index == streamIndex);
        if (stream is null)
        {
            return (null, Error(StatusCodes.Status404NotFound, "StreamNotFound", "The item has no subtitle stream with that index."));
        }

        if (stream.IsExternal)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "AlreadyExternal", "This subtitle is already a file next to the video; edit it directly."));
        }

        if (!EmbeddedTrackService.IsExtractable(stream))
        {
            return (null, Error(
                StatusCodes.Status400BadRequest,
                "NotExtractable",
                "Only text subtitle tracks can be made editable; this track is a picture format (" + stream.Codec + ")."));
        }

        string? videoPath = EmbeddedTrackService.LocalVideoPath(item);
        if (videoPath is null)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "NotExtractable", "The video is not a local file."));
        }

        string cachePath;
        byte[] content;
        try
        {
            (cachePath, content) = await embedded.ReadAsync(item, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "NotExtractable", ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Jellyfin could not extract subtitle stream {StreamIndex} of item {ItemId}.", streamIndex, item.Id);
            return (null, Error(StatusCodes.Status500InternalServerError, "ExtractFailed", "Jellyfin could not extract the subtitle track: " + ex.Message));
        }

        return (new ExtractRequest
        {
            ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            SourceStreamIndex = streamIndex,
            VideoPath = videoPath,
            ExtractedPath = cachePath,
            Content = content,
            Language = stream.Language,
            Disposition = EmbeddedTrackService.DispositionOf(stream),
            Replace = body?.Replace ?? false,
            ExpectedExistingHash = body?.ExpectedExistingHash?.Trim().ToLowerInvariant(),
            UserId = rights.UserId,
            UserName = rights.UserName,
            IsAdmin = rights.IsAdmin,
        }, null);
    }

    private ObjectResult ExtractionProblem(ExtractionException ex, Guid itemId, int streamIndex)
    {
        if (ex.Error == ExtractionError.StorageFailed)
        {
            logger.LogError(ex, "Subtitle Editor could not extract or delete subtitle stream {StreamIndex} of item {ItemId}.", streamIndex, itemId);
        }

        return ex.Error switch
        {
            ExtractionError.Unsupported => Error(StatusCodes.Status400BadRequest, "NotExtractable", ex.Message),
            ExtractionError.TooLarge => Error(StatusCodes.Status413PayloadTooLarge, "TooLarge", ex.Message),
            ExtractionError.NotParsable => Error(StatusCodes.Status422UnprocessableEntity, "NotEditable", ex.Message),
            ExtractionError.FileExists => Error(StatusCodes.Status409Conflict, "FileExists", ex.Message, currentHash: ex.CurrentHash),
            ExtractionError.ReplaceNotAllowed => Error(StatusCodes.Status403Forbidden, "Forbidden", ex.Message, currentHash: ex.CurrentHash),
            ExtractionError.FileChanged => Error(StatusCodes.Status409Conflict, "FileChanged", ex.Message, currentHash: ex.CurrentHash),
            ExtractionError.NotFound => Error(StatusCodes.Status404NotFound, "ExtractedNotFound", ex.Message),
            _ => Error(StatusCodes.Status500InternalServerError, "StorageFailed", ex.Message),
        };
    }

    /// <summary>
    /// Adds an entry to Jellyfin's activity log when a user who is not an admin made an editable copy, so admins see
    /// it. A failure is logged and otherwise ignored: the file is already written.
    /// </summary>
    private async Task AnnounceExtractAsync(EditorRights rights, BaseItem item, string fileName)
    {
        try
        {
            var entry = new ActivityLog(
                string.Create(CultureInfo.InvariantCulture, $"{rights.UserName} made an editable copy of a subtitle track"),
                "SubtitleQuickEditExtract",
                rights.User!.Id)
            {
                ShortOverview = ItemName(item) + ": " + fileName,
                Overview = "Listed in Dashboard, Plugins, Subtitle Editor, where it can be deleted.",
                ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
                LogSeverity = LogLevel.Information,
            };
            await activityManager.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The activity log is a courtesy; any failure there must not fail the extraction.
            logger.LogWarning(ex, "Subtitle Editor could not add the extraction to the activity log.");
        }
    }
}
