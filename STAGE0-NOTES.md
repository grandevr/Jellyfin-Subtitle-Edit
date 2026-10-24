# Stage 0 findings (2026-10-16)

Test server: container `jellyfin-test` on CT 103, `/opt/jellyfin-test`, http://192.168.0.21:8097,
image pinned to the live digest sha256:799509f8... (Jellyfin 12.2.0, linuxserver 12.2ubu2604-ls54).

## Build
- Runtime: net10.0 (10.0.12). SDK image: mcr.microsoft.com/dotnet/sdk:10.0.
- NuGet: Jellyfin.Controller / Jellyfin.Model 12.2.0 exist.
- Official template (master, 2026-09-18) is net9.0 / Jellyfin 10.11.5 / targetAbi 10.11.0.0.
  Our copy: TargetFramework net10.0, Jellyfin.Controller + Model 12.2.0, targetAbi 12.2.0.0, framework net10.0.
- Builds with 0 warnings, 0 errors, TreatWarningsAsErrors on. No analyzer rule had to be relaxed.
- The built template DLL loads on 12.2 ("Loaded plugin: Template 0.0.0.0").
- Build command (as uid 1000, nuget cache inside the source folder):
  docker run --rm --user 1000:1000 -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1
  -e NUGET_PACKAGES=/src/.nuget -v /opt/jellyfin-test/src/<proj>:/src -w /src
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet build -c Release

## File Transformation
- 3.0.2.0, asset Release-12.2.0.zip (manifest lists targetAbi 12.2.0.0). Zip = DLL + deps.json + logo + pdb, no meta.json.
- Installed in /config/data/plugins/FileTransformation_3.0.2.0/. Log: "Loaded plugin: File Transformation 3.0.2.0",
  "[FileTransformation] Installing response transformation middleware".
- Cannot be referenced as a library (separate load context); register transformations by reflection.

## Jellyfin 12.2 behaviour (source at tag v12.2 + tested on the test server)
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
- A temp-file + rename write (what sed -i does) gives the new file the process's primary group (104), not the original's (1000).
  The Jellyfin user is in groups 104 and 100 only, so it cannot chgrp back to 1000. Owner (uid 1000) is unaffected.
