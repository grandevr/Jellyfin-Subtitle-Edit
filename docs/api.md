# Server API

All routes are under `/SubtitleQuickEdit`. The client sends an item id and a subtitle stream index, never a path: the
file comes from Jellyfin's own library data (`IMediaSourceManager.GetMediaStreams`). JSON property names are camelCase.
Every error answer has the shape `{ "code", "message", "cueIndex"?, "currentHash"? }`.

| Method and route | Who | Answers |
|---|---|---|
| `GET Me` | anyone signed in | `isAdmin`, `canEdit`, `canSubmit`, `submissionsOn`, `pendingReviews` (admins only, else null), `maxNoteLength` |
| `GET Items/{itemId}/Subtitles` | editors and submitters (below), for items visible to them (library access included) | the item's subtitle streams: `streamIndex`, `language`, `title`, `external`, `format`, `editable`, `reason`, `extractable` (embedded text track of a local video), `extractedStreamIndex` (the external file its extraction would write, when that already exists) |
| `GET Items/{itemId}/Subtitles/{streamIndex}/Cues` | same | `hash` (SHA-256 of the file), `encoding`, and the cues (`index` = 1-based position, `start`, `end` in ms, `text` with `\n`) |
| `POST Items/{itemId}/Subtitles/{streamIndex}/Edits` | editors (403 for everyone else) | `hash`, `savedCueIndexes`, `addedCues` (client id to position), `writeMethod` |
| `POST Items/{itemId}/Subtitles/{streamIndex}/Restore` | editors | `hash`, `restored` (false when the file already had that content), `writeMethod` |
| `POST Items/{itemId}/Subtitles/{streamIndex}/Submissions` | submitters | the stored submission (below) |
| `GET Items/{itemId}/Subtitles/{streamIndex}/Extract` | editors and submitters | preview: `fileName`, `format`, `cueCount`, `exists`, `existingHash`, `existingStreamIndex`, `existingIsExtracted`, `canReplace`, and for admins when a file exists `cues` and `existingCues` |
| `POST Items/{itemId}/Subtitles/{streamIndex}/Extract` | editors and submitters; replacing: admins | `fileName`, `hash`, `replaced`, `streamIndex` (null while the library refresh runs), `refreshPending` |
| `GET Extracted` | admins | the files made from embedded tracks, newest first: `id`, `itemId`, `itemName`, `sourceStreamIndex`, `fileName`, `streamIndex`, `exists`, `edited`, `replaced`, `userId`, `userName`, `created` |
| `DELETE Extracted/{id}` | admins | `fileName`, `backedUp` |
| `GET Submissions/Mine` | anyone signed in | the caller's own submissions, newest first, in every state |
| `POST Submissions/{id}/Withdraw` | the submitter, while pending (also with submissions off) | the submission, now `Withdrawn` |
| `GET Submissions?status=pending\|decided\|all` | admins | submissions, newest first (default `pending`) |
| `POST Submissions/{id}/Approve` | admins, while submissions are on | `submission`, `applied`, `hash`?, `writeMethod`? |
| `POST Submissions/{id}/Reject` | admins, while submissions are on | the submission, now `Rejected` |

Who: **admins** may do everything. **Editors** are admins plus the users that "Allow user edits" lets save directly
(all users, or the selected ones). **Submitters** are the other users, when "Allow user submissions" is on; an editor
never submits. Editors that are not admins cannot review submissions or change settings.

