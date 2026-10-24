#!/bin/sh
# Run inside the test Jellyfin container as uid 1000, after make_videos.sh and make_testdata.py:
#   . devtools/env.sh; sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/make_webm.sh
# VP9/Opus copies of the two test videos for browser tests (Playwright's Chromium cannot decode H.264), with copies of
# their English and Polish .srt files, and a third movie with tracks that are not editable. They live in their own
# folder, the TestWebm movies library, so the API tests' episode list is unchanged.
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
# Movie 3: the tracks the edit button must stay hidden for (an embedded subtitle and an external .vtt), plus one
# editable external .srt to switch to.
printf 'WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nEmbedded line\n' > /tmp/sqe-embedded.vtt
"$FF" -y -loglevel error -i "$D/TestWebm 01.webm" -i /tmp/sqe-embedded.vtt -t 30 \
  -map 0:v -map 0:a -map 1:s -c:v copy -c:a copy -c:s webvtt "$D/TestWebm 03.webm"
rm -f /tmp/sqe-embedded.vtt
printf 'WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nVTT line\n' > "$D/TestWebm 03.de.vtt"
printf '1\n00:00:01,000 --> 00:00:03,000\nGood line one\n\n2\n00:00:04,000 --> 00:00:06,000\nGood line two\n' > "$D/TestWebm 03.en.srt"
ls -l "$D"
