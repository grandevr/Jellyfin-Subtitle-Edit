#!/bin/sh
# Run inside the test Jellyfin container as uid 1000:
#   . devtools/env.sh; sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/make_ep3.sh
# Episode 3: a video with an EMBEDDED subtitle track, plus external subtitles the editor must refuse.
set -e
D="/data/TestShow/Season 01"
FF=/usr/lib/jellyfin-ffmpeg/ffmpeg
printf '1\n00:00:01,000 --> 00:00:03,000\nEmbedded line\n\n' > /tmp/embedded.srt
"$FF" -y -loglevel error \
  -f lavfi -i "testsrc2=duration=30:size=640x360:rate=24" \
  -f lavfi -i "sine=frequency=500:duration=30" \
  -i /tmp/embedded.srt \
  -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 64k -c:s srt -shortest \
  "$D/TestShow - S01E03.mkv"
rm -f /tmp/embedded.srt
printf '1\n00:00:01,000 --> 00:00:03,000\nGood line one\n\n2\n00:00:04,000 --> 00:00:06,000\nGood line two\n' > "$D/TestShow - S01E03.en.srt"
printf '[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Styled line\n' > "$D/TestShow - S01E03.ja.ass"
printf 'WEBVTT\n\n00:01.000 --> 00:03.000\nVTT line\n' > "$D/TestShow - S01E03.de.vtt"
printf '1\n00:00:01,000 --> 00:00:03,000\nFirst\n\nstray paragraph after a blank line\n\n2\n00:00:04,000 --> 00:00:06,000\nSecond\n' > "$D/TestShow - S01E03.fr.srt"
ls -l "$D"/*S01E03*
