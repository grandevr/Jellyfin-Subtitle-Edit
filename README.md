# Subtitle Editor

A Jellyfin 12.3 plugin that edits the text of the subtitle line on screen (or adds a new line) in external `.srt`
files, and from 1.1 also WebVTT (`.vtt`) and ASS/SSA (`.ass`, `.ssa`). Until 1.0 it was called "Subtitle Quick Edit"; the
internal name (`SubtitleQuickEdit`: assembly, API routes, data and plugin folders) is unchanged so settings, backups and
submissions carry over. Spec: the "Jellyfin Subtitle Quick-Edit Plugin — Spec" doc. Licence: GPL-3.0 (see `LICENSE`).

Status: **1.1 is live**; 1.2 (making subtitle tracks inside a video editable, stage 6) is in progress.

## Versions

Docs name versions as 1.0, 1.1, 1.2; Jellyfin shows them with four numbers (1.2.0.0), which is also the plugin's
folder name (`SubtitleQuickEdit_1.2.0.0`), so an update removes the old versioned folder.

| Version | What | Code |
|---|---|---|
| 1.2 | Make an embedded text track editable: a copy in its own format next to the video, Replace with a comparison for admins, the copies listed in the settings (stage 6) | in progress |
| 1.1 | Edit WebVTT (`.vtt`) and ASS/SSA (`.ass`, `.ssa`) files as well as `.srt` (stage 5); renamed to Subtitle Editor | main c19c155, 2026-10-25 |
| 1.0 | `.srt` editing in the web player, backups and Revert, submissions for review and "Allow user edits" (stages 0 to 4). Built as 0.2.0.0 | main ff112ca, 2026-10-25 |

## Layout

| Path | What |
|---|---|
| `src/SubtitleQuickEdit.Core` | Jellyfin-free library: `Subtitles/` (the format-neutral document, cue and edit model, encodings), `Srt/` (parse, edit .srt), `Storage/` (safe file write, backups, platform services), `Editing/` (the save rules) |
| `src/Jellyfin.Plugin.SubtitleQuickEdit` | The plugin: controller, settings page, item/stream resolver, startup logging, File Transformation registration |
| `src/Jellyfin.Plugin.SubtitleQuickEdit/Client` | The web player script: `logic.js` (page-free logic, Node-tested in `test/`), `quickedit.js` (button and panel), `quickedit.css` |
| `tests/SubtitleQuickEdit.Core.Tests` | xUnit tests of the core |
| `testdata/` | Scripts for the throwaway Jellyfin test server: fixtures, users, `stage2_api_tests.py`, `stage4_api_tests.py`, `stage5_api_tests.py`, the TestWebm library (`make_webm.sh`, `reset_webm03_srt.sh`, `reset_webm03_formats.sh`) |
| `testdata/browser` | Browser tests: `stage3_tests.py` and `stage5_tests.py` (headless Chromium against the test server), `fake_page_smoke.js` and `config_page_smoke.js` (no server), `reload_spike.py` |
| `devtools/` | `sync.sh`, `dotnet.sh` (build/test in the SDK container on the test host), `prepare_mismatch.sh`, `mutation_check.py` |
| `STAGE0-NOTES.md` | What Jellyfin 12.2 actually does (cache, encoding, refresh) |
| `docs/client-notes.md` | What jellyfin-web 12.2 actually does (load order, OSD, Sessions lag, Reload, phone width) |

## Building, testing, installing on the test server

The scripts in `devtools/` and `testdata/` drive a throwaway Jellyfin test server and nothing else. They read their
settings from the environment (see `devtools/env.sh`; there are no defaults, a missing variable stops the script):

| Variable | Meaning | Example |
|---|---|---|
| `SQE_HOST_EXEC` | Command prefix that runs a command string on the test host; it gets the whole quoted command as one last argument (stdin is passed through) | `ssh myhost`, `ssh myhost pct exec 103 --`, or `sh -c` for a local test server |
| `SQE_TEST_ROOT` | Test server folder on that host, holding `config/`, `media/` and `src/` | `/srv/jellyfin-test` |
| `JF_BASE` | Test server URL, used by `testdata/*.py` | `http://test-host:8097` |
| `SQE_LOG_CMD` | Optional: command that prints the test container's last 2000 log lines to stdout, for `stage2_api_tests.py`, `stage4_api_tests.py` and `stage5_api_tests.py` (default: `docker logs --tail 2000 $SQE_CONTAINER` through `SQE_HOST_EXEC`). Split like a shell line, no pipes | `my-log-tool jellyfin-test 2000` |
| `SQE_CONTAINER` | Optional: test server container name (default `jellyfin-test`) | |
| `SQE_DIR` | Optional: synced project folder on the host (default `$SQE_TEST_ROOT/src/sqe`) | |

