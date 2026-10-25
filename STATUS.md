# Subtitle Quick Edit: status (stage 3 done, 2026-10-24; waiting on the hand test)

Spec: the "Jellyfin Subtitle Quick-Edit Plugin — Spec" doc (claude.ai artifact YP25tXFeL6JFKs3i32VdNx). Read it again
before resuming: it is the source of truth and was updated after every stage. Nothing has been installed on the live
Jellyfin or touched on the live Bazarr. All work is on a throwaway Jellyfin on the test host.

## State now

- Solution builds clean (0 warnings, 0 errors, `TreatWarningsAsErrors`, `AnalysisMode=AllEnabledByDefault`, no rule relaxed).
- Server: 248 of 248 unit tests (with `SQE_REQUIRE_INTEGRATION=1`), 75 of 75 API checks, mutation check 17 of 17 (stage 2).
- Client: 158 Node tests (`Client/test`), client mutation check 38 of 38, fake-page smoke test 35 of 35, and the browser
  tests against the test server (`testdata/browser/stage3_tests.py`): 42 of 42 at stage 3f, plus the 3g phone checks.
- The test subtitles are pristine after every browser run (TestWebm 01 restored through the API; TestWebm 03's `.en.srt`
  by `testdata/reset_webm03_srt.sh`).
- Plugin version is still 0.2.0.0; it is bumped at packaging (stage 6).

## Done

**Stage 0: facts about Jellyfin 12.2** (`STAGE0-NOTES.md`)
- Runtime net10.0. Official plugin template is net9.0/10.11.5, so we use our own copy bumped to net10.0,
  Jellyfin.Controller/Model 12.2.0, targetAbi 12.2.0.0 (builds with no analyzer changes).
  2026-10-25: bumped to Jellyfin.Controller/Model 12.3.0 to match the live server (12.3.0); the test server was
  upgraded to the live image the same day.
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

