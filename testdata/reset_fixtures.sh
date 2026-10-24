#!/bin/sh
# Put the THROWAWAY test server's subtitle fixtures, folder modes and the plugin's backup folder back to a known state.
# Test folder only: /opt/jellyfin-test. Usage: testdata/reset_fixtures.sh
set -e
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
python3 -I "$here/make_testdata.py" "$tmp"
tar -c --owner=1000 --group=1000 -C "$tmp" . | ssh pve pct exec 103 -- tar -x -C /opt/jellyfin-test/media
rm -rf "$tmp"
ssh pve "pct exec 103 -- chmod 755 '/opt/jellyfin-test/media/TestShow/Season 01'"
ssh pve pct exec 103 -- rm -rf /opt/jellyfin-test/config/data/SubtitleQuickEdit/backups
ssh pve pct exec 103 -- docker exec -i -u 1000 jellyfin-test sh -s <<'INNER'
D="/data/TestShow/Season 01"
printf '1\n00:00:01,000 --> 00:00:03,000\nGood line one\n\n2\n00:00:04,000 --> 00:00:06,000\nGood line two\n' > "$D/TestShow - S01E03.en.srt"
printf '[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Styled line\n' > "$D/TestShow - S01E03.ja.ass"
printf 'WEBVTT\n\n00:01.000 --> 00:03.000\nVTT line\n' > "$D/TestShow - S01E03.de.vtt"
printf '1\n00:00:01,000 --> 00:00:03,000\nFirst\n\nstray paragraph after a blank line\n\n2\n00:00:04,000 --> 00:00:06,000\nSecond\n' > "$D/TestShow - S01E03.fr.srt"
INNER
