# Client notes: what jellyfin-web 12.2 really looks like

Recorded in stage 3a (2026-10-24) against Jellyfin 12.2.0 and the web client it ships, with headless Chromium driven by
Playwright 1.64.1 on a throwaway test server, logged in as an administrator through the login form. The item played was a
60 s WebM (VP9 video, Opus audio) by direct play, with two external `.srt` tracks (English index 0, Polish index 1). The
layout was desktop (`html.layout-desktop`, viewport 1280x720). Later sections add what stages 3d to 3g found.

Everything below was observed, not read from the jellyfin-web source. Selectors can change between releases, so the client
keeps them in one constants block and falls back to a floating button.

## How the script reaches the page

- File Transformation matches `fileNamePattern` against the request path **after `/web/`**, with the leading `/` removed.
  `/web/` and `/web` count as `index.html`. An exact string match is tried first; otherwise the pattern is a .NET regular
  expression applied with `IsMatch` (not anchored, 2 s timeout).
- Only **one pipeline runs per request**: the exact-match key if there is one, else the first regex key that matches.
  Transformations registered under the *same key string* run in registration order. So load order only matters between
  plugins that use the same key; a plugin that registers `^index\.html$` and one that registers `index.html` do not share
  a pipeline, and the exact `index.html` key wins. This plugin registers the plain key `index.html`.
- A registration is kept in memory only: it is lost on restart, so the plugin registers again on every start. A second
  registration with the same id is ignored. The callback is called with an object whose `Contents` property is the page
  text and must return the new text.
- `index.html` is served with `Cache-Control: no-cache` and an ETag. There is **no Content-Security-Policy** header and no
  CSP `<meta>` tag, so an external script and stylesheet from the same origin load without any change to policy. After
  transformation File Transformation replaces the ETag, adds `Vary: Accept-Encoding` and drops `Last-Modified`.
- The two tags end up after `<div id="reactRoot">`, at the end of `<body>`: one `<link rel="stylesheet">` and one
  `<script>`, both carrying `data-subtitle-quick-edit`, with `../SubtitleQuickEdit/Client/quickedit.(js|css)?v=<version>`.
  Both requests return 200 and the console shows our line first, before any jellyfin-web line.

## Load order (matters for the script)

jellyfin-web loads all of its bundles as `defer` scripts in `<head>`; our script is a plain script at the end of `<body>`,
so it runs **before** them.

| Moment | `document.readyState` | `window.ApiClient` |
|---|---|---|
| our script executes | `loading` | `undefined` |
| `DOMContentLoaded` | `interactive` | `undefined` |
| `load` | `complete` | `undefined` |
| about 180 ms after our script | `complete` | defined, `isLoggedIn()` is `false` |

So the script must wait for `window.ApiClient` (poll or observe), then for a logged-in user, and must read
`window.ApiClient` fresh on every use: the instance is created by `getOrAddApiClient` and can be replaced.

## `window.ApiClient`

- A class instance (minified constructor name `e`) with about 240 methods. The ones we need exist as functions:
  `getCurrentUser()`, `getCurrentUserId()`, `accessToken()`, `deviceId()`, `deviceName()`, `serverAddress()`, `serverId()`,
  `getUrl(name, params)`, `getJSON(url)`, `ajax(options)`, `fetch(options)`, `getSessions()`, `sendCommand()`,
  `getItem()`, `getPlaybackInfo()`, `isLoggedIn()`, plus `setRequestHeaders`.
- `getCurrentUser()` returns a promise for the user object: keys `Configuration, EnableAutoLogin, HasConfiguredEasyPassword,
  HasConfiguredPassword, HasPassword, Id, LastActivityDate, LastLoginDate, Name, Policy, ServerId`. `Policy.IsAdministrator`
  is the admin flag (42 policy keys).
- `accessToken()` returns the token as a 32-character string. `deviceId()` returns a string (a base64 text built from the
  user agent and a timestamp, stable for the browser profile).
