using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;
using Jellyfin.Plugin.SubtitleQuickEdit.Configuration;
using Jellyfin.Plugin.SubtitleQuickEdit.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api;

/// <summary>
/// The Subtitle Quick Edit server API. The client sends an item id and a subtitle stream index, never a path:
/// the file is found from Jellyfin's own library data.
/// </summary>
[ApiController]
[Route("SubtitleQuickEdit")]
[Authorize]
[Produces("application/json")]
public class SubtitleQuickEditController : ControllerBase
{
    private const string ElevationPolicy = "RequiresElevation";
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";
    private const int MaxRequestBytes = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SubtitleTrackResolver resolver;
    private readonly IUserManager userManager;
    private readonly ILogger<SubtitleQuickEditController> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleQuickEditController"/> class.
    /// </summary>
    /// <param name="resolver">Finds items and subtitle files.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleQuickEditController(
        SubtitleTrackResolver resolver,
        IUserManager userManager,
        ILogger<SubtitleQuickEditController> logger)
    {
        this.resolver = resolver;
        this.userManager = userManager;
        this.logger = logger;
    }

    /// <summary>
    /// Lists an item's subtitle streams and whether each can be edited.
    /// </summary>
    /// <param name="itemId">The id of the item whose subtitle streams are listed.</param>
    /// <response code="200">The streams.</response>
    /// <response code="403">The caller may not use the editor.</response>
    /// <response code="404">No such item.</response>
    /// <returns>The item's subtitle streams, each with an editable flag.</returns>
    [HttpGet("Items/{itemId}/Subtitles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<SubtitleTrackDto>> GetSubtitles([FromRoute] Guid itemId)
    {
        (BaseItem? item, ObjectResult? denied) = Authorize(itemId);
        if (item is null)
        {
            return denied!;
        }

        var tracks = new List<SubtitleTrackDto>();
        foreach (MediaStream stream in resolver.GetSubtitleStreams(item))
        {
            TrackResolution resolution = resolver.Resolve(item, stream.Index);
            var dto = new SubtitleTrackDto
            {
                StreamIndex = stream.Index,
                Language = stream.Language,
                Title = stream.Title,
                External = stream.IsExternal,
                Format = stream.IsExternal && !string.IsNullOrEmpty(stream.Path)
                    ? Path.GetExtension(stream.Path).TrimStart('.').ToLowerInvariant()
                    : stream.Codec,
            };

            if (resolution.Path is null)
            {
                dto.Reason = resolution.Message;
            }
            else
            {
                TrackInspection inspection = SrtTrackInspector.Inspect(resolution.Path);
                dto.Editable = inspection.Editable;
                dto.Reason = inspection.Reason;
            }

            tracks.Add(dto);
        }

        return tracks;
    }

    /// <summary>
    /// Gets a subtitle file's cues and its hash.
    /// </summary>
    /// <param name="itemId">The id of the item the subtitle belongs to.</param>
    /// <param name="streamIndex">The index of the subtitle stream whose cues are returned.</param>
    /// <response code="200">The cues.</response>
    /// <response code="400">The stream is not an editable external .srt.</response>
    /// <response code="403">The caller may not use the editor.</response>
    /// <response code="404">No such item or stream.</response>
    /// <response code="422">The file cannot be parsed reliably.</response>
    /// <returns>The cues and the hash.</returns>
    [HttpGet("Items/{itemId}/Subtitles/{streamIndex}/Cues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public ActionResult<CuesResponse> GetCues([FromRoute] Guid itemId, [FromRoute] int streamIndex)
    {
        (BaseItem? item, ObjectResult? denied) = Authorize(itemId);
        if (item is null)
        {
            return denied!;
        }

        TrackResolution resolution = resolver.Resolve(item, streamIndex);
        if (resolution.Path is null)
        {
            return Error(resolution.Status, resolution.Code!, resolution.Message!);
        }

        TrackInspection inspection = SrtTrackInspector.Inspect(resolution.Path);
        if (inspection.Document is null)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "NotEditable", inspection.Reason ?? "The subtitle cannot be edited.");
        }

        SrtDocument doc = inspection.Document;
        return new CuesResponse
        {
            Hash = doc.Hash,
            Encoding = doc.Encoding.Name,
            Cues = doc.Cues.Select(c => new CueDto { Index = c.Position, Start = c.StartMs, End = c.EndMs, Text = c.Text }).ToList(),
        };
    }

    /// <summary>
    /// Applies edits to a subtitle file. Admins only.
    /// </summary>
    /// <param name="itemId">The id of the item the subtitle belongs to, for saving.</param>
    /// <param name="streamIndex">The index of the subtitle stream that is edited.</param>
    /// <param name="cancellationToken">Cancels reading the request body.</param>
    /// <response code="200">Saved.</response>
    /// <response code="400">The request or an edit is invalid.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such item or stream.</response>
    /// <response code="409">The file changed since it was loaded, or a cue's text no longer matches.</response>
    /// <response code="413">The request is larger than 1 MB.</response>
    /// <response code="422">A character cannot be stored in the file's encoding, or the file is not editable.</response>
    /// <response code="500">The file could not be written (the body says why).</response>
    /// <returns>The new hash and the saved cue positions.</returns>
    [HttpPost("Items/{itemId}/Subtitles/{streamIndex}/Edits")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<EditsResponse>> PostEdits(
        [FromRoute] Guid itemId,
        [FromRoute] int streamIndex,
        CancellationToken cancellationToken)
    {
        // The size is checked here, before anything reads the body, so an oversized request gets a clean 413 instead of
        // an exception from the web server.
        if (Request.ContentLength > MaxRequestBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "RequestTooLarge", "The request is larger than 1 MB.");
        }

        BaseItem? item = resolver.FindVideo(itemId);
        if (item is null)
        {
            return Error(StatusCodes.Status404NotFound, "ItemNotFound", "No such item.");
        }

        TrackResolution resolution = resolver.Resolve(item, streamIndex);
        if (resolution.Path is null)
        {
            return Error(resolution.Status, resolution.Code!, resolution.Message!);
        }

        (EditsRequest? request, ObjectResult? unreadable) = await ReadRequestAsync<EditsRequest>(cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return unreadable!;
        }

        if (string.IsNullOrWhiteSpace(request.BaseHash))
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", "The request needs a baseHash.");
        }

        List<SrtEdit>? edits = MapEdits(request.Edits, out ObjectResult? invalid);
        if (edits is null)
        {
            return invalid!;
        }

        PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        string backupFolder = Plugin.Instance?.BackupFolder ?? Path.Combine(Path.GetTempPath(), "SubtitleQuickEdit", "backups");
        var service = new SubtitleSaveService(new SafeFileWriter(), new BackupStore(backupFolder, config.BackupMode, config.BackupKeepLastSaves));
        long? runtimeMs = item.RunTimeTicks.HasValue ? item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond : null;

        try
        {
            SaveResult result = service.Save(
                new SaveRequest(
                    item.Id.ToString("N", CultureInfo.InvariantCulture), streamIndex, resolution.Path, request.BaseHash, edits, runtimeMs, request.KeepRestorePoint));

            logger.LogInformation(
                "{User} saved {Count} edit(s) to subtitle stream {StreamIndex} of item {ItemId} ({Method}).",
                User.Identity?.Name,
                edits.Count,
                streamIndex,
                item.Id,
                result.Method);
            foreach (string note in result.Notes)
            {
                logger.LogWarning("Subtitle Quick Edit: {Note}", note);
            }

            return new EditsResponse
            {
                Hash = result.Hash,
                SavedCueIndexes = result.SavedCueIndexes,
                AddedCues = result.AddedCues,
                WriteMethod = result.Method.ToString(),
            };
        }
        catch (SrtEditException ex)
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
    }

    /// <summary>
    /// Puts a subtitle back to an earlier version kept in the plugin's backups (the original, an earlier save, or the
    /// restore point kept at the first save of an editing session), chosen by the hash of its content. Admins only.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="streamIndex">The subtitle stream index.</param>
    /// <param name="cancellationToken">Cancels reading the request body.</param>
    /// <response code="200">Restored, or the file already had that content.</response>
    /// <response code="400">The request is invalid.</response>
    /// <response code="403">Not an administrator.</response>
    /// <response code="404">No such item, stream or kept version.</response>
    /// <response code="409">The file changed since it was loaded.</response>
    /// <response code="500">The file could not be written (the body says why).</response>
    /// <returns>The new hash.</returns>
    [HttpPost("Items/{itemId}/Subtitles/{streamIndex}/Restore")]
    [Authorize(Policy = ElevationPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RestoreResponse>> PostRestore(
        [FromRoute] Guid itemId,
        [FromRoute] int streamIndex,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxRequestBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "RequestTooLarge", "The request is larger than 1 MB.");
        }

        BaseItem? item = resolver.FindVideo(itemId);
        if (item is null)
        {
            return Error(StatusCodes.Status404NotFound, "ItemNotFound", "No such item.");
        }

        TrackResolution resolution = resolver.Resolve(item, streamIndex);
        if (resolution.Path is null)
        {
            return Error(resolution.Status, resolution.Code!, resolution.Message!);
        }

        (RestoreBody? body, ObjectResult? unreadable) = await ReadRequestAsync<RestoreBody>(cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return unreadable!;
        }

        if (!IsHash(body.BaseHash) || !IsHash(body.TargetHash))
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", "The request needs baseHash and targetHash (SHA-256, hex).");
        }

        PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        string backupFolder = Plugin.Instance?.BackupFolder ?? Path.Combine(Path.GetTempPath(), "SubtitleQuickEdit", "backups");
        var service = new SubtitleSaveService(new SafeFileWriter(), new BackupStore(backupFolder, config.BackupMode, config.BackupKeepLastSaves));

        try
        {
            RestoreResult result = service.Restore(
                new RestoreRequest(item.Id.ToString("N", CultureInfo.InvariantCulture), streamIndex, resolution.Path, body.BaseHash!, body.TargetHash!));
            if (result.Restored)
            {
                logger.LogInformation(
                    "{User} restored subtitle stream {StreamIndex} of item {ItemId} to an earlier version ({Method}).",
                    User.Identity?.Name,
                    streamIndex,
                    item.Id,
                    result.Method);
            }

            foreach (string note in result.Notes)
            {
                logger.LogWarning("Subtitle Quick Edit: {Note}", note);
            }

            return new RestoreResponse { Hash = result.Hash, Restored = result.Restored, WriteMethod = result.Method?.ToString() };
        }
        catch (RestorePointNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, "RestorePointNotFound", ex.Message);
        }
        catch (SubtitleWriteException ex)
        {
            return WriteError(ex, item.Id, streamIndex);
        }
    }

    private static bool IsHash(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit);
    }

    private async Task<(T? Request, ObjectResult? Problem)> ReadRequestAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        // Read at most the limit plus one byte, so a chunked body without a Content-Length is caught too.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (buffer.Length > MaxRequestBytes)
            {
                return (null, Error(StatusCodes.Status413PayloadTooLarge, "RequestTooLarge", "The request is larger than 1 MB."));
            }
        }

        try
        {
            T? request = JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions);
            return request is null
                ? (null, Error(StatusCodes.Status400BadRequest, "InvalidRequest", "The request body is empty."))
                : (request, null);
        }
        catch (JsonException)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "InvalidRequest", "The request body is not valid JSON."));
        }
    }

    private static List<SrtEdit>? MapEdits(IReadOnlyList<EditDto> dtos, out ObjectResult? invalid)
    {
        invalid = null;
        var edits = new List<SrtEdit>(dtos.Count);
        for (int i = 0; i < dtos.Count; i++)
        {
            EditDto dto = dtos[i];
            string at = string.Create(CultureInfo.InvariantCulture, $"Edit {i + 1}: ");
            switch (dto?.Type)
            {
                case "edit" when dto.CueIndex is >= 1 && dto.OldText is not null && dto.NewText is not null:
                    edits.Add(new SrtTextEdit(dto.CueIndex.Value, dto.OldText, dto.NewText));
                    break;
                case "edit":
                    invalid = ErrorObject(StatusCodes.Status400BadRequest, "InvalidEdit", at + "an edit needs cueIndex (1 or more), oldText and newText.");
                    return null;
                case "add" when dto.ClientId is not null && dto.StartMs.HasValue && dto.EndMs.HasValue && dto.Text is not null:
                    edits.Add(new SrtAddCue(dto.ClientId, dto.StartMs.Value, dto.EndMs.Value, dto.Text));
                    break;
                case "add":
                    invalid = ErrorObject(
                        StatusCodes.Status400BadRequest,
                        "InvalidEdit",
                        at + "an added line needs clientId, startMs, endMs and text.",
                        clientId: string.IsNullOrEmpty(dto.ClientId) ? null : dto.ClientId);
                    return null;
                default:
                    invalid = ErrorObject(StatusCodes.Status400BadRequest, "InvalidEdit", at + "type must be 'edit' or 'add'.");
                    return null;
            }
        }

        if (edits.Count == 0)
        {
            invalid = ErrorObject(StatusCodes.Status400BadRequest, "InvalidEdit", "There are no edits to save.");
            return null;
        }

        return edits;
    }

    private static ObjectResult ErrorObject(int status, string code, string message, int? cueIndex = null, string? currentHash = null, string? clientId = null) =>
        new(new ErrorResponse { Code = code, Message = message, CueIndex = cueIndex, CurrentHash = currentHash, ClientId = clientId }) { StatusCode = status };

    private ObjectResult Error(int status, string code, string message, int? cueIndex = null, string? currentHash = null, string? clientId = null) =>
        ErrorObject(status, code, message, cueIndex, currentHash, clientId);

    private ObjectResult EditError(SrtEditException ex) => ex.Error switch
    {
        SrtEditError.InvalidEdit => Error(StatusCodes.Status400BadRequest, "InvalidEdit", ex.Message, ex.CuePosition, clientId: ex.ClientId),
        SrtEditError.CueNotFound => Error(StatusCodes.Status400BadRequest, "CueNotFound", ex.Message, ex.CuePosition),
        SrtEditError.TextMismatch => Error(StatusCodes.Status409Conflict, "TextMismatch", ex.Message, ex.CuePosition),
        SrtEditError.Unrepresentable => Error(StatusCodes.Status422UnprocessableEntity, "Unrepresentable", ex.Message, ex.CuePosition),
        _ => Error(StatusCodes.Status500InternalServerError, "VerificationFailed", ex.Message, ex.CuePosition),
    };

    private ObjectResult WriteError(SubtitleWriteException ex, Guid itemId, int streamIndex)
    {
        if (ex.Error == SubtitleWriteError.Conflict)
        {
            return Error(StatusCodes.Status409Conflict, "FileChanged", ex.Message, currentHash: ex.CurrentHash);
        }

        logger.LogError(ex, "Saving subtitle stream {StreamIndex} of item {ItemId} failed: {Error}", streamIndex, itemId, ex.Error);
        string code = ex.Error switch
        {
            SubtitleWriteError.NotWritable => "NotWritable",
            SubtitleWriteError.VerificationFailed => "VerificationFailed",
            SubtitleWriteError.LeftoverUnrecoverable => "LeftoverUnrecoverable",
            _ => "WriteFailed",
        };
        return Error(StatusCodes.Status500InternalServerError, code, ex.Message);
    }

    /// <summary>
    /// Decides whether the caller may use the editor on an item, and finds the item. Admins always may; others
    /// only when submissions are on and the item is visible to them (library access included).
    /// </summary>
    private (BaseItem? Item, ObjectResult? Denied) Authorize(Guid itemId)
    {
        bool admin = User.IsInRole(AdministratorRole);
        if (!admin && Plugin.Instance?.Configuration.AllowUserSubmissions != true)
        {
            return (null, Error(StatusCodes.Status403Forbidden, "Forbidden", "Only administrators can use Subtitle Quick Edit."));
        }

        BaseItem? item = resolver.FindVideo(itemId);
        if (item is null)
        {
            return (null, Error(StatusCodes.Status404NotFound, "ItemNotFound", "No such item."));
        }

        if (!admin)
        {
            var user = Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out Guid userId) ? userManager.GetUserById(userId) : null;
            if (user is null || !item.IsVisibleStandalone(user))
            {
                return (null, Error(StatusCodes.Status404NotFound, "ItemNotFound", "No such item."));
            }
        }

        return (item, null);
    }
}
