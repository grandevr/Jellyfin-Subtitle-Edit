# Notes on Jellyfin server behaviour

What Jellyfin 12.2 was found to do (read from the source at tag v12.2 and checked on a throwaway test server,
2026-10-16). The plugin's design rests on these; re-check them when Jellyfin changes.

## Build
- Runtime: net10.0. Jellyfin.Controller / Jellyfin.Model come from NuGet.
- The official plugin template (2026-09) targeted net9.0 / Jellyfin 10.11.5; this project uses a copy bumped to
  net10.0 and the Jellyfin 12.x packages. It builds with 0 warnings and `TreatWarningsAsErrors` on, with no analyzer
  rule relaxed.

## File Transformation
- 3.0.2.0, asset Release-12.2.0.zip (manifest lists targetAbi 12.2.0.0). Zip = DLL + deps.json + logo + pdb, no meta.json.
- Installed in /config/data/plugins/FileTransformation_3.0.2.0/. Log: "Loaded plugin: File Transformation 3.0.2.0",
  "[FileTransformation] Installing response transformation middleware".
- Cannot be referenced as a library (separate load context); register transformations by reflection.

## Jellyfin 12.2 behaviour (source at tag v12.2 + tested on a test server)
- External .srt that the parser supports is read from disk on every request (SubtitleEncoder.GetReadableFile returns
  the file path; no cache copy). The cache with size:mtime .meta files only covers extracted/converted tracks.
  Tested: edited a file on disk, next Stream.vtt fetch showed the new text, no refresh, no restart.
- Responses carry no Cache-Control, ETag or Last-Modified.
- jellyfin-web loads text subtitles with fetch(<...>/Stream.js) (JSON TrackEvents) and adds a manual text track.
- Encoding: SubtitleEncoder uses UtfUnknown (UTF.Unknown 2.7.0, CharsetDetector), NOT Ude (Ude.NetStandard 1.2.0 also ships,
  not used by SubtitleEncoder). Non-UTF-8 files are decoded with the detected encoding and re-served as UTF-8.
  Tested OK: Windows-1250 (pl, cs), UTF-16LE with BOM, UTF-8 with BOM, malformed file (served as plain cues).
- Refresh: external subtitle files are (re)discovered only during a refresh (FFProbeVideoInfo.AddExternalSubtitlesAsync).
  A ValidationOnly refresh (POST /Items/{id}/Refresh?MetadataRefreshMode=ValidationOnly&ImageRefreshMode=None&
  ReplaceAllMetadata=false&ReplaceAllImages=false) picked up a newly added .srt (new stream index). Not needed for content
  edits. Default/FullRefresh can also trigger subtitle downloading when the library has download languages set.
- Library file watcher did not notice a new file within 20 s.
- External subtitle streams get indexes 0..n-1 BEFORE the video/audio streams.
- Web folder /usr/share/jellyfin/web is root-owned; Jellyfin runs as abc, so patching index.html is impossible.

## Gotchas
- Jellyfin 12 ignores the X-Emby-Authorization header; use Authorization: MediaBrowser ... Token="...".
- A temp-file + rename write (what sed -i does) gives the new file the process's primary group, not the original's. When the
  Jellyfin user is not a member of the file's group it cannot change the group back. The owner is unaffected.
