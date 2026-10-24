#!/usr/bin/env python3
"""Complete the startup wizard on the THROWAWAY test Jellyfin and add the test library.

Usage: bootstrap_test_server.py <base-url> <secrets-dir>
Writes <secrets-dir>/admin.pw and <secrets-dir>/token (mode 600). Prints neither.
Creates two libraries: TestTV on /data/TestShow and TestWebm (movies) on /data/TestWebm. Both folders should exist before
this runs (testdata/reset_fixtures.sh creates the first; the second holds the VP9/Opus copies for browser tests).
Test server only: never point this at the live Jellyfin.
"""
import json
import os
import secrets
import sys
import urllib.request

base = sys.argv[1].rstrip("/")
sdir = sys.argv[2]
AUTH = 'MediaBrowser Client="bootstrap", Device="ct111", DeviceId="stage0-bootstrap", Version="1.0"'


def call(method, path, body=None, token=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    hdr = AUTH + (', Token="%s"' % token if token else "")
    req.add_header("Authorization", hdr)  # Jellyfin 12 ignores X-Emby-Authorization
    with urllib.request.urlopen(req, timeout=60) as r:
        raw = r.read()
        return r.status, (json.loads(raw) if raw else None)


def write_secret(name, value):
    path = os.path.join(sdir, name)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(value)


os.makedirs(sdir, mode=0o700, exist_ok=True)
pw_path = os.path.join(sdir, "admin.pw")
if os.path.exists(pw_path):
    # Wizard already ran with this password: reuse it.
    pw = open(pw_path).read()
else:
    pw = secrets.token_urlsafe(12)
    write_secret("admin.pw", pw)
    print("config", call("POST", "/Startup/Configuration",
                         {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})[0])
    call("GET", "/Startup/User")
    print("user", call("POST", "/Startup/User", {"Name": "testadmin", "Password": pw})[0])
    print("remote", call("POST", "/Startup/RemoteAccess",
                         {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})[0])
    print("complete", call("POST", "/Startup/Complete")[0])

st, auth = call("POST", "/Users/AuthenticateByName", {"Username": "testadmin", "Pw": pw})
token = auth["AccessToken"]
write_secret("token", token)
print("auth", st, "admin" if auth["User"]["Policy"]["IsAdministrator"] else "NOT admin")

# The libraries must not overlap: /data/TestShow holds the episodes the API tests count, /data/TestWebm the VP9/Opus
# copies that browser tests play (a TV library rooted at /data would pick the copies up as episodes).
st, _ = call("POST", "/Library/VirtualFolders?name=TestTV&collectionType=tvshows&refreshLibrary=true",
             {"LibraryOptions": {"PathInfos": [{"Path": "/data/TestShow"}]}}, token)
print("library TestTV", st)
st, _ = call("POST", "/Library/VirtualFolders?name=TestWebm&collectionType=movies&refreshLibrary=true",
             {"LibraryOptions": {"PathInfos": [{"Path": "/data/TestWebm"}]}}, token)
print("library TestWebm", st)
