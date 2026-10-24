# Subtitle Quick Edit: status (project ON HOLD after stage 2, 2026-10-16)

Spec: the "Jellyfin Subtitle Quick-Edit Plugin — Spec" doc (claude.ai artifact YP25tXFeL6JFKs3i32VdNx). Read it again
before resuming: it is the source of truth and was updated after every stage. Nothing has been installed on the live
Jellyfin or touched on the live Bazarr. All work is on a throwaway Jellyfin on the test host.

## State at the pause

- Solution builds clean (0 warnings, 0 errors, `TreatWarningsAsErrors`, `AnalysisMode=AllEnabledByDefault`, no rule relaxed).
- 248 of 248 unit tests pass with `SQE_REQUIRE_INTEGRATION=1` (nothing silently skipped).
- 75 of 75 API checks pass against the test server (`testdata/stage2_api_tests.py`).
- Mutation check: control passes, 17 of 17 mutants killed.
- Nothing is half-applied. The fixtures on the test server are pristine (the API test script resets them when it ends).

## Done

**Stage 0: facts about Jellyfin 12.2** (`STAGE0-NOTES.md`)
- Runtime net10.0. Official plugin template is net9.0/10.11.5, so we use our own copy bumped to net10.0,
  Jellyfin.Controller/Model 12.2.0, targetAbi 12.2.0.0 (builds with no analyzer changes).
- File Transformation 3.0.2.0 has a 12.2 build and loads on the test server.
- External `.srt` files are read from disk on every request: no server cache, no refresh needed after an edit.
  The web client loads text subtitles with `fetch(.../Stream.js)`. A refresh is only needed for added or removed files.
- Jellyfin's subtitle code uses UtfUnknown (UTF.Unknown 2.7.0), not Ude. The web folder is root-owned (no index.html patching).
- Jellyfin 12 ignores `X-Emby-Authorization`; use `Authorization: MediaBrowser ... Token="..."`.

**Stage 1: SRT core** (`src/SubtitleQuickEdit.Core`: `Srt/`, `Storage/`)
- Lenient byte-offset parser; edits are spliced into the original bytes (no-op save is byte-identical); post-edit self-check.
- Same encoding detector as Jellyfin; strict encodings; undetectable encoding = not editable.
- Hybrid writer: temp file + verify, rename if owner/group/mode can be matched, otherwise `.inplace` marker + in-place copy
  + hash verification; leftovers are recovered; hidden non-subtitle temp names; per-file locking.

**Between stages 1 and 2**
- fsync of the temp file, of the folder after each rename/copy/cleanup, and of the `.inplace` marker rename; a failed folder
  fsync is a note, not a failed save.
- `PlatformServices.Select(isLinux)`: off Linux no native code is created, plain rename path, startup notice logged once.
- Project moved to its own folder.

**Stage 2: plugin skeleton and server API** (`src/Jellyfin.Plugin.SubtitleQuickEdit`, `Core/Editing`)
- Plugin "Subtitle Quick Edit" 0.2.0.0 (id `b3b6f2a4-6f0e-4d5c-9a0c-5e2f6c1d7a31`), settings page, startup log.
- API under `/SubtitleQuickEdit`: `GET Items/{id}/Subtitles`, `GET Items/{id}/Subtitles/{index}/Cues`,
  `POST Items/{id}/Subtitles/{index}/Edits` (details and status codes in `README.md`).
- Save rules in order; backups in `/config/data/SubtitleQuickEdit/backups` (`ProgramDataPath`, outside the plugin folder;
  survive an update and a rollback, tested); `BackupKeepLastSaves` setting (default 5).
- Access: Edits is admin-only. Subtitles and Cues are admin-only unless submissions are on, then any user with access to
  the item (`IsVisibleStandalone`: library access included; a defect here was found by the API tests and fixed).
- Oversize requests answer 413 (body read by hand: Jellyfin's middleware turns Kestrel's exception into a 500).

## Next: stage 3, the client script (from the spec)

Test on the test server only; deploy to live only after the whole acceptance checklist passes and with the user's approval.
- **Injection:** a JS file and CSS served by the plugin, injected into jellyfin-web through the File Transformation plugin
  (3.0.2.0 or later, registered by reflection: it cannot be referenced as a library). The plugin never patches
  `index.html`. If File Transformation is missing or fails, log a clear error and keep the server API working.