A convenient place for them is an untracked `local.env` in the repo root (git-ignored, as is `*.local`), with
`export SQE_HOST_EXEC=...` lines, loaded with `. ./local.env` before running the scripts. Keep secrets out of it.

`<secrets-dir>` in the docs is any mode-700 folder for the test server's admin password and tokens; it is never committed.

- Unit tests: `devtools/sync.sh && devtools/dotnet.sh test -c Release` (needs the test host, see below). Some tests need
  the test server's files, a file in a foreign group and a non-root user; set `SQE_REQUIRE_INTEGRATION=1` (with
  `SQE_FIXTURE_DIR` and `SQE_MISMATCH_FILE`) so that skipping them is a failure. Re-arm the foreign-group file with
  `devtools/prepare_mismatch.sh` first: a write that renames a new file over it resets its group, which is exactly what
  the test is there to catch.
- Install on the test server only: build the plugin straight into its plugin folder
  (`DOCKER_FLAGS="-v $SQE_TEST_ROOT/config/data/plugins:/plugins" devtools/dotnet.sh build
  src/Jellyfin.Plugin.SubtitleQuickEdit -c Release -o /plugins/SubtitleQuickEdit_1.2.1.0`, after removing any older `SubtitleQuickEdit_*` folder) and restart the container.
  Jellyfin writes its own `meta.json`. `UtfUnknown.dll` is not shipped: the plugin uses the copy Jellyfin already has.
- API tests: `python3 -I testdata/stage2_api_tests.py <secrets-dir>` resets the fixtures and runs 75 checks against the
  test server (see below).
- Stage 4 API tests: `python3 -I testdata/stage4_api_tests.py <secrets-dir>` covers "Allow user edits", `Me`, and
  submitting, withdrawing, approving (also after the file changed, with conflicts and in part) and rejecting. It uses
  `testuser` (sees the library) and `limiteduser` (does not), and puts the settings back to the defaults at the end.
- Stage 5 API tests: `python3 -I testdata/stage5_api_tests.py <secrets-dir>` writes a richer `.vtt` and `.ass` over
  episode 3's and checks cues, refusals, saves byte for byte (BOM, CRLF, tags, comments and other blocks kept), backups
  with the subtitle's extension, Restore, and an approved submission on an ASS file. It resets the fixtures at the end.
- `devtools/mutation_check.py` breaks the code in scratch copies (38 ways) and requires the unit tests to fail for each break.
- Client, no server needed: `node --test src/Jellyfin.Plugin.SubtitleQuickEdit/Client/test/*.test.js` (181 tests),
  `node src/Jellyfin.Plugin.SubtitleQuickEdit/Client/test/mutation-check.js` (54 of 54 killed), and with Playwright for
  Node on `NODE_PATH`, `node testdata/browser/fake_page_smoke.js` (91 checks against a fake jellyfin-web page, as admin, submitter and a user without rights, with an ASS file drawn as text or by JASSUB, and with a track inside the video), and
  `node testdata/browser/config_page_smoke.js` (18 checks of the settings page with a mock server).
- Client against the test server: `testdata/browser/stage3_tests.py` (see its header; also the stage 4 user modes, which
  change the plugin settings and put the defaults back). It needs the TestWebm library and
  changes test subtitles: TestWebm 01 is restored through the API; run `testdata/reset_webm03_srt.sh` in the test
  container before and after, because the API cannot remove the line it adds to TestWebm 03.
  `testdata/browser/stage5_tests.py` (same way of running) edits TestWebm 03's `.vtt` and `.ass` and restores them
  through the API; run `testdata/reset_webm03_formats.sh` in the test container once before the first run.
  `testdata/browser/stage6_tests.py` (same way of running) makes an editable copy of TestWebm 03's embedded track
  through the panel, edits it, and deletes the copy through the API at the end.
- Stage 6 API tests: `python3 -I testdata/stage6_api_tests.py <secrets-dir>` makes episode 3's embedded track editable
  as a submitter, edits it, compares and replaces it as admin, lists and deletes it, and checks the file's owner and
  mode, the backups, the library refresh and the activity log entry. It resets the fixtures at the end.