Submission body: like an Edits body plus `"note"?` (at most 500 characters). It is checked exactly as a save would be
and stored as a pending submission in `data/SubtitleQuickEdit/submissions/{id}.json` (outside the plugin folder); the
file is not touched, and Jellyfin's activity log gets an entry so admins notice. A submission answers with `id`,
`userId`, `userName`, `itemId`, `itemName`, `streamIndex`, `streamLabel`, `baseHash`, `note`, `created`, `state`
(`Pending`, `Approved`, `PartlyApproved`, `Rejected`, `Withdrawn`), `decided`, `decidedBy`, `reason` and `edits` (each
with `type`, the edit's fields, `cueStart` and `outcome`: `Pending`, `Applied`, `Conflict`, `NotChosen`, plus a
`message` for a conflict). Approve body: `{ "edits"?: [0-based indexes] }` (all when left out). When the file changed
since the submission, each edited line is looked for again (same position and text, else the one line with the same
start and text); changes not found are conflicts and are not written. If no chosen change can be written, nothing is
written, `applied` is false and the submission stays pending with its conflicts marked. Reject body: `{ "reason"? }`.
The newest 50 decided submissions are kept; pending ones are never deleted. Extra codes: 404 `SubmissionNotFound`;
409 `NotPending` / `SubmissionsOff`; 400 `TextTooLong` / `InvalidChoice`; 429 `TooManyPending`; 500 `StorageFailed`.

Extract body: `{ "replace"?, "expectedExistingHash"? }`. Jellyfin extracts the embedded track into its cache (its own
subtitle encoder; ASS and SSA as they are, every other text codec as SubRip) and the plugin writes it next to the video
as `{video name}.{language}[.default][.forced][.sdh].{srt|ass|ssa}` (two-letter language code when there is one), with
the video's owner and group and its mode without execute bits, then refreshes the item. The video is never changed. A
file of that name is never overwritten: 409 `FileExists` (with `currentHash`), unless an admin sends `replace` with the
`existingHash` the preview showed (409 `FileChanged` when it no longer matches, 403 for others); the replaced file is
kept as `…_replaced.srt` in the backups. Each extraction is recorded in `data/SubtitleQuickEdit/extracted/{id}.json`;
`DELETE Extracted/{id}` keeps the file's last content as `…_deleted.srt` and refreshes the item. A user who is not an
admin gets an activity log entry for each extraction. Extra codes: 400 `AlreadyExternal` / `NotExtractable` (picture
track, not a local video); 413 `TooLarge`; 422 `NotEditable`; 404 `ExtractedNotFound`; 500 `ExtractFailed`.

Edits body: `{ "baseHash", "edits": [ {"type":"edit","cueIndex","oldText","newText"} | {"type":"add","clientId","startMs","endMs","text"} ], "keepRestorePoint"? }`.
With `keepRestorePoint` (the editor sends it on the first save of a browser session) the file as it was is also kept
as a restore point (`…_restore.srt`, the newest 3 per file).

Restore body: `{ "baseHash", "targetHash" }`. Writes back, byte for byte, the kept backup (original, save or restore
point) whose SHA-256 is `targetHash`, after backing up the current file. 404 `RestorePointNotFound` when none matches,
409 `FileChanged` when the file's hash is not `baseHash`. The panel's "Revert to original" button uses it.

Status codes: 400 `InvalidRequest` / `InvalidEdit` / `CueNotFound` / `NotEditable` (embedded, not .srt, not local); 403
`Forbidden`; 404 `ItemNotFound` / `StreamNotFound` (a non-admin cannot probe for items: without any right they get
403 before the item is looked up, and without library access 404, never a hint of what exists); 409 `FileChanged` (with
`currentHash`) / `TextMismatch` (with `cueIndex`); 413 `RequestTooLarge` (over 1 MB); 422 `Unrepresentable` /
`NotEditable` (file not parseable); 500 `NotWritable` (names the folder and the user) / `WriteFailed` /
`VerificationFailed` / `LeftoverUnrecoverable`. A refused request never changes the file, makes a backup or leaves a
temp file.

Save rules in order (`Core/Editing/SubtitleSaveService`): the stream must be an external local `.srt`, `.vtt`, `.ass` or `.ssa` file of that item; the
file must parse; its hash must equal `baseHash`; every edit must be valid and its `oldText` must match; the original is
backed up; the new content is written by the hybrid writer. Jellyfin reads external `.srt` files on every request, so
nothing is refreshed or invalidated afterwards.

