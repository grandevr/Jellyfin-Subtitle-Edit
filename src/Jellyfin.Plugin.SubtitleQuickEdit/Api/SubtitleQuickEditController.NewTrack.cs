using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;
using Jellyfin.Plugin.SubtitleQuickEdit.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SubtitleQuickEdit.Core.Extraction;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api;

/// <summary>
/// The new-track routes: a subtitle track written from scratch in the editor becomes a new .srt file next to the video.
/// The file is made only when there are lines for it, never empty. It is listed with the editable copies, so admins
/// can see and delete it.
/// </summary>
public partial class SubtitleQuickEditController
{
    /// <summary>
    /// Shows the name a new subtitle track would get, and whether a file of that name is already there. Admins, and
    /// users the settings allow to edit.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="language">The language for the file name (<c>en</c>, <c>eng</c>, ...), or empty for none.</param>
    /// <response code="200">The preview.</response>
    /// <response code="400">The video is not a local file, or its name is too long.</response>
    /// <response code="403">The caller may not save edits.</response>
    /// <response code="404">No such item.</response>
    /// <returns>The file name and what is there now.</returns>
    [HttpGet("Items/{itemId}/Subtitles/New")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<NewTrackPreviewResponse> GetNewTrackPreview([FromRoute] Guid itemId, [FromQuery] string? language)
    {
        (BaseItem? item, _, ObjectResult? denied) = Authorize(itemId, Need.Edit);
        if (item is null)
        {
            return denied!;
        }

        (string? target, ObjectResult? problem) = NewTrackPath(item, language);
        if (target is null)
        {
            return problem!;
        }

        bool exists = System.IO.File.Exists(target);
        return new NewTrackPreviewResponse
        {
            FileName = Path.GetFileName(target),
            Language = ExtractedNaming.LanguageCode(language),
            Exists = exists,
            ExistingStreamIndex = exists ? embedded.FindExternal(item, target) : null,
        };
    }

    /// <summary>
    /// Writes a new .srt file next to the video with the given lines and has Jellyfin pick it up. A file of the same
    /// name is never overwritten. Admins, and users the settings allow to edit.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancels reading the request and the refresh.</param>
    /// <response code="200">Written. streamIndex is the new track, or null while the library refresh is still running.</response>
    /// <response code="400">The request or a line is invalid, or the video is not a local file.</response>
    /// <response code="403">The caller may not save edits.</response>
    /// <response code="404">No such item.</response>
    /// <response code="409">A file of that name exists (FileExists).</response>
    /// <response code="413">The request is larger than 1 MB.</response>
    /// <response code="500">The file could not be written (the body says why).</response>
    /// <returns>The new file.</returns>
    [HttpPost("Items/{itemId}/Subtitles/New")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<NewTrackResponse>> PostNewTrack([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        (BaseItem? item, EditorRights rights, ObjectResult? denied) = Authorize(itemId, Need.Edit);
        if (item is null)
        {
            return denied!;
        }

        if (Request.ContentLength > MaxRequestBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "RequestTooLarge", "The request is larger than 1 MB.");
        }

        (NewTrackBody? body, ObjectResult? unreadable) = await ReadRequestAsync<NewTrackBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return unreadable!;
        }

        List<SubtitleEdit>? edits = MapEdits(body.Edits, out ObjectResult? invalid);
        if (edits is null)
        {
            return invalid!;
        }

        if (edits.Any(e => e is not SubtitleAddCue))
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidEdit", "A new subtitle track can only hold added lines.");
        }

        (string? target, ObjectResult? problem) = NewTrackPath(item, body.Language);
        if (target is null)
        {
            return problem!;
        }

        SubtitleApplyResult composed;
        try
        {
            composed = SrtDocument.Compose(edits.Cast<SubtitleAddCue>().ToList(), RuntimeMs(item));
        }
        catch (SubtitleEditException ex)
        {
            return EditError(ex);
        }

        var request = new ExtractRequest
        {
            ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            SourceStreamIndex = ExtractedFile.NewTrack,
            VideoPath = EmbeddedTrackService.LocalVideoPath(item)!,
            ExtractedPath = target,
            Content = composed.Content,
            Language = body.Language,
            UserId = rights.UserId,
            UserName = rights.UserName,
            IsAdmin = rights.IsAdmin,
        };

        ExtractResult result;
        try
        {
            result = Extractor().Extract(request);
        }
        catch (ExtractionException ex)
        {
            return ExtractionProblem(ex, item.Id, ExtractedFile.NewTrack);
        }
        catch (SubtitleWriteException ex)
        {
            return WriteError(ex, item.Id, ExtractedFile.NewTrack);
        }

        logger.LogInformation(
            "{User} started a new subtitle track for item {ItemId} with {Count} line(s): {File}.",
            rights.UserName,
            item.Id,
            edits.Count,
            result.Record.Path);
        foreach (string note in result.Notes)
        {
            logger.LogWarning("Subtitle Editor: {Note}", note);
        }

        if (!rights.IsAdmin)
        {
            await AnnounceMadeFileAsync(rights, item, Path.GetFileName(result.Record.Path), "started a new subtitle track").ConfigureAwait(false);
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
            logger.LogWarning(ex, "Subtitle Editor could not refresh item {ItemId} after starting a new subtitle track.", item.Id);
            pending = true;
        }

        return new NewTrackResponse
        {
            FileName = Path.GetFileName(result.Record.Path),
            Hash = result.Record.Hash,
            AddedCues = composed.AddedCuePositions,
            StreamIndex = newIndex,
            RefreshPending = pending,
        };
    }

    /// <summary>
    /// The path a new track of the item gets: <c>{video name}[.{language}].srt</c> next to the video.
    /// </summary>
    private (string? Path, ObjectResult? Problem) NewTrackPath(BaseItem item, string? language)
    {
        string? videoPath = EmbeddedTrackService.LocalVideoPath(item);
        if (videoPath is null)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "NotLocal", "The video is not a local file, so no subtitle file can be put next to it."));
        }

        try
        {
            return (ExtractedNaming.TargetPath(videoPath, language, TrackDisposition.None, ".srt"), null);
        }
        catch (ArgumentException ex)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "NotLocal", ex.Message));
        }
    }
}
