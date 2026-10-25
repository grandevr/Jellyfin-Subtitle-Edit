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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Submissions;
using SubtitleQuickEdit.Core.Subtitles;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api;

/// <summary>
/// The caller's rights and the submission routes: users submit edits, admins approve or reject them.
/// </summary>
public partial class SubtitleQuickEditController
{
    /// <summary>
    /// Says what the caller may do with the editor.
    /// </summary>
    /// <response code="200">The caller's rights.</response>
    /// <returns>The rights, and for admins the number of submissions waiting for review.</returns>
    [HttpGet("Me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MeResponse> GetMe()
    {
        EditorRights rights = Rights();
        bool on = Config().AllowUserSubmissions;
        return new MeResponse
        {
            IsAdmin = rights.IsAdmin,
            CanEdit = rights.CanEdit,
            CanSubmit = rights.CanSubmit,
            SubmissionsOn = on,
            PendingReviews = rights.IsAdmin ? Submissions().All().Count(s => s.State == SubmissionState.Pending) : null,
            MaxNoteLength = SubmissionService.MaxTextLength,
        };
    }

    /// <summary>
    /// Submits edits for an admin to review. The edits are checked exactly as a save would check them, but the file
    /// is not changed. Users without the right to edit, when submissions are on.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="streamIndex">The subtitle stream index.</param>
    /// <param name="cancellationToken">Cancels reading the request body.</param>
    /// <response code="200">Submitted.</response>
    /// <response code="400">The request, an edit or the note is invalid.</response>
    /// <response code="403">The caller may not submit (submissions are off, or the caller can save directly).</response>
    /// <response code="404">No such item or stream.</response>
    /// <response code="409">The file changed since it was loaded, or a cue's text no longer matches.</response>
    /// <response code="413">The request is larger than 1 MB.</response>
    /// <response code="422">A character cannot be stored in the file's encoding, or the file is not editable.</response>
    /// <response code="429">The caller already has the most pending submissions allowed.</response>
    /// <returns>The stored submission.</returns>
    [HttpPost("Items/{itemId}/Subtitles/{streamIndex}/Submissions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SubmissionDto>> PostSubmission(
        [FromRoute] Guid itemId,
        [FromRoute] int streamIndex,
        CancellationToken cancellationToken)
    {
        (BaseItem? item, EditorRights rights, ObjectResult? denied) = Authorize(itemId, Need.Submit);
        if (item is null)
        {
            return denied!;
        }

        if (Request.ContentLength > MaxRequestBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "RequestTooLarge", "The request is larger than 1 MB.");
        }

        TrackResolution resolution = resolver.Resolve(item, streamIndex);
        if (resolution.Path is null)
        {
            return Error(resolution.Status, resolution.Code!, resolution.Message!);
        }

        (SubmitBody? body, ObjectResult? unreadable) = await ReadRequestAsync<SubmitBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return unreadable!;
        }

        if (string.IsNullOrWhiteSpace(body.BaseHash))
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", "The request needs a baseHash.");
        }

        List<SubtitleEdit>? edits = MapEdits(body.Edits, out ObjectResult? invalid);
        if (edits is null)
        {
            return invalid!;
        }

        Submission submission;
        try
        {
            submission = Submissions().Submit(new SubmitRequest(
                rights.UserId,
                rights.UserName,
                item.Id.ToString("N", CultureInfo.InvariantCulture),
                streamIndex,
                resolution.Path,
                body.BaseHash,
                edits,
                body.Note,
                RuntimeMs(item),
                Math.Max(1, Config().MaxPendingSubmissionsPerUser)));
        }
        catch (SubtitleEditException ex)
        {
            return EditError(ex);
        }
        catch (SubtitleNotEditableException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "NotEditable", ex.Reason);
        }
        catch (SubtitleWriteException ex)
        {
            return WriteError(ex, item.Id, streamIndex);
        }
        catch (SubmissionException ex)
        {
            return SubmissionProblem(ex);
        }