- `getUrl('Sessions', {DeviceId: x})` returns an **absolute** URL including the origin: `<origin>/Sessions?DeviceId=x`.
  `getUrl('SubtitleQuickEdit/Items/<id>/Subtitles')` gives `<origin>/SubtitleQuickEdit/Items/<id>/Subtitles`, so plugin
  routes need no special handling. `getJSON(getUrl(...))` sends the user's token and returns parsed JSON.
- `isWebSocketOpen()`, `isWebSocketOpenOrConnecting()` and `isMessageChannelOpen()` are all `false` even while the page's
  own socket is open (see WebSocket below): do not use them.
- **Never log URLs of media requests**: the video URL carries the token as a query parameter (`ApiKey`).

## Video page

- The player route is `#/video`; the details page is `#/details?id=<itemId>` with `button.btnPlay[data-action=resume|play]`.
- The video is `div.videoPlayerContainer.videoPlayerContainer-onTop > video.htmlvideoplayer`, a **direct child of
  `<body>`**, not inside the OSD view. Attributes: `class, preload, autoplay, webkit-playsinline, playsinline, poster,
  crossorigin, src`. Direct play of a WebM gives `src` = `/Videos/<id>/stream.webm?Static=true&mediaSourceId=…&deviceId=…&ApiKey=…&Tag=…`.
  `readyState` is 4 and `currentTime` is the precise playback time.
- `<html>` has `layout-desktop transparentDocument`; `<body>` has `libraryDocument hide-scroll` while playing.
- A **new `<video>` and a new `.videoPlayerContainer` are created for every playback** and both are removed when the
  player is left (back button). Mark a video element yourself and it will not survive to the next playback.
- Subtitles: the page keeps **one native text track**, `kind=subtitles`, `label=manualTrack`, `language=und`, and puts the
  chosen external track's cues into it (12 cues for the test file). There is no separate subtitle element in the DOM; the
  browser draws the cue text. Choosing *Off* sets that track's `mode` to `disabled` (`cues` becomes `null`); choosing a
  track sets `mode=showing` and loads its cues again. Text subtitles are fetched as JSON track events (the console says
  `downloaded N track events`).

## OSD structure (desktop)

```
body
├─ div.videoPlayerContainer > video.htmlvideoplayer
├─ div.skinHeader.osdHeader                        legacy header block (opacity 0 + class osdHeader-hidden when hidden)
├─ div.skinHeader.osdHeader.MuiBox-root            React header: back button, title; SyncPlay and Cast buttons on the right
└─ …                                               (the OSD view, added a moment after the video)
   div.videoOsdBottom.videoOsdBottom-maincontrols  bottom block, display:flex, pointer-events:none, y 497..720 at 720p
   └─ div.osdControls                              pointer-events:all, y 609..694
      ├─ div.osdTextContainer.osdMainTextContainer > h3.osdTitle
      ├─ div (position row): div.osdPositionText, input.osdPositionSlider, div.osdDurationText
      └─ div.buttons.focuscontainer-x               the bottom button row
```

The bottom button row, left to right with x positions at 1280 px width (each button is 42x41):
`btnRewind` 16, `btnPause` 70, `btnFastForward` 125 (these three sit in a nested `div`), then `div.osdTimeText` ("Ends at …"),
then on the right `btnUserRating` 818, **`btnSubtitles` 869**, `div.volumeButtons` with `buttonMute` 923 and
`input.osdVolumeSlider`, `btnVideoOsdSettings` 1122, `btnPip` 1172, `btnFullscreen` 1222. Every button is
`button.<name>.autoSize.paper-icon-button-light` with a `title` ("Subtitles", "Settings", …).

The subtitle button is:

```html
<button is="paper-icon-button-light" class="btnSubtitles autoSize paper-icon-button-light" title="Subtitles">
  <span class="xlargePaperIconButton material-icons closed_caption" aria-hidden="true"></span>
</button>
```

The glyph comes from the **class name** on the span (`closed_caption`), not from text, so `textContent` is empty. It is added
with the extra class `hide` and stays hidden until the item is known to have subtitle tracks. A button of our own can copy
this markup with a different icon class.

