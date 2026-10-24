#!/usr/bin/env python3
"""Tiny authenticated client for the THROWAWAY test Jellyfin.

Usage: api.py <secrets-dir> <METHOD> <path> [json-body]
Reads <secrets-dir>/token. Prints status, selected headers and the (JSON) body.
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("JF_BASE", "http://192.168.0.21:8097")
sdir, method, path = sys.argv[1:4]
body = sys.argv[4] if len(sys.argv) > 4 else None
token = open(os.path.join(sdir, "token")).read()
req = urllib.request.Request(BASE + path, data=body.encode() if body else None, method=method)
req.add_header("Content-Type", "application/json")
req.add_header("Authorization",
               'MediaBrowser Client="api", Device="ct111", DeviceId="stage0-api", Version="1.0", Token="%s"' % token)
try:
    r = urllib.request.urlopen(req, timeout=60)
    status, hdrs, raw = r.status, r.headers, r.read()
except urllib.error.HTTPError as e:
    status, hdrs, raw = e.code, e.headers, e.read()
print("HTTP", status)
for h in ("Content-Type", "Cache-Control", "ETag", "Last-Modified", "Expires"):
    if hdrs.get(h):
        print("%s: %s" % (h, hdrs[h]))
text = raw.decode("utf-8", "replace")
try:
    print(json.dumps(json.loads(text), indent=1, ensure_ascii=False))
except ValueError:
    print(text)