        string itemName = ItemName(item);
        logger.LogInformation(
            "{User} submitted {Count} change(s) to subtitle stream {StreamIndex} of item {ItemId} for review ({SubmissionId}).",
            rights.UserName,
            edits.Count,
            streamIndex,
            item.Id,
            submission.Id);
        await AnnounceAsync(rights, item, itemName, edits.Count).ConfigureAwait(false);
        return ToDto(submission, item, itemName);
    }

    /// <summary>
    /// Lists the caller's own submissions, newest first.
    /// </summary>
    /// <response code="200">The submissions.</response>
    /// <returns>The caller's submissions, in every state.</returns>
    [HttpGet("Submissions/Mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SubmissionDto>> GetMySubmissions()
    {
        EditorRights rights = Rights();
        if (rights.User is null)
        {
            return Array.Empty<SubmissionDto>();
        }

        return Submissions().All()
            .Where(s => string.Equals(s.UserId, rights.UserId, StringComparison.OrdinalIgnoreCase))
            .Select(s => ToDto(s))
            .ToList();
    }

    /// <summary>
    /// Takes back one of the caller's own pending submissions.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <response code="200">Withdrawn.</response>
    /// <response code="404">The caller has no such submission.</response>
    /// <response code="409">It is no longer pending.</response>
    /// <returns>The withdrawn submission.</returns>
    [HttpPost("Submissions/{id}/Withdraw")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SubmissionDto> PostWithdraw([FromRoute] string id)
    {
        EditorRights rights = Rights();
        if (rights.User is null)
        {
            return Error(StatusCodes.Status404NotFound, "SubmissionNotFound", "No such submission.");
        }

        try
        {
            Submission withdrawn = Submissions().Withdraw(id, rights.UserId, rights.UserName);
            logger.LogInformation("{User} withdrew submission {SubmissionId}.", rights.UserName, withdrawn.Id);
            return ToDto(withdrawn);
        }
        catch (SubmissionException ex)
        {
            return SubmissionProblem(ex);
        }
    }

    /// <summary>
    /// Lists submissions for review. Admins only.
    /// </summary>
    /// <param name="status"><c>pending</c> (the default), <c>decided</c> (approved, rejected or withdrawn) or <c>all</c>.</param>
    /// <response code="200">The submissions, newest first.</response>
    /// <response code="400">An unknown status.</response>
    /// <response code="403">Not an administrator.</response>
    /// <returns>The submissions.</returns>
    [HttpGet("Submissions")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<SubmissionDto>> GetSubmissions([FromQuery] string? status)
    {
        Func<Submission, bool>? filter = (status ?? "pending").ToUpperInvariant() switch
        {
            "PENDING" => s => s.State == SubmissionState.Pending,
            "DECIDED" => s => s.State != SubmissionState.Pending,
            "ALL" => _ => true,
            _ => null,
        };
        if (filter is null)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", "status must be pending, decided or all.");
        }

        return Submissions().All().Where(filter).Select(s => ToDto(s)).ToList();
    }

    /// <summary>
    /// Approves a pending submission: writes the chosen changes in one save, with the usual backups. When the file
    /// changed since it was submitted, each changed line is looked for again; changes whose line cannot be found are
    /// conflicts and are not written. When none of the chosen changes can be written, nothing is written and the
    /// submission stays pending with its conflicts marked. Admins only, while submissions are on.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <param name="cancellationToken">Cancels reading the request body.</param>
    /// <response code="200">Approved, or (applied false) nothing could be written.</response>
    /// <response code="400">An invalid choice of changes.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such submission, or its item or stream is gone.</response>
    /// <response code="409">It is no longer pending, submissions are off, or a change no longer fits the file.</response>
    /// <response code="422">The file is no longer editable, or a character cannot be stored in its encoding.</response>
    /// <response code="500">The file could not be written (the body says why).</response>
    /// <returns>The outcome.</returns>
    [HttpPost("Submissions/{id}/Approve")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApproveResponse>> PostApprove([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (!Config().AllowUserSubmissions)
        {
            return SubmissionsOff();
        }

        (ApproveBody? body, ObjectResult? unreadable) = await ReadRequestAsync<ApproveBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return unreadable!;
        }

        SubmissionService service = Submissions();
        Submission? submission = service.Find(id);
        if (submission is null)
        {
            return Error(StatusCodes.Status404NotFound, "SubmissionNotFound", "No such submission.");
        }

        BaseItem? item = Guid.TryParse(submission.ItemId, out Guid itemId) ? resolver.FindVideo(itemId) : null;
        if (item is null)
        {
            return Error(StatusCodes.Status404NotFound, "ItemNotFound", "The submission's item is no longer in the library. Reject it instead.");
        }

        TrackResolution resolution = resolver.Resolve(item, submission.StreamIndex);
        if (resolution.Path is null)
        {
            return Error(resolution.Status, resolution.Code!, resolution.Message!);
        }

        string adminName = User.Identity?.Name ?? string.Empty;
        try
        {
            ApproveResult result = service.Approve(new ApproveRequest(submission.Id, resolution.Path, adminName, body.Edits, RuntimeMs(item)));
            if (result.Save is null)
            {
                logger.LogInformation(
                    "{User} tried to approve submission {SubmissionId}, but none of the chosen changes fit the file any more.",
                    adminName,
                    submission.Id);
                return new ApproveResponse { Submission = ToDto(result.Submission, item), Applied = false };
            }

            logger.LogInformation(
                "{User} approved submission {SubmissionId} by {Submitter}: {State}, subtitle stream {StreamIndex} of item {ItemId} ({Method}).",
                adminName,
                submission.Id,
                submission.UserName,
                result.Submission.State,
                submission.StreamIndex,
                item.Id,
                result.Save.Method);
            foreach (string note in result.Save.Notes)
            {
                logger.LogWarning("Subtitle Editor: {Note}", note);
            }

            return new ApproveResponse
            {
                Submission = ToDto(result.Submission, item),
                Applied = true,
                Hash = result.Save.Hash,
                WriteMethod = result.Save.Method.ToString(),
            };
        }
        catch (SubmissionException ex)
        {
            return SubmissionProblem(ex);
        }
        catch (SubtitleEditException ex)
        {
            return EditError(ex);
        }
        catch (SubtitleNotEditableException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "NotEditable", ex.Reason);
        }
        catch (SubtitleWriteException ex)
        {
            return WriteError(ex, item.Id, submission.StreamIndex);
        }
    }

    /// <summary>
    /// Rejects a pending submission. Nothing is written to the subtitle. Admins only, while submissions are on.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <param name="cancellationToken">Cancels reading the request body.</param>
    /// <response code="200">Rejected.</response>
    /// <response code="400">The reason is too long.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such submission.</response>
    /// <response code="409">It is no longer pending, or submissions are off.</response>
    /// <returns>The rejected submission.</returns>
    [HttpPost("Submissions/{id}/Reject")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubmissionDto>> PostReject([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (!Config().AllowUserSubmissions)
        {
            return SubmissionsOff();
        }

        (RejectBody? body, ObjectResult? unreadable) = await ReadRequestAsync<RejectBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return unreadable!;
        }

        string adminName = User.Identity?.Name ?? string.Empty;
        try
        {
            Submission rejected = Submissions().Reject(id, adminName, body.Reason);
            logger.LogInformation("{User} rejected submission {SubmissionId} by {Submitter}.", adminName, rejected.Id, rejected.UserName);
            return ToDto(rejected);
        }
        catch (SubmissionException ex)
        {
            return SubmissionProblem(ex);
        }
    }

    private static SubmissionService Submissions()
    {
        string folder = Plugin.Instance?.SubmissionsFolder ?? Path.Combine(Path.GetTempPath(), "SubtitleQuickEdit", "submissions");
        return new SubmissionService(new SubmissionStore(folder), NewSaveService());
    }

    private static string ItemName(BaseItem item) => item is Episode episode
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{episode.SeriesName} S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00} {episode.Name}").Trim()
        : item.Name;

    private static SubmittedEditDto ToDto(SubmittedEdit edit) => new()
    {
        Type = edit.Kind == SubmittedEditKind.Add ? "add" : "edit",
        CueIndex = edit.CuePosition,
        CueStart = edit.CueStartMs,
        OldText = edit.OldText,
        NewText = edit.NewText,
        ClientId = edit.ClientId,
        StartMs = edit.StartMs,
        EndMs = edit.EndMs,
        Text = edit.Text,
        Outcome = edit.Outcome.ToString(),
        Message = edit.Message,
    };

    private ObjectResult SubmissionsOff() =>
        Error(StatusCodes.Status409Conflict, "SubmissionsOff", "User submissions are turned off in the plugin settings. Turn them on to review submissions.");

    private ObjectResult SubmissionProblem(SubmissionException ex)
    {
        if (ex.Error == SubmissionError.StorageFailed)
        {
            logger.LogError(ex, "Subtitle Editor could not store a submission.");
        }

        return ex.Error switch
        {
            SubmissionError.NotFound => Error(StatusCodes.Status404NotFound, "SubmissionNotFound", ex.Message),
            SubmissionError.NotPending => Error(StatusCodes.Status409Conflict, "NotPending", ex.Message),
            SubmissionError.TooManyPending => Error(StatusCodes.Status429TooManyRequests, "TooManyPending", ex.Message),
            SubmissionError.TextTooLong => Error(StatusCodes.Status400BadRequest, "TextTooLong", ex.Message),
            SubmissionError.InvalidChoice => Error(StatusCodes.Status400BadRequest, "InvalidChoice", ex.Message),
            _ => Error(StatusCodes.Status500InternalServerError, "StorageFailed", ex.Message),
        };
    }

    private SubmissionDto ToDto(Submission submission, BaseItem? item = null, string? itemName = null)
    {
        item ??= Guid.TryParse(submission.ItemId, out Guid itemId) ? resolver.FindVideo(itemId) : null;
        MediaStream? stream = item is null ? null : resolver.GetSubtitleStreams(item).FirstOrDefault(s => s.Index == submission.StreamIndex);
        string? label = stream is null
            ? null
            : string.Join(" - ", new[] { stream.Language, stream.Title }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return new SubmissionDto
        {
            Id = submission.Id,
            UserId = submission.UserId,
            UserName = submission.UserName,
            ItemId = submission.ItemId,
            ItemName = itemName ?? (item is null ? null : ItemName(item)),
            StreamIndex = submission.StreamIndex,
            StreamLabel = string.IsNullOrEmpty(label) ? null : label,
            BaseHash = submission.BaseHash,
            Note = submission.Note,
            Created = submission.CreatedUtc,
            State = submission.State.ToString(),
            Decided = submission.DecidedUtc,
            DecidedBy = submission.DecidedBy,
            Reason = submission.Reason,
            Edits = submission.Edits.Select(ToDto).ToList(),
        };
    }

    /// <summary>
    /// Adds an entry to Jellyfin's activity log (Dashboard, Activity), so admins see that a submission is waiting. A
    /// failure is logged and otherwise ignored: the submission is already stored.
    /// </summary>
    private async Task AnnounceAsync(EditorRights rights, BaseItem item, string itemName, int count)
    {
        try
        {
            var entry = new ActivityLog(
                string.Create(CultureInfo.InvariantCulture, $"{rights.UserName} submitted {count} subtitle change(s) for review"),
                "SubtitleQuickEditSubmission",
                rights.User!.Id)
            {
                ShortOverview = itemName,
                Overview = "Review it in Dashboard, Plugins, Subtitle Editor.",
                ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
                LogSeverity = LogLevel.Information,
            };
            await activityManager.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The activity log is a courtesy; any failure there must not fail the submission.
            logger.LogWarning(ex, "Subtitle Editor could not add the submission to the activity log.");
        }
    }
}
