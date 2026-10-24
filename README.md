# Subtitle Quick Edit

A Jellyfin 12.2 plugin that edits the text of the subtitle line on screen (or adds a new line) in external `.srt`
files. Spec: the "Jellyfin Subtitle Quick-Edit Plugin — Spec" doc. Licence: GPL-3.0 (see `LICENSE`).

Status: **stage 2 done**: the SRT core (stage 1), durability and platform handling, and the plugin with its server
API, settings page and backups. The client script (button, panel) is stage 3; submissions and review are stage 4.

## Layout

| Path | What |
|---|---|
| `src/SubtitleQuickEdit.Core` | Jellyfin-free library: `Srt/` (parse, edit), `Storage/` (safe file write, backups, platform services), `Editing/` (the save rules) |
| `src/Jellyfin.Plugin.SubtitleQuickEdit` | The plugin: controller, settings page, item/stream resolver, startup logging |
| `tests/SubtitleQuickEdit.Core.Tests` | xUnit tests of the core |
| `testdata/` | Scripts for the throwaway Jellyfin test server: fixtures, users, `stage2_api_tests.py` |
| `devtools/` | `sync.sh`, `dotnet.sh` (build/test in the SDK container on the test host), `prepare_mismatch.sh`, `mutation_check.py` |
| `STAGE0-NOTES.md` | What Jellyfin 12.2 actually does (cache, encoding, refresh) |

## Building, testing, installing on the test server

The scripts in `devtools/` and `testdata/` drive a throwaway Jellyfin test server and nothing else. They read their
settings from the environment (see `devtools/env.sh`; there are no defaults, a missing variable stops the script):

| Variable | Meaning | Example |
|---|---|---|
| `SQE_HOST_EXEC` | Command prefix that runs a command string on the test host; it gets the whole quoted command as one last argument (stdin is passed through) | `ssh myhost`, `ssh myhost pct exec 103 --`, or `sh -c` for a local test server |
| `SQE_TEST_ROOT` | Test server folder on that host, holding `config/`, `media/` and `src/` | `/srv/jellyfin-test` |
| `JF_BASE` | Test server URL, used by `testdata/*.py` | `http://test-host:8097` |
| `SQE_LOG_CMD` | Optional: command that prints the test container's last 2000 log lines to stdout, for `stage2_api_tests.py` (default: `docker logs --tail 2000 $SQE_CONTAINER` through `SQE_HOST_EXEC`). Split like a shell line, no pipes | `my-log-tool jellyfin-test 2000` |
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
  src/Jellyfin.Plugin.SubtitleQuickEdit -c Release -o /plugins/SubtitleQuickEdit_0.2.0.0`) and restart the container.
  Jellyfin writes its own `meta.json`. `UtfUnknown.dll` is not shipped: the plugin uses the copy Jellyfin already has.
- API tests: `python3 -I testdata/stage2_api_tests.py <secrets-dir>` resets the fixtures and runs 75 checks against the
  test server (see below).
- `devtools/mutation_check.py` breaks the code in scratch copies (17 ways) and requires the unit tests to fail for each break. Last run: control passed, 17 of 17 killed.

## Analyzers

`TreatWarningsAsErrors` is on everywhere. Core and the plugin use `AnalysisMode=AllEnabledByDefault`; the plugin
also carries the template's StyleCop, Serilog and multithreading analyzers. **No rule is relaxed or suppressed** in
either. The only project settings beyond the template are `AllowUnsafeBlocks` in Core (the `LibraryImport` P/Invoke
stubs for `statx`, `chown`, `open`, `fsync`) and the `ExcludeAssets=runtime` on the Jellyfin packages that the template
already has. The bump of the template to net10.0, Jellyfin 12.2.0 and targetAbi 12.2.0.0 needed no rule changes.

The test project uses the default analyzer set (not `AllEnabledByDefault`), since rules such as test-name underscores
and per-call-site string allocations do not suit test code, and carries
`[assembly: SupportedOSPlatform("linux")]` (the writer tests use Unix file modes, hard links and libc).

## Server API

All routes are under `/SubtitleQuickEdit`. The client sends an item id and a subtitle stream index, never a path: the
file comes from Jellyfin's own library data (`IMediaSourceManager.GetMediaStreams`). JSON property names are camelCase.
Every error answer has the shape `{ "code", "message", "cueIndex"?, "currentHash"? }`.

| Method and route | Who | Answers |
|---|---|---|
| `GET Items/{itemId}/Subtitles` | admins; other users only when submissions are on and the item is visible to them (library access included) | the item's subtitle streams: `streamIndex`, `language`, `title`, `external`, `format`, `editable`, `reason` |
| `GET Items/{itemId}/Subtitles/{streamIndex}/Cues` | same | `hash` (SHA-256 of the file), `encoding`, and the cues (`index` = 1-based position, `start`, `end` in ms, `text` with `\n`) |
| `POST Items/{itemId}/Subtitles/{streamIndex}/Edits` | admins only (403 for everyone else) | `hash`, `savedCueIndexes`, `addedCues` (client id to position), `writeMethod` |

Edits body: `{ "baseHash", "edits": [ {"type":"edit","cueIndex","oldText","newText"} | {"type":"add","clientId","startMs","endMs","text"} ] }`.

Status codes: 400 `InvalidRequest` / `InvalidEdit` / `CueNotFound` / `NotEditable` (embedded, not .srt, not local); 403
`Forbidden`; 404 `ItemNotFound` / `StreamNotFound` (a non-admin cannot probe for items: without access, or with
submissions off, they get 403 or 404 as appropriate and never a hint of what exists); 409 `FileChanged` (with
`currentHash`) / `TextMismatch` (with `cueIndex`); 413 `RequestTooLarge` (over 1 MB); 422 `Unrepresentable` /
`NotEditable` (file not parseable); 500 `NotWritable` (names the folder and the user) / `WriteFailed` /
`VerificationFailed` / `LeftoverUnrecoverable`. A refused request never changes the file, makes a backup or leaves a
temp file.

Save rules in order (`Core/Editing/SubtitleSaveService`): the stream must be an external local `.srt` of that item; the
file must parse; its hash must equal `baseHash`; every edit must be valid and its `oldText` must match; the original is
backed up; the new content is written by the hybrid writer. Jellyfin reads external `.srt` files on every request, so
nothing is refreshed or invalidated afterwards.

## Settings

| Setting | Default | |
|---|---|---|
| Allow user submissions | Off | Stage 4 uses it; from stage 2 it already decides whether non-admins may read cues |
| Max pending submissions per user | 50 | Stage 4 |
| Backups | Original only | or "Original + last N saves" |
| N (`BackupKeepLastSaves`) | 5 | Added: the spec's "last N" needs a number |

## Backups

`data/SubtitleQuickEdit/backups` under Jellyfin's program-data folder (`/config/data/SubtitleQuickEdit/backups` in the
Docker image; `IApplicationPaths.ProgramDataPath`). It is outside the versioned plugin folder, so backups survive a
plugin update and a rollback (tested: delete the plugin folder, restart, backups remain, reinstall). Files are named
`{itemId}_{streamIndex}_{pathId}_{utc time}_original.srt` (content from before the first save of that subtitle;
never overwritten or removed) and, in "Original + last N saves" mode, `…_save.srt` for the content from before each
later save, pruned to the newest N. A backup that cannot be written stops the save.

## Behaviour worth knowing

**Parsing** is lenient (missing blank lines, missing number lines, stray spaces, `.` or `,` before milliseconds, CR, LF
or CRLF). A file is *not editable*, with a reason, when it is empty, over 8 MB, has no detectable or an unsupported
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