- **Button:** in the player's bottom control bar next to the subtitle button; shown only when the active subtitle track is an
  editable external `.srt` and the user is an admin (or submissions are on and the user has access to the item).
  `MutationObserver`; fallback floating button; all selectors in one constants block at the top of the file.
- **Panel:** right side panel on desktop, full-width bottom sheet under about 600 px. Esc closes, Ctrl+Enter applies, Ctrl+S
  saves; key events never reach the player while the panel has focus (no space/f shortcuts while typing).
- **Flow:** click pauses and opens the panel with the cue at the current time (nearest previous if none; each overlapping cue
  gets its own box); Previous/Next step cues and seek; Apply stages an edit; Save sends everything in one request; Reload
  subtitles switches the track off and on (fallback: reload the video at the current position).
- **Add line:** start = now, end = start + 2 s or the next cue's start if sooner (minimum 0.5 s); `hh:mm:ss,mmm` fields, "Set to
  now", ±100 ms nudges, Preview (from 1 s before start to the end), non-blocking overlap warning; a staged new line can be
  reopened from the list.
- **"Changes this session" list:** cue number and start (click seeks), old and new text (or "New" with times and text),
  undo, status staged/saved/failed; editing a cue twice updates its row; saved rows stay until the panel closes or the page reloads.
- **State:** item/track/position from `GET /Sessions` (this device's id) with `<video>.currentTime` when there is a `<video>`
  element, otherwise Sessions API for position, pause and seek; cues from the Cues endpoint, cached until Save or Reload;
  staged edits kept in `localStorage` per item and stream (try/catch); confirm prompt when leaving with unsaved edits.
- **Save results:** success marks rows saved and stores the new hash; 409 re-fetches cues, keeps staged edits whose
  `oldText` still matches, marks the rest failed, and always keeps staged new lines; after adds, renumber the list using the
  `clientId` mapping.
- **Tests:** acceptance checklist sections 1 and 2 plus "typing in the panel never triggers player shortcuts", by browser
  automation at desktop and phone width; then you test a desktop browser (required) and the Android app's web player (best
  effort). iOS and Jellyfin Media Player are out of scope. Stages 4 (submissions and review) and 6 (acceptance, packaging,
  deployment) follow; Bazarr is dropped from v1 entirely.

## Open decisions

1. **Secrets location.** The test secrets live in the session scratchpad (below), which is session-specific temporary
   storage and may be gone when the work resumes. They are throwaway, but your rule is that passwords live in your password
   manager. Options: re-create the users and tokens on resume (a few commands, below), or keep a mode-600 secrets folder in
   the project for the test server only. Your call.
2. **Automated browser tests.** Stage 3 needs a headless browser against the test server. The plan was a Playwright container
   on the test host (an image of roughly 1 to 2 GB; the test host had about 16 GB free). Approve that, or choose manual-only testing.
3. **Teardown** (below) happens only when the project is finished; confirm the list then.
4. **Not decided yet, needed at deployment:** installing File Transformation on the live Jellyfin is a deployment step, and
   restarting the live Jellyfin interrupts playback (done only with approval).

## Test server (the test host, throwaway, STOPPED at the pause, nothing removed)

- Container `jellyfin-test` (`--restart no`), LAN-only port 8097, image pinned by digest to the live image
  (`lscr.io/linuxserver/jellyfin@sha256:799509f8ef3be7037478699cc6fd14d19dd75faac5e474ed219e98ae78a5b3e4`, 12.2.0). UPnP is off.
  Also pulled: `mcr.microsoft.com/dotnet/sdk:10.0`.
- Folders: `$SQE_TEST_ROOT/config` (Jellyfin config and plugins), `$SQE_TEST_ROOT/media` (test videos and subtitles),
  `$SQE_TEST_ROOT/src` (`sqe`: synced copy of the source; `.nuget`: package cache; `template-bump`: the stage 0 template
  build check; `mismatch`: the gid-2000 fixture file, kept for now, goes in the final teardown).