## Analyzers

`TreatWarningsAsErrors` is on everywhere. Core and the plugin use `AnalysisMode=AllEnabledByDefault`; the plugin
also carries the template's StyleCop, Serilog and multithreading analyzers. **No rule is relaxed or suppressed** in
either. The only project settings beyond the template are `AllowUnsafeBlocks` in Core (the `LibraryImport` P/Invoke
stubs for `statx`, `chown`, `open`, `fsync`) and the `ExcludeAssets=runtime` on the Jellyfin packages that the template
already has. The bump of the template to net10.0, Jellyfin 12.2.0 and targetAbi 12.2.0.0 needed no rule changes.
Since 2026-10-25 the plugin builds against Jellyfin 12.3.0, the version of the live server.

The test project uses the default analyzer set (not `AllEnabledByDefault`), since rules such as test-name underscores
and per-call-site string allocations do not suit test code, and carries
`[assembly: SupportedOSPlatform("linux")]` (the writer tests use Unix file modes, hard links and libc).

## Server API

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

Save rules in order (`Core/Editing/SubtitleSaveService`): the stream must be an external local `.srt` of that item; the
file must parse; its hash must equal `baseHash`; every edit must be valid and its `oldText` must match; the original is
backed up; the new content is written by the hybrid writer. Jellyfin reads external `.srt` files on every request, so
nothing is refreshed or invalidated afterwards.

## The web player client

Requires the File Transformation plugin (3.0.2.0 or later). At startup the plugin registers a transformation of
`index.html` that adds one script tag and one stylesheet link, served anonymously from `/SubtitleQuickEdit/Client/`.
If File Transformation is missing the server API still works and the log says why there is no button.

For a user who may edit (admins, and users "Allow user edits" lets in) or submit (other users, when "Allow user
submissions" is on), playing an item whose active subtitle is an editable external `.srt`, an edit button appears next
to the subtitle button. The client asks `GET Me` which of the two it is. It pauses the video and opens a panel (a side panel on desktop, a bottom sheet under 600 px):

- one text box per line on screen (or the nearest line), with Previous/Next to step and seek;
- **Apply** (Ctrl+Enter) stages changes; **Save** (Ctrl+S) writes everything staged in one request; **Reload subtitles**
  makes the player show the saved file without restarting playback;
- **+ Add line** opens a form with start and end times, Set to now, ±100 ms, Preview and an overlap warning;
- "Changes this session" lists each change with its status (staged, saved, failed), Undo, and click to jump to it;
- unsaved changes survive a page reload (`localStorage`, per user, item and track); Esc closes the panel, and keys typed
  in the panel never reach the player.

Users who submit get the same panel titled "Suggest a subtitle fix", with **Submit for review** (Ctrl+S) and an
optional note for the admin instead of Save, Reload and Revert. Submitted changes leave the list (the file is not
changed); "Your submissions for this subtitle" lists them with their state, the admin's reason for a rejection, and
Withdraw while they are pending. Admins see a link "N submissions are waiting for your review" at the top of the panel,
to the plugin's settings page.

If the file changed on disk since it was loaded, Save writes nothing: changes that still match stay staged, the rest
are marked failed. `window.SubtitleQuickEdit.debugState()` shows the client's state (no tokens) for troubleshooting.

## Settings

Dashboard, Plugins, Subtitle Editor. Below the settings, **Submissions** lists the ones waiting for review (or the
newest 50 decided ones): item, subtitle, user, note, and each change with a checkbox. **Approve selected** writes the
ticked changes in one save; **Reject** takes an optional reason that the user sees. Deciding needs "Allow user
submissions" on.

| Setting | Default | |
|---|---|---|
| Allow user submissions | Off | Other users may submit edits for an admin to approve |
| Allow user edits (`UserEdits`) | Off | `AllUsers` or `SelectedUsers`: those users save directly, like an admin, without approval |
| Users allowed to edit (`UserEditUserIds`) | none | For `SelectedUsers`: Jellyfin user ids |
| Max pending submissions per user | 50 | |
| Backups | Original only | or "Original + last N saves" |
| N (`BackupKeepLastSaves`) | 5 | Added: the spec's "last N" needs a number |

## Backups

`data/SubtitleQuickEdit/backups` under Jellyfin's program-data folder (`/config/data/SubtitleQuickEdit/backups` in the
Docker image; `IApplicationPaths.ProgramDataPath`). It is outside the versioned plugin folder, so backups survive a
plugin update and a rollback (tested: delete the plugin folder, restart, backups remain, reinstall). Files are named
`{itemId}_{streamIndex}_{pathId}_{utc time}_original.srt`, with the subtitle's own extension (content from before the first save of that subtitle;
never overwritten or removed) and, in "Original + last N saves" mode, `…_save.srt` for the content from before each
later save, pruned to the newest N. A backup that cannot be written stops the save. `…_replaced` and `…_deleted` files
hold a subtitle file that an extraction replaced and an extracted file that an admin deleted; they are never pruned.