**Stage 3: the client script** (`src/Jellyfin.Plugin.SubtitleQuickEdit/Client`, PRs #5 to #9 and the 3g PR)
- 3a: the plugin serves `quickedit.js` (logic.js + quickedit.js) and `quickedit.css` anonymously and registers an
  `index.html` transformation with File Transformation by reflection. What jellyfin-web 12.2 really does is recorded in
  `docs/client-notes.md`.
- 3b: `logic.js`, a page-free module with the time, cue lookup, validation, staging, conflict and draft logic (Node tests).
- 3c: the edit button next to the subtitle button (floating fallback), admin check, Sessions lookup plus menu tracking,
  side panel (bottom sheet under 600 px), key isolation.
- 3d: one text box per cue on screen, Previous/Next with seek, Apply, "Changes this session" with Undo and click-to-seek.
- 3e: Save (one request), 409 handling, Reload subtitles (session command off/on, fallback: replace the track's cues),
  drafts in `localStorage`, leave-page prompt, Ctrl+S.
- 3f: Add line (prefill, Set to now, ±100 ms, Preview, overlap warning, reopen from the list, renumbering after save).
- 3g: 44 px touch targets, focus rings, contrast checked by a test, phone-width checks, docs.

## Next

1. **Hand test** (required): a desktop browser against the test server, then the Android app's web player (best effort).
2. Remove the Playwright image from the test host once stage 3 is accepted (it can be rebuilt for stage 4).
3. Stage 4: submissions and review for non-admins. Stage 6: acceptance, packaging, deployment (only with approval).

## Open decisions

1. **Teardown** (below) happens only when the project is finished; confirm the list then.
2. **Not decided yet, needed at deployment:** installing File Transformation on the live Jellyfin is a deployment step, and
   restarting the live Jellyfin interrupts playback (done only with approval).
3. Known limits to decide on later: an item with alternate versions, a burned-in subtitle and the Secondary Subtitles menu
   are not handled specially yet (see `docs/client-notes.md`).

## Test server (the test host, throwaway)

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
- Library `TestWebm` (`testdata/make_webm.sh`): VP9/Opus copies of episodes 1 and 2 with their English and Polish `.srt`,
  and a 30 s third movie with an embedded subtitle, a `.de.vtt` and an `.en.srt`. Playwright's Chromium cannot play H.264,
  so the browser tests use these.
- Browser tests run in a Playwright for Python image built on the test host (`sqe-playwright:1.64.1`), from a copy of
  `testdata/browser` there (`devtools/sync.sh` skips `testdata/`).

## Commands to resume (each is one line; run from the project folder)

Run `. devtools/env.sh` first (with the settings from `local.env`) so that `sqe_host` is defined.

- Start the test server: `sqe_host docker start jellyfin-test`
- Wait until it answers: `until curl -s -m 3 $JF_BASE/System/Info/Public | grep -qi '"version"'; do sleep 2; done`
- Sync the source to the test host: `devtools/sync.sh`
- Clean rebuild: `devtools/dotnet.sh build -c Release --no-incremental`
- Re-arm the foreign-group fixture: `devtools/prepare_mismatch.sh`
- Unit tests (all integration tests required): `DOCKER_FLAGS="-v $SQE_TEST_ROOT/src/mismatch:/mismatch -v $SQE_TEST_ROOT/media:/fixtures:ro -e SQE_MISMATCH_FILE=/mismatch/file.srt -e SQE_FIXTURE_DIR=/fixtures -e SQE_REQUIRE_INTEGRATION=1" devtools/dotnet.sh test -c Release --no-build`
- Install the plugin on the TEST server: `DOCKER_FLAGS="-v $SQE_TEST_ROOT/config/data/plugins:/plugins" devtools/dotnet.sh build src/Jellyfin.Plugin.SubtitleQuickEdit -c Release --no-incremental -o /plugins/SubtitleQuickEdit_0.2.0.0`
- Restart the test server to load it: `sqe_host docker restart jellyfin-test`
- API tests: `python3 -I testdata/stage2_api_tests.py <secrets-dir>`
- Mutation check (about 8 minutes): `python3 -I devtools/mutation_check.py` (needs `$SQE_TEST_ROOT/src/sqe-mut`, created with `sqe_host mkdir $SQE_TEST_ROOT/src/sqe-mut` and `sqe_host chown 1000:1000 $SQE_TEST_ROOT/src/sqe-mut`)
- Client tests (no server): `node --test src/Jellyfin.Plugin.SubtitleQuickEdit/Client/test/*.test.js`, then
  `node src/Jellyfin.Plugin.SubtitleQuickEdit/Client/test/mutation-check.js` and, with Playwright for Node,
  `node testdata/browser/fake_page_smoke.js`
- Browser tests on the TEST server: reset TestWebm 03 first and after (`sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/reset_webm03_srt.sh`),
  then run `stage3_tests.py` in the Playwright image with the testadmin and testuser passwords on stdin (see the file's header)
- Stop the test server again: `sqe_host docker stop jellyfin-test`

## Teardown when the project is finished (nothing below is done yet)

`docker rm -f jellyfin-test` on the test host; `rm -rf $SQE_TEST_ROOT` (config, media, source copy and the gid-2000 fixture);
`docker rmi` of the pinned Jellyfin image, of `mcr.microsoft.com/dotnet/sdk:10.0` and of the Playwright image; delete the test secrets folder. Nothing from this project exists on the live Jellyfin yet, so there is nothing to undo there.

## Gotchas learned

- A write that renames a new file over the gid-2000 fixture resets its group: run `devtools/prepare_mismatch.sh` before tests.
- `dotnet test` has no `--no-incremental`: use `dotnet build --no-incremental`, then `dotnet test --no-build`.
- Jellyfin drops an external `.srt` that ffprobe cannot read at scan time; a `FullRefresh` (not `ValidationOnly`) retries a file it
  rejected earlier. `ValidationOnly` is enough to pick up a newly added file.
- Jellyfin omits null JSON properties (a track without a language has no `language` key).
- `ApiClient.ajax` with `dataType: 'json'` rejects on an empty 204 reply (the session command): see `docs/client-notes.md`.
- `GET /Sessions` shows a subtitle change about 6 s late; the client follows the subtitle menu clicks meanwhile.
- The Read tool is blocked outside the working directory (`blockReadsOutsideWorkingDirectories`); the project now lives inside it.
- `STAGE0-NOTES.md`, `upstream-template/`, `template-bump/` and `upstream-template.tar.gz` in the project root are stage 0
  reference material and can be deleted when the project is finished.