- Library `TestTV` with three episodes. Episodes 1 and 2: five external `.srt` each in the encodings and line endings from the
  spec (group 1000, so saves take the in-place path). Episode 3: an embedded subtitle plus `.en.srt`, `.ja.ass`, `.de.vtt` and
  a `.fr.srt` with a stray paragraph (group of the Jellyfin user, so saves take the atomic rename).
- Plugins installed there: File Transformation 3.0.2.0, Subtitle Quick Edit 0.2.0.0.
- Users: `testadmin` (administrator), `testuser` (regular), `limiteduser` (no library access). Plugin setting "Allow user
  submissions" is Off.
- Passwords and tokens: `<secrets-dir>/`
  (`admin.pw`, `token`, `testuser.pw`, `testuser.token`, `limiteduser.pw`, `limiteduser.token`; mode 600). The admin
  password was shown to the user in the stage 0 report; the other two were never printed. If that folder is gone: log in as
  `testadmin` with that password to get a token, delete `testuser` and `limiteduser`, and re-run
  `testdata/create_test_users.py` (it skips users that already exist).
- Reset fixtures any time: `testdata/reset_fixtures.sh`.

## Commands to resume (each is one line; run from the project folder)

- Start the test server: `$SQE_HOST_EXEC docker start jellyfin-test`
- Wait until it answers: `until curl -s -m 3 $JF_BASE/System/Info/Public | grep -qi '"version"'; do sleep 2; done`
- Sync the source to the test host: `devtools/sync.sh`
- Clean rebuild: `devtools/dotnet.sh build -c Release --no-incremental`
- Re-arm the foreign-group fixture: `devtools/prepare_mismatch.sh`
- Unit tests (all integration tests required): `DOCKER_FLAGS="-v $SQE_TEST_ROOT/src/mismatch:/mismatch -v $SQE_TEST_ROOT/media:/fixtures:ro -e SQE_MISMATCH_FILE=/mismatch/file.srt -e SQE_FIXTURE_DIR=/fixtures -e SQE_REQUIRE_INTEGRATION=1" devtools/dotnet.sh test -c Release --no-build`
- Install the plugin on the TEST server: `DOCKER_FLAGS="-v $SQE_TEST_ROOT/config/data/plugins:/plugins" devtools/dotnet.sh build src/Jellyfin.Plugin.SubtitleQuickEdit -c Release --no-incremental -o /plugins/SubtitleQuickEdit_0.2.0.0`
- Restart the test server to load it: `$SQE_HOST_EXEC docker restart jellyfin-test`
- API tests: `python3 -I testdata/stage2_api_tests.py <secrets-dir>`
- Mutation check (about 8 minutes): `python3 -I devtools/mutation_check.py` (needs `$SQE_TEST_ROOT/src/sqe-mut`, created with `$SQE_HOST_EXEC mkdir $SQE_TEST_ROOT/src/sqe-mut` and `$SQE_HOST_EXEC chown 1000:1000 $SQE_TEST_ROOT/src/sqe-mut`)
- Stop the test server again: `$SQE_HOST_EXEC docker stop jellyfin-test`

## Teardown when the project is finished (nothing below is done yet)

`docker rm -f jellyfin-test` on the test host; `rm -rf $SQE_TEST_ROOT` (config, media, source copy and the gid-2000 fixture);
`docker rmi` of the pinned Jellyfin image and of `mcr.microsoft.com/dotnet/sdk:10.0` (and the Playwright image if one is
pulled); delete the scratchpad secrets. Nothing from this project exists on the live Jellyfin yet, so there is nothing to undo there.

## Gotchas learned

- A write that renames a new file over the gid-2000 fixture resets its group: run `devtools/prepare_mismatch.sh` before tests.
- `dotnet test` has no `--no-incremental`: use `dotnet build --no-incremental`, then `dotnet test --no-build`.
- Jellyfin drops an external `.srt` that ffprobe cannot read at scan time; a `FullRefresh` (not `ValidationOnly`) retries a file it
  rejected earlier. `ValidationOnly` is enough to pick up a newly added file.
- Jellyfin omits null JSON properties (a track without a language has no `language` key).
- The Read tool is blocked outside the working directory (`blockReadsOutsideWorkingDirectories`); the project now lives inside it.
- `STAGE0-NOTES.md`, `upstream-template/`, `template-bump/` and `upstream-template.tar.gz` in the project root are stage 0
  reference material and can be deleted when the project is finished.
