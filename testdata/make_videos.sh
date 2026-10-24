#!/bin/sh
# Run inside the test Jellyfin container as uid 1000:
#   ssh pve pct exec 103 -- docker exec -i -u 1000 jellyfin-test sh -s < make_videos.sh
# Creates two 60 s test videos next to the generated .srt files.
set -e
D="/data/TestShow/Season 01"
FF=/usr/lib/jellyfin-ffmpeg/ffmpeg
for n in 01 02; do
  "$FF" -y -loglevel error \
    -f lavfi -i "testsrc2=duration=60:size=640x360:rate=24" \
    -f lavfi -i "sine=frequency=$((300 + 100 * ${n#0})):duration=60" \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 64k -shortest \
    -movflags +faststart "$D/TestShow - S01E$n.mp4"
done
ls -l "$D"/*.mp4
