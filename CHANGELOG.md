# Changelog

Jellyfin shows versions with four numbers; the plugin's folder is named after it (`SubtitleQuickEdit_1.2.2.0`). The
Release workflow copies the section of the version it releases (rename "Unreleased" to the version when bumping it) into `manifest.json`, so Jellyfin shows it as the
version's changelog.

## 1.3.0.0

- Change the times of an existing line: the pencil next to a line's times opens the Add line form for that line. Works
  for SRT, WebVTT and ASS/SSA; only the two times change, everything else on the line stays. Users who submit changes
  for review can suggest new times too.
- Fixed: with "Original + last N saves", many saves of one subtitle within the same millisecond, or after the server
  clock went back, could prune the newest backup instead of an older one.

## 1.2.2.0

- The panel says plainly that the player draws ASS subtitles itself, so a change shows after Save and Reload subtitles.

## 1.2.1.0

- Subtitle files up to 32 MB can be edited (was 8 MB). ASS tracks with a lot of typesetting are often over 8 MB.
- Parts of the panel that should be hidden stay hidden.

## 1.2.0.0

- Make an embedded text subtitle track editable: the plugin writes it as a file next to the video, in its own format
  for ASS/SSA and as SubRip for other text codecs, and playback restarts on it. The video is never changed.
- Admins can replace an existing file after a side-by-side comparison; the replaced file goes to the backups.
- The settings page lists the editable copies, with Delete.

## 1.1.0.0

- Edit WebVTT (`.vtt`) and ASS/SSA (`.ass`, `.ssa`) files as well as `.srt`.
- "Show style tags" toggle for ASS and VTT, off by default.
- Works with the player's own ASS renderer.
- Renamed to "Subtitle Editor".

## 1.0 (built as 0.2.0.0)

- Edit and add `.srt` lines from the web player, with a changes list, Save and Reload subtitles.
- Backups of the original, Revert to original, Undo of saved changes.
- User submissions for admin review, and "Allow user edits" for chosen users.
