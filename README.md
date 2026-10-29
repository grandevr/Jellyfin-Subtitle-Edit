# Subtitle Editor for Jellyfin

Fix a wrong subtitle line while you watch. Subtitle Editor adds an edit button to the Jellyfin web player: pause on
the bad line, correct the text (or add a missing line with its timing), save, and the player shows the fix. The
subtitle file next to your video is changed in place, and the original is kept as a backup.

- Edits external `.srt`, `.vtt`, `.ass` and `.ssa` subtitles, byte for byte: only the lines you change are touched.
- Makes a subtitle track embedded in a video editable by writing it as a file next to the video. The video itself is
  never changed.
- Lets other users suggest fixes for an admin to approve, or lets chosen users edit directly.
- Keeps backups of every subtitle before its first edit, with a one-click **Revert to original**.

![Screenshot](docs/Screenshot_20261025_203955.png?raw=true)

## Requirements

- Jellyfin 12.3 or later.
- The [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin, 3.0.2.0 or
  later. Subtitle Editor uses it to add its script to the web player. Without it the plugin loads, but no button
  appears and the server log says why.
- Jellyfin running on Linux is what the plugin is tested on. Other platforms work, but file ownership is not kept.

## Install

1. In Jellyfin go to **Dashboard → Plugins → Repositories** (in some versions **Catalog → ⚙**) and add a repository:
   - Name: `Subtitle Editor`
   - URL: `https://raw.githubusercontent.com/grandevr/Jellyfin-Subtitle-Edit/main/manifest.json`
2. Open the **Catalog**, find **Subtitle Editor** under Subtitles and install it. Install File Transformation the same
   way if you do not have it yet (it has its own repository; see its README).
3. Restart Jellyfin.

Updates then show up in the plugin list like those of any other plugin, and Jellyfin installs them on restart.

To install by hand instead, download the zip from [Releases](https://github.com/grandevr/Jellyfin-Subtitle-Edit/releases),
unpack it into a folder named `SubtitleQuickEdit_<version>` in Jellyfin's `plugins` folder, and restart.

## Use

Play something with subtitles on in the web player. If you may edit, an edit button appears next to the subtitle
button. It pauses the video and opens a panel (a side panel on desktop, a sheet at the bottom on a phone):

- One text box per line on screen. **Previous** and **Next** step through the lines and seek to them.
- **Apply** (Ctrl+Enter) stages a change, **Save** (Ctrl+S) writes all staged changes at once, and **Reload subtitles**
  makes the player show the saved file without restarting playback.
- **+ Add line** adds a new line, with start and end times, "Set to now", ±100 ms nudges, a preview and an overlap
  warning.
- **Changes this session** lists every change with its state, Undo, and a click to jump to it.
- **Revert to original** puts back the file as it was before your first edit.
- Unsaved changes survive a page reload. Esc closes the panel; keys typed in it never reach the player.
- For ASS/SSA files, **Show style tags** shows the `{\...}` tags at the start of a line. When it is off, they are kept
  as they are on save. The player draws ASS itself, so a change shows after Save and Reload subtitles.

When the subtitle is a track inside the video, the button reads **Make this subtitle editable**: the plugin writes the
track as a file next to the video (in its own format for ASS/SSA, as `.srt` for other text tracks) and restarts
playback on it. Picture-based tracks (PGS, VobSub) cannot be edited.

With no subtitle on (or one that cannot be edited), the button is still there for users who may save directly, and
offers **Start new track**: choose a language for the file name, add lines with **+ Add line**, and the first Save
writes them to a new `.srt` file next to the video and restarts playback on it. Nothing is written before that first
Save. The panel also offers the item's subtitles that can be edited. Users who submit for review get the button only
on a subtitle they can work on.

If the file changed on disk since it was loaded (another user, a subtitle downloader), Save writes nothing and says
which changes no longer fit.

## Who may edit

| Who | What they get |
|---|---|
| Administrators | Edit and save, review submissions, change settings |
| Users let in by **Allow user edits** (all users, or the ones you tick) | Edit and save on the items they can see |
| Other users, when **Allow user submissions** is on | The same panel titled "Suggest a subtitle fix", with **Submit for review** and an optional note. The file is not changed until an admin approves |
| Everyone else | No button |

Admins see a link to waiting submissions at the top of the panel. Submissions are reviewed in the plugin's settings
page, where each change can be approved or left out, and a rejection can carry a reason that the user sees.

## Settings

**Dashboard → Plugins → Subtitle Editor.**

| Setting | Default | |
|---|---|---|
| Allow user edits | Off | Off, All users, or Selected users (with a checkbox per user) |
| Allow user submissions | Off | Other users may send edits for an admin to approve |
| Max pending submissions per user | 50 | |
| Backups | Original only | Or "Original + last N saves" |
| Keep last N saves | 5 | Used by "Original + last N saves" |

Below the settings are the **Submissions** waiting for review and the **Editable copies** made from embedded tracks,
each with Delete.

## Backups and data

Everything the plugin keeps lives in `data/SubtitleQuickEdit` under Jellyfin's data folder
(`/config/data/SubtitleQuickEdit` in the official Docker images), outside the plugin's own folder, so it survives
updates, a reinstall and a rollback:

- `backups/`: the original of every subtitle from before its first edit (never removed), restore points, and, if you
  choose, the last N saves. A file that an extraction replaced or an editable copy that was deleted is kept here too.
- `submissions/`: one JSON file per submission. Pending ones are never removed; the newest 50 decided ones are kept.
- `extracted/`: a record of each editable copy made from an embedded track.

Uninstalling the plugin leaves this folder and every edited subtitle file in place.

## How it works

The browser sends an item id and a subtitle stream index, never a path; the server finds the file through Jellyfin's
library. A save checks that the file still has the content the editor loaded (SHA-256), backs it up, splices only the
changed bytes into it (so encoding, BOM, line endings and everything else stay as they were), parses the result again
to check it, and writes it through a temp file that is verified before it replaces the original. Files whose encoding
cannot be detected for sure are shown as not editable rather than guessed at.

More detail:

- [docs/api.md](docs/api.md): the server API, permissions and error codes.
- [docs/internals.md](docs/internals.md): parsing, encodings, the safe file writer, platforms, analyzers.
- [docs/client-notes.md](docs/client-notes.md) and [docs/server-notes.md](docs/server-notes.md): what jellyfin-web and
  Jellyfin were found to do, which the design depends on.

## Building

Needs the .NET 10 SDK.

```sh
dotnet build SubtitleQuickEdit.slnx -c Release
dotnet test SubtitleQuickEdit.slnx -c Release
node --test src/Jellyfin.Plugin.SubtitleQuickEdit/Client/test/*.test.js
```

The plugin is `src/Jellyfin.Plugin.SubtitleQuickEdit/bin/Release/net10.0/Jellyfin.Plugin.SubtitleQuickEdit.dll` plus
`SubtitleQuickEdit.Core.dll` next to it. Two browser checks run against a fake web page and need only
[Playwright](https://playwright.dev) for Node: `node tests/browser/fake_page_smoke.js` and
`node tests/browser/config_page_smoke.js`.

| Path | What |
|---|---|
| `src/SubtitleQuickEdit.Core` | Jellyfin-free library: the subtitle model, the SRT, VTT and ASS parsers and editors, the save rules, the safe file writer, backups, submissions, extraction |
| `src/Jellyfin.Plugin.SubtitleQuickEdit` | The plugin: API controller, settings page, track resolver, File Transformation registration |
| `src/Jellyfin.Plugin.SubtitleQuickEdit/Client` | The web player script and styles, with Node tests in `test/` |
| `tests/` | xUnit tests of the core, and the two browser checks |

The `dev` branch also holds the tooling for a throwaway Jellyfin test server: fixtures, API tests, browser tests
against a real server, and mutation checks. See its README.

## Licence

GPL-3.0, see [LICENSE](LICENSE).