## How the OSD shows and hides

- Shown: `<body>` has no `mouseIdle` class; `.videoOsdBottom` is `display:flex` and `.osdHeader` is visible.
- Hidden: **about 3.0 s** after the last mouse movement `<body>` gets the class `mouseIdle`, `.videoOsdBottom` gets
  `videoOsdBottom-hidden hide` (`display:none`, opacity 0), the legacy header gets `osdHeader-hidden` and the React header
  gets `visibility:hidden`. Nothing is removed from the DOM, and all rects of OSD children become 0x0.
- Shown again by mouse movement (checked: both classes are removed immediately). A **key press does not show it again**
  (checked with Shift: `mouseIdle` stayed).
- Nothing in the DOM says "OSD visible" except those classes, so use a MutationObserver on `class` of `body` and
  `.videoOsdBottom`, or test `document.body.classList.contains('mouseIdle')`.

## OSD lifecycle

| Event | What happens to the DOM |
|---|---|
| on the home page | no `video`, no `.osdControls`, no `button.btnSubtitles` |
| playback starts (`#/video`) | at once: `.videoPlayerContainer` and `video`; about 300 ms later the OSD view: `.videoOsdBottom`, `.osdControls`, `button.btnSubtitles.hide` |
| leaving the player | `video` and `.videoPlayerContainer` are removed; the **OSD view stays in the DOM, hidden** (one `.osdControls`, one `btnSubtitles`, button not visible) |
| next playback | a new OSD view is added and the old one removed within the same millisecond; the count of `.osdControls` stays 1; the new `btnSubtitles` starts with `hide` again |

So an injected button is destroyed with its OSD view on every playback, and a MutationObserver has to add it again each
time, check that only one copy exists, and wait for `btnSubtitles` to lose `hide` (or look at the track count) before showing it.

## Subtitle menu

Clicking `button.btnSubtitles` opens `div.dialogContainer` (a child of `<body>`) holding the action sheet:

- Items are `button.listItem.listItem-button.actionSheetMenuItem.emby-button`, with `data-id` = the **stream index**:
  `secondarysubtitle` ("Secondary Subtitles", new in this version), `-1` ("Off"), then one per track with its stream index
  (`0` "English - SUBRIP - External", `1` "Polish - SUBRIP - External").
- Each item starts with `span.actionsheetMenuItemIcon.material-icons.check`. The **selected** item's check has no inline
  style; unselected items have `style="visibility:hidden;"`.
- Other `li.MuiMenuItem-root` entries found in the DOM at the same time belong to the SyncPlay and Cast menus, not to this sheet.

## `GET /Sessions` for a playing item

`ApiClient.getJSON(ApiClient.getUrl('Sessions', {DeviceId: ApiClient.deviceId()}))` returns an array with one session
for this browser. Session keys: `AdditionalUsers, ApplicationVersion, Capabilities, Client, DeviceId, DeviceName,
HasCustomDeviceName, Id, IsActive, LastActivityDate, LastPlaybackCheckIn, NowPlayingItem, NowPlayingQueue, PlayState,
PlayableMediaTypes, PlaylistItemId, RemoteEndPoint, ServerId, SupportedCommands, SupportsMediaControl,
SupportsRemoteControl, UserId, UserName`.

`PlayState` keys: `AudioStreamIndex, CanSeek, IsMuted, IsPaused, MediaSourceId, PlayMethod, PlaybackOrder, PositionTicks,
RepeatMode, SubtitleStreamIndex, VolumeLevel`. Observed for the WebM movie: `PlayMethod=DirectPlay`,
`SubtitleStreamIndex=0` (English, the default), `AudioStreamIndex=3` (external subtitle streams take indexes 0 and 1, the
video is 2), `MediaSourceId` = the item id, `NowPlayingItem.Id` = the item id, `NowPlayingItem.Type=Movie`.

**It lags.**

- `SubtitleStreamIndex` took about **6 s** to show a change made in the subtitle menu (checked once; still 0 after
  2.5 s, and 4 s, then 1 at 6 s). `Off` is not reflected in the first seconds either.
