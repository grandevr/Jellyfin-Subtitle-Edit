#!/usr/bin/env python3
"""Fetch subtitle streams from the THROWAWAY test Jellyfin as a client would.

Usage: fetch_subs.py <secrets-dir> <itemId> <streamIndex> [vtt|srt|js]
Prints status, cache-related headers, and the first lines of the decoded body.
"""
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("JF_BASE", "http://192.168.0.21:8097")
sdir, item, idx = sys.argv[1:4]
fmt = sys.argv[4] if len(sys.argv) > 4 else "vtt"
tok = open(os.path.join(sdir, "token")).read()
url = "%s/Videos/%s/%s/Subtitles/%s/0/Stream.%s" % (BASE, item, item, idx, fmt)
req = urllib.request.Request(url)
req.add_header("Authorization", 'MediaBrowser Client="api", Device="ct111", DeviceId="stage0-api", Version="1.0", Token="%s"' % tok)
try:
    r = urllib.request.urlopen(req, timeout=60)
    status, hdrs, raw = r.status, r.headers, r.read()
except urllib.error.HTTPError as e:
    status, hdrs, raw = e.code, e.headers, e.read()
print("HTTP", status, "bytes", len(raw))
for h in ("Content-Type", "Cache-Control", "ETag", "Last-Modified", "Expires", "Accept-Ranges"):
    if hdrs.get(h):
        print("  %s: %s" % (h, hdrs[h]))
print("  starts with BOM:", raw[:3] == b"\xef\xbb\xbf")
text = raw.decode("utf-8", "replace")
print("  has U+FFFD:", "�" in text)
for line in text.splitlines()[:int(os.environ.get("LINES", "8"))]:
    print("  |", line)
