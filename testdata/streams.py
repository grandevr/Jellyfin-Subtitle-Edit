#!/usr/bin/env python3
"""List the subtitle streams of an item on the THROWAWAY test Jellyfin.

Usage: streams.py <secrets-dir> <itemId>
"""
import json
import os
import sys
import urllib.request

BASE = os.environ.get("JF_BASE") or sys.exit("set JF_BASE to the test server URL")
sdir, item = sys.argv[1:3]
tok = open(os.path.join(sdir, "token")).read()


def get(p):
    r = urllib.request.Request(BASE + p)
    r.add_header("Authorization", 'MediaBrowser Client="api", Device="sqe-tests", DeviceId="stage0-api", Version="1.0", Token="%s"' % tok)
    return json.load(urllib.request.urlopen(r, timeout=60))


uid = get("/Users/Me")["Id"]
d = get("/Users/%s/Items/%s" % (uid, item))
for s in d["MediaSources"][0]["MediaStreams"]:
    if s["Type"] == "Subtitle":
        print("idx %s lang=%s ext=%s %s" % (s["Index"], s.get("Language"), s.get("IsExternal"), os.path.basename(s.get("Path", ""))))
