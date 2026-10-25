#!/bin/sh
# Writes the WebVTT and ASS subtitles of "TestWebm 03" that the stage 5 browser tests edit (testdata/browser/stage5_tests.py)
# and puts them back after a run. Run inside the test Jellyfin container as uid 1000, like make_webm.sh:
#   . devtools/env.sh; sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/reset_webm03_formats.sh
# Keep the two files the same as in make_webm.sh.
set -e
D="/data/TestWebm"
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
ls -l "$D/TestWebm 03.de.vtt" "$D/TestWebm 03.ja.ass"