- `PositionTicks` trails `video.currentTime` by up to about a second and is reported in steps. The page's `<video>`
  `currentTime` is the right clock for cue lookup, as the spec says.
- Because of that, "what track is on screen right now" cannot come from a Sessions call made at the moment of a click.
  The reliable signals in the page are the clicks on `.actionSheetMenuItem[data-id]` and the single text track's `mode`
  and cue count.

## WebSocket

- On the login page jellyfin-web opens `/socket` without a token; the server answers 403 ("Token is required") and the
  console shows `WebSocket connection to '…/socket' failed … 403`. That error is normal and appears before every login.
- After login a second socket, `/socket?ApiKey=…`, opens and receives `ForceKeepAlive`, `KeepAlive` and `UserDataChanged`.
  Messages sent by the server to a session (for example a `SetSubtitleStreamIndex` command) travel over it.

## Differences from the spec that stage 3b onwards has to design for

1. The script runs before `window.ApiClient` exists (it appears about 180 ms later, not logged in yet): wait for it.
2. The Sessions lookup is not authoritative right after a track change (about 6 s lag): follow the menu clicks as well.
3. The OSD and its buttons are created again for every playback; the old OSD stays hidden in the DOM.
4. The OSD hides after about 3 s without mouse movement, and keys do not show it; clicking only works while it is shown.
5. There is one text track named `manualTrack` whose cues are replaced when the track changes; the subtitle element is the
   browser's own rendering, so "replace the cues in the active text track" (reload option c) acts on that one track.
6. The subtitle menu has a *Secondary Subtitles* entry the spec does not mention. The client follows the primary track
   only: a secondary choice is ignored, and the panel always edits the primary track.
7. `ApiClient.isWebSocketOpen()` is unusable; the page's own socket is the live one.
8. File Transformation runs one pipeline per request, so the plugin's choice of the plain key `index.html` matters when other
   plugins also transform the index page.
9. Not yet checked: playing an item with alternate versions (the spec hides the button when `MediaSourceId` differs from the
   item id), a burned-in subtitle, and a client other than Chromium (stage 3g's hand test covers a desktop browser).

## Reload subtitles (stage 3d spike, used from 3e)

`testdata/browser/reload_spike.py` changed one line on disk and tried three ways to make the paused player show it. All
three updated the on-screen text and kept the position and the pause:

- A: `POST /Sessions/{id}/Command` with `{"Name":"SetSubtitleStreamIndex","Arguments":{"Index":"-1"}}`, 1.5 s later the
  track's index again. jellyfin-web's `serverNotifications` hands it to `playbackManager.setSubtitleStreamIndex`, which
  fetches the external track again.
- B: the OSD subtitle menu, Off and then the track.
- C: replacing the cues of `manualTrack` with the plugin's Cues (`new VTTCue(start/1000, end/1000, text)`).

The client uses A, because it goes through jellyfin-web's own loading (subtitle offset, styling). If the shown cues don't
match the file within 5 s it falls back to C. jellyfin-web prefixes each cue's text with a direction mark (U+200E), so
comparisons strip those.

## `ApiClient.ajax` and empty replies

`ApiClient.ajax({ dataType: 'json' })` parses the reply whenever `dataType` is `'json'`, and an empty reply then
rejects. `POST /Sessions/{id}/Command` answers 204 with no body. The first 3e build asked for JSON there, so the Off
command was carried out but treated as failed, and the On command was never sent: Reload left subtitles off. The
client's `apiPost` takes `noReply` for such calls. It also rejects (with the `Response`) on any status of 400 or more.

## Phone width (stage 3g)

Checked in headless Chromium at 390x844 with touch (`stage3_tests.py`, `admin_phone`): the button shows in the OSD row,
the panel is a full-width bottom sheet of at most 60 % of the height, so the video stays visible above it. Every panel
button is at least 44 px high, the time fields of the new-line form fit on one row each, and nothing scrolls sideways.
Taps work like clicks. The 44 px rule applies under 600 px or on a coarse pointer (touch screens of any size).