## Behaviour worth knowing

**Parsing** is lenient (missing blank lines, missing number lines, stray spaces, `.` or `,` before milliseconds, CR, LF
or CRLF). A file is *not editable*, with a reason, when it is empty, over 32 MB, has no detectable or an unsupported
encoding (UTF-32 included), has bytes invalid for the detected encoding, does not survive decode and re-encode
byte-for-byte, has text outside any cue, or has a `-->` line that is not a valid time line. (Jellyfin itself drops an
external `.srt` that ffprobe cannot read, so a wholly garbage file never shows up as a track.)

**Encoding** uses the same detector and version as Jellyfin's `SubtitleEncoder` (UTF.Unknown 2.7.0). A BOM wins;
ASCII counts as UTF-8 (as in Jellyfin). A legacy code page file whose encoding the detector cannot place is treated as
not editable rather than guessed. Random text is not detectable; natural text (Polish, Czech) is.
A character that the file's encoding cannot hold is refused with a message naming the encoding.

**Editing** works on bytes: only the edited cue's text bytes, the added blocks and (when something is added) the
number lines change. Every other byte, including BOM, line endings, spacing and the file's ending style, is kept.
Loading and saving with no edits, or replacing a cue with its own text, is byte-identical.
- Cues are addressed by their 1-based position, not their number line.
- New text uses the file's dominant line ending. Text must not be empty, contain a blank (or whitespace-only) line,
  contain a line that looks like a time line, or contain control characters.
- An added cue goes before the first existing cue that starts later (after existing cues with the same start); in a file
  with out-of-order cues that is a deterministic rule, not a sort. Adding renumbers every cue 1..n, also writing a number
  line for cues that had none. The file's ending style (0, 1 or 2 final line breaks) is kept.
- Added cues: start at least 0, end after start, at least 100 ms long, at most 99:59:59,999, and not after the item's
  runtime when it is given.
- After building the new bytes the result is parsed again and compared with what was intended (cue count, times, texts,
  encoding, numbering). A mismatch refuses the save. One case this catches: in a gap-less file whose next cue has no
  number line, new text ending in a bare number would be read as that cue's number.

**Writing** (`Storage/SafeFileWriter`): the new content goes to a hidden temp file in the same folder
(`.<name>.sqe-<id>-<hash>.tmp`, never a subtitle extension, never starting with the video's name), is fsynced, and is
read back and verified. If its owner, group and mode can be made equal to the original's it is renamed over the
original and the folder is fsynced. Otherwise (for example the file's group is one the Jellyfin user is not in, as on
the test server and the live server) the temp file is renamed to `….inplace`, the folder is fsynced (so the recovery
file is durable before the original is touched), the content is copied over the original in place and fsynced, and the
`.inplace` file is deleted only after the original's hash is verified, then the folder is fsynced again. The base hash
is checked before the temp file is written and again just before the change.
- A failed folder fsync is a note in the result (logged as a warning), not a failed save: the data is already in place.
- A leftover `.tmp` was never applied and is deleted by the next save. A leftover `.inplace` is an interrupted copy:
  the next save (or `RecoverLeftovers`) finishes it, or just removes it if the copy had completed. A damaged or
  duplicate `.inplace` is never guessed at: the save is refused with a message naming the file.
- Saves of one file are serialized in-process; concurrent saves from the same base hash let exactly one through.
- The rename is atomic; the in-place fallback is not, which is why the verified copy is kept until the end.

**Platforms**: `PlatformServices.Select(isLinux)` chooses the system-call services (`statx`, `chown`, `open`/`fsync`) on
Linux and, elsewhere, services that call no native code: ownership is not read or matched, the plain rename path is
used, files are still flushed, and the plugin logs a startup notice once.
