#!/bin/sh
# Re-arm the real group-mismatch fixture on the test host: a file owned by uid 1000 whose group (2000) uid 1000 is not in.
# A write that renames a new file over it (instead of copying in place) resets the group to 1000, so run this before
# every test run that must exercise the fallback. Test folder only: /opt/jellyfin-test/src/mismatch.
set -e
ssh pve pct exec 103 -- chown 1000:2000 /opt/jellyfin-test/src/mismatch/file.srt
