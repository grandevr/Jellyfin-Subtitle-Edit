#!/bin/sh
# Put the THROWAWAY test server's subtitle fixtures, folder modes and the plugin's backup folder back to a known state.
# Test folder only: $SQE_TEST_ROOT. Needs the settings in devtools/env.sh. Usage: testdata/reset_fixtures.sh
set -e
here="$(cd "$(dirname "$0")" && pwd)"
. "$here/../devtools/env.sh"
tmp="$(mktemp -d)"
python3 -I "$here/make_testdata.py" "$tmp"
tar -c --owner=1000 --group=1000 -C "$tmp" . | sqe_host tar -x -C "$SQE_TEST_ROOT/media"
rm -rf "$tmp"
sqe_host chmod 755 "$SQE_TEST_ROOT/media/TestShow/Season 01"
sqe_host rm -rf "$SQE_TEST_ROOT/config/data/SubtitleQuickEdit/backups" "$SQE_TEST_ROOT/config/data/SubtitleQuickEdit/submissions" "$SQE_TEST_ROOT/config/data/SubtitleQuickEdit/extracted"
sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s <<'INNER'
D="/data/TestShow/Season 01"
# Subtitles left by an interrupted extraction test (stage 6): episode 3 subtitle files that are not fixtures.
for f in "$D/TestShow - S01E03".*.srt "$D/TestShow - S01E03".*.ass "$D/TestShow - S01E03".*.ssa "$D/TestShow - S01E03".*.vtt "$D/TestShow - S01E03.srt" "$D/TestShow - S01E03.ass"; do
  case "$f" in *.en.srt|*.ja.ass|*.de.vtt|*.fr.srt) ;; *) rm -f "$f" ;; esac
done
printf '1\n00:00:01,000 --> 00:00:03,000\nGood line one\n\n2\n00:00:04,000 --> 00:00:06,000\nGood line two\n' > "$D/TestShow - S01E03.en.srt"
printf '[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Styled line\n' > "$D/TestShow - S01E03.ja.ass"
printf 'WEBVTT\n\n00:01.000 --> 00:03.000\nVTT line\n' > "$D/TestShow - S01E03.de.vtt"
printf '1\n00:00:01,000 --> 00:00:03,000\nFirst\n\nstray paragraph after a blank line\n\n2\n00:00:04,000 --> 00:00:06,000\nSecond\n' > "$D/TestShow - S01E03.fr.srt"
INNER
