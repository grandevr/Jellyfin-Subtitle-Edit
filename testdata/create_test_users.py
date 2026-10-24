#!/usr/bin/env python3
"""Create two throwaway users on the THROWAWAY test Jellyfin: 'testuser' (regular) and 'limiteduser' (no library access).

Usage: create_test_users.py <base-url> <secrets-dir>
Needs <secrets-dir>/token (admin). Writes <name>.pw and <name>.token (mode 600). Prints no secrets.
"""
import json
import os
import secrets
import sys
import urllib.request

base = sys.argv[1].rstrip("/")
sdir = sys.argv[2]
HDR = 'MediaBrowser Client="users", Device="ct111", DeviceId="stage2-users", Version="1.0"'


def call(method, path, body=None, token=None):
    req = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), method=method)
    req.add_header("Content-Type", "application/json")
    req.add_header("Authorization", HDR + (', Token="%s"' % token if token else ""))
    with urllib.request.urlopen(req, timeout=60) as r:
        raw = r.read()
        return r.status, (json.loads(raw) if raw else None)


def secret(name, value):
    fd = os.open(os.path.join(sdir, name), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(value)


admin = open(os.path.join(sdir, "token")).read()
existing = {u["Name"]: u["Id"] for u in call("GET", "/Users", token=admin)[1]}
for name, restricted in (("testuser", False), ("limiteduser", True)):
    pw = secrets.token_urlsafe(12)
    if name in existing:
        print(name, "exists: leaving it (delete it first to recreate)")
        continue
    st, user = call("POST", "/Users/New", {"Name": name, "Password": pw}, admin)
    uid = user["Id"]
    if restricted:
        policy = call("GET", "/Users/%s" % uid, token=admin)[1]["Policy"]
        policy["EnableAllFolders"] = False
        policy["EnabledFolders"] = []
        call("POST", "/Users/%s/Policy" % uid, policy, admin)
    secret(name + ".pw", pw)
    st, auth = call("POST", "/Users/AuthenticateByName", {"Username": name, "Pw": pw})
    secret(name + ".token", auth["AccessToken"])
    print(name, "created", "admin" if auth["User"]["Policy"]["IsAdministrator"] else "non-admin",
          "restricted" if restricted else "all folders")
