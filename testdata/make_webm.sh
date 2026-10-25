#!/bin/sh
# Run inside the test Jellyfin container as uid 1000, after make_videos.sh and make_testdata.py:
#   . devtools/env.sh; sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/make_webm.sh
# VP9/Opus copies of the two test videos for browser tests (Playwright's Chromium cannot decode H.264), with copies of
# their English and Polish .srt files, and a third movie with an embedded track and .vtt and .ass files. They live in their own
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
# Movie 3: an embedded subtitle (the edit button must stay hidden for it), an external .vtt and .ass (editable from 1.1),
# and an editable external .srt to switch to.
printf 'WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nEmbedded line\n' > /tmp/sqe-embedded.vtt
"$FF" -y -loglevel error -i "$D/TestWebm 01.webm" -i /tmp/sqe-embedded.vtt -t 30 \
  -map 0:v -map 0:a -map 1:s -c:v copy -c:a copy -c:s webvtt "$D/TestWebm 03.webm"
rm -f /tmp/sqe-embedded.vtt
# Keep these two files the same as in reset_webm03_formats.sh (the stage 5 browser tests edit them).
cat > "$D/TestWebm 03.de.vtt" <<'EOF'
WEBVTT

00:00:01.000 --> 00:00:03.000
VTT line

00:00:04.000 --> 00:00:06.000 line:0
<i>Styled VTT line</i>

00:00:07.000 --> 00:00:09.000
<v Anna>Anna speaks</v>
EOF
cat > "$D/TestWebm 03.ja.ass" <<'EOF'
[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,24,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
Style: Sign,Arial,20,&H0000FFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,8,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Plain ASS line
Dialogue: 0,0:00:04.00,0:00:06.00,Sign,,0,0,0,,{\an8\c&H00FFFF&}Top sign, with a comma
Dialogue: 0,0:00:07.00,0:00:09.00,Default,,0,0,0,,Two lines\Nin one cue
Dialogue: 1,0:00:10.00,0:00:12.00,Sign,,0,0,0,,{\p1}m 0 0 l 100 0 100 100 0 100{\p0}
EOF
# Keep this line the same as in reset_webm03_srt.sh.
printf '1\n00:00:01,000 --> 00:00:03,000\nGood line one\n\n2\n00:00:04,000 --> 00:00:06,000\nGood line two\n' > "$D/TestWebm 03.en.srt"
ls -l "$D"
