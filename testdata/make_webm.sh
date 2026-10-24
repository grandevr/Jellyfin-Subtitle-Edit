#!/bin/sh
# Run inside the test Jellyfin container as uid 1000, after make_videos.sh and make_testdata.py:
#   . devtools/env.sh; sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/make_webm.sh
# VP9/Opus copies of the two test videos for browser tests (Playwright's Chromium cannot decode H.264), with copies of
# their English and Polish .srt files. They live in their own folder, the TestWebm movies library, so the API tests'
# episode list is unchanged.
set -e
S="/data/TestShow/Season 01"
D="/data/TestWebm"
FF=/usr/lib/jellyfin-ffmpeg/ffmpeg
mkdir -p "$D"
for n in 01 02; do
  "$FF" -y -loglevel error -i "$S/TestShow - S01E$n.mp4" \
    -c:v libvpx-vp9 -b:v 0 -crf 40 -deadline realtime -cpu-used 8 -c:a libopus -b:a 64k \
    "$D/TestWebm $n.webm"
  for lang in en pl; do
    if [ -f "$S/TestShow - S01E$n.$lang.srt" ]; then
      cp "$S/TestShow - S01E$n.$lang.srt" "$D/TestWebm $n.$lang.srt"
    fi
  done
done
ls -l "$D"
