#!/bin/sh
# Puts "TestWebm 03.en.srt" back to the two lines testdata/make_webm.sh writes (the 3f browser test adds a line to it,
# which the plugin's API cannot remove). Run inside the test Jellyfin container as uid 1000, like make_webm.sh:
#   . devtools/env.sh; sqe_host docker exec -i -u 1000 "$SQE_CONTAINER" sh -s < testdata/reset_webm03_srt.sh
set -e
printf '1\n00:00:01,000 --> 00:00:03,000\nGood line one\n\n2\n00:00:04,000 --> 00:00:06,000\nGood line two\n' > "/data/TestWebm/TestWebm 03.en.srt"
ls -l "/data/TestWebm/TestWebm 03.en.srt"
