# How it works

Details for people reading or changing the code. For what the plugin does, see the [README](../README.md).

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

**Editing** works on bytes: only the edited cue's text bytes (and times, when they change), the added blocks and (when
something is added) the number lines change. Every other byte, including BOM, line endings, spacing and the file's ending style, is kept.
Loading and saving with no edits, or replacing a cue with its own text, is byte-identical.
- Cues are addressed by their 1-based position, not their number line.
- New text uses the file's dominant line ending. Text must not be empty, contain a blank (or whitespace-only) line,
  contain a line that looks like a time line, or contain control characters.
- An added cue goes before the first existing cue that starts later (after existing cues with the same start); in a file
  with out-of-order cues that is a deterministic rule, not a sort. Adding renumbers every cue 1..n, also writing a number
  line for cues that had none. The file's ending style (0, 1 or 2 final line breaks) is kept.
- Added cues: start at least 0, end after start, at least 100 ms long, at most 99:59:59,999, and not after the item's
  runtime when it is given.
- New times for an existing cue (1.3) follow the same rules. Only the two times on its time line are rewritten (in the
  canonical `hh:mm:ss,mmm` form; SRT coordinates and WebVTT cue settings after them stay; for ASS only the Start and End
  fields, rounded to centiseconds). The cue is not moved, even when its new start is after the next cue's: positions
  stay what the client knows, and players show lines by their times, not their order in the file.
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

## Analyzers

`TreatWarningsAsErrors` is on everywhere. Core and the plugin use `AnalysisMode=AllEnabledByDefault`; the plugin
also carries the template's StyleCop, Serilog and multithreading analyzers. **No rule is relaxed or suppressed** in
either. The only project settings beyond the template are `AllowUnsafeBlocks` in Core (the `LibraryImport` P/Invoke
stubs for `statx`, `chown`, `open`, `fsync`) and the `ExcludeAssets=runtime` on the Jellyfin packages that the template
already has. Bumping the official template to net10.0 and the Jellyfin 12.x packages needed no rule changes. The plugin builds
against Jellyfin 12.3.0.

The test project uses the default analyzer set (not `AllEnabledByDefault`), since rules such as test-name underscores
and per-call-site string allocations do not suit test code, and carries
`[assembly: SupportedOSPlatform("linux")]` (the writer tests use Unix file modes, hard links and libc).

