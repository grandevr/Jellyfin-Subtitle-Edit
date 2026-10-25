#!/usr/bin/env python3
"""Stage 6 integration tests: making episode 3's embedded subtitle track editable through the plugin's API, against the
THROWAWAY test Jellyfin. Needs JF_BASE, SQE_HOST_EXEC and SQE_TEST_ROOT (see devtools/env.sh); SQE_LOG_CMD is optional.

Usage: python3 -I testdata/stage6_api_tests.py <secrets-dir>
Needs token and testuser.token in <secrets-dir>. Resets the fixtures and the plugin settings first and last. In between
it extracts the embedded track to a new file next to episode 3, edits, replaces and deletes it. Prints PASS/FAIL per
check; exit code 1 on any failure. Never touches the live Jellyfin: every URL and path below is the test server's.
"""
import hashlib
import json
import os
import re
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ["JF_BASE"].rstrip("/")
HOST_EXEC = shlex.split(os.environ["SQE_HOST_EXEC"])
TEST_ROOT = os.environ["SQE_TEST_ROOT"]
CONTAINER = os.environ.get("SQE_CONTAINER", "jellyfin-test")
PLUGIN_ID = "b3b6f2a4-6f0e-4d5c-9a0c-5e2f6c1d7a31"
HOST_MEDIA = TEST_ROOT + "/media/TestShow/Season 01"
HOST_BACKUPS = TEST_ROOT + "/config/data/SubtitleQuickEdit/backups"
CONTAINER_MEDIA = "/data/TestShow/Season 01"
HERE = os.path.dirname(os.path.abspath(__file__))
SECRETS = sys.argv[1]

results = []


def token(name):
    return open(os.path.join(SECRETS, name)).read()


ADMIN, USER = token("token"), token("testuser.token")


def call(method, path, body=None, tok=ADMIN):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    auth = 'MediaBrowser Client="s6", Device="sqe-tests", DeviceId="stage6-tests", Version="1.0"'
    if tok:
        auth += ', Token="%s"' % tok
    req.add_header("Authorization", auth)
    try:
        r = urllib.request.urlopen(req, timeout=120)
        status, raw_out = r.status, r.read()
    except urllib.error.HTTPError as e:
        status, raw_out = e.code, e.read()
    try:
        return status, json.loads(raw_out) if raw_out else None
    except ValueError:
        return status, raw_out.decode("utf-8", "replace")


def host_cmd(*args):
    return HOST_EXEC + [" ".join(shlex.quote(a) for a in args)]


def host(*args):
    out = subprocess.run(host_cmd(*args), capture_output=True, check=False)
    if out.returncode != 0:
        raise RuntimeError("host command failed: %s %s" % (args, out.stderr.decode()[:200]))
    return out.stdout


def log_cmd():
    custom = os.environ.get("SQE_LOG_CMD")
    return shlex.split(custom) if custom else host_cmd("docker", "logs", "--tail", "2000", CONTAINER)


def plugin_log():
    return subprocess.run(log_cmd(), capture_output=True, text=True).stdout.splitlines()


def read_file(name):
    return host("cat", "%s/%s" % (HOST_MEDIA, name))


def write_fixture(name, data):
    """Writes a test subtitle as the container's user (uid 1000), like reset_fixtures.sh does."""
    path = "%s/%s" % (CONTAINER_MEDIA, name)
    out = subprocess.run(host_cmd("docker", "exec", "-i", "-u", "1000", CONTAINER, "sh", "-c", 'cat > "$1"', "sh", path),
                         input=data, capture_output=True, check=False)
    if out.returncode != 0:
        raise RuntimeError("could not write %s: %s" % (name, out.stderr.decode()[:200]))


def backups():
    try:
        return [n for n in host("ls", "-A", HOST_BACKUPS).decode().split("\n") if n]
    except RuntimeError:
        return []


def sha(data):
    return hashlib.sha256(data).hexdigest()


def check(name, condition, detail=""):
    results.append((name, bool(condition), detail))
    print(("PASS  " if condition else "FAIL  ") + name + ("" if condition else "   <-- " + str(detail)))



DEFAULTS = {"AllowUserSubmissions": False, "UserEdits": "Off", "UserEditUserIds": [], "MaxPendingSubmissionsPerUser": 50,
            "BackupMode": "OriginalOnly"}


def configure(**settings):
    st, cfg = call("GET", "/Plugins/%s/Configuration" % PLUGIN_ID)
    cfg.update(settings)
    return call("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, cfg)[0]


def sub(idx, part):
    return "/SubtitleQuickEdit/Items/%s/Subtitles/%s/%s" % (EP3, idx, part)


def tracks(tok=ADMIN):
    st, t = call("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % EP3, tok=tok)
    return t if st == 200 else []


def host_exists(name):
    out = subprocess.run(host_cmd("test", "-e", "%s/%s" % (HOST_MEDIA, name)), capture_output=True, check=False)
    return out.returncode == 0


def stat(name):
    return host("stat", "-c", "%u:%g:%a", "%s/%s" % (HOST_MEDIA, name)).decode().strip()


def code(r):
    return r.get("code") if isinstance(r, dict) else r


LOG_BEFORE = plugin_log()

# ---- setup -----------------------------------------------------------------------------------------------------

subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
check("settings: start from the defaults", configure(**DEFAULTS) == 204)
st, items = call("GET", "/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path")
EP3 = [it["Id"] for it in items["Items"] if "S01E03" in it["Path"]][0]
VIDEO = "TestShow - S01E03.mkv"
st, me = call("GET", "/Users/Me", tok=USER)
USER_NAME = me["Name"]
before = tracks()
emb = [t for t in before if not t["external"]]
check("listing: episode 3 has one embedded track, extractable, not editable, no copy yet",
      len(emb) == 1 and emb[0]["extractable"] and not emb[0]["editable"] and emb[0].get("extractedStreamIndex") is None, emb)
EMB = emb[0]["streamIndex"]
EN = next(t["streamIndex"] for t in before if t["external"] and t["format"] == "srt" and t["language"] in ("eng", "en"))
check("listing: external tracks are not extractable", all(not t["extractable"] for t in before if t["external"]), before)

# ---- rights and refusals -----------------------------------------------------------------------------------------

st, r = call("GET", sub(EMB, "Extract"), tok=USER)
check("preview: a user without any right -> 403", st == 403, (st, r))
st, r = call("POST", sub(EMB, "Extract"), {}, tok=USER)
check("extract: a user without any right -> 403, nothing written", st == 403 and len(tracks()) == len(before), (st, r))
st, r = call("GET", sub(EN, "Extract"))
check("preview: an external track -> 400 AlreadyExternal", st == 400 and code(r) == "AlreadyExternal", (st, r))
st, r = call("GET", sub(99, "Extract"))
check("preview: no such stream -> 404 StreamNotFound", st == 404 and code(r) == "StreamNotFound", (st, r))
st, r = call("POST", sub(EMB, "Extract"), {"replace": True})
check("extract: replace without a hash -> 400 InvalidRequest", st == 400 and code(r) == "InvalidRequest", (st, r))

# ---- a submitter makes an editable copy ----------------------------------------------------------------------------

check("settings: user submissions on", configure(AllowUserSubmissions=True) == 204)
st, p = call("GET", sub(EMB, "Extract"), tok=USER)
NAME = p.get("fileName", "") if isinstance(p, dict) else ""
check("preview: 200, a SubRip file named after the video, one line, nothing there yet, no replace for users",
      st == 200 and NAME.startswith("TestShow - S01E03.") and NAME.endswith(".srt") and p["format"] == "srt"
      and p["cueCount"] == 1 and not p["exists"] and not p["canReplace"] and p.get("cues") is None, (st, p))
check("preview: nothing was written", not host_exists(NAME), NAME)

st, x = call("POST", sub(EMB, "Extract"), {}, tok=USER)
NEW = x.get("streamIndex") if isinstance(x, dict) else None
check("extract: 200, the file name from the preview, picked up by the library as a new stream",
      st == 200 and x["fileName"] == NAME and NEW is not None and not x["refreshPending"] and not x["replaced"], (st, x))
data = read_file(NAME) if host_exists(NAME) else b""
check("extract: the file holds the embedded line and matches the reported hash",
      b"Embedded line" in data and sha(data) == x.get("hash"), data)
video_mode = int(stat(VIDEO).split(":")[2], 8)
check("extract: the file has the video's owner and group, and its mode without execute bits",
      stat(NAME) == "%s:%o" % (":".join(stat(VIDEO).split(":")[:2]), video_mode & 0o666), (stat(NAME), stat(VIDEO)))
after = tracks()
new_track = next((t for t in after if t["streamIndex"] == NEW), None)
check("listing: the new track is external, SubRip and editable",
      new_track is not None and new_track["external"] and new_track["format"] == "srt" and new_track["editable"], new_track)
emb_now = next((t for t in after if not t["external"]), None)
check("listing: the embedded track is still there and points to its copy",
      emb_now is not None and emb_now.get("extractedStreamIndex") == NEW, emb_now)
# Jellyfin numbers external files before the video's own streams, so the new file moves the embedded track up by one.
EMB_BEFORE, EMB = EMB, emb_now["streamIndex"] if emb_now else EMB
check("listing: the embedded track's index moved past the new file", EMB == EMB_BEFORE + 1, (EMB_BEFORE, EMB))
st, c = call("GET", sub(NEW, "Cues"), tok=USER)
check("cues: the copy reads back with the embedded line", st == 200 and [q["text"] for q in c["cues"]] == ["Embedded line"], (st, c))
st, act = call("GET", "/System/ActivityLog/Entries?limit=30")
check("activity log: the admins see that a user made an editable copy",
      st == 200 and any(e.get("Type") == "SubtitleQuickEditExtract" for e in act.get("Items", [])), act if st != 200 else "")

st, r = call("POST", sub(EMB, "Extract"), {}, tok=USER)
check("extract again: 409 FileExists with the file's hash, file unchanged",
      st == 409 and code(r) == "FileExists" and r.get("currentHash") == sha(data) and read_file(NAME) == data, (st, r))
st, r = call("POST", sub(EMB, "Extract"), {"replace": True, "expectedExistingHash": sha(data)}, tok=USER)
check("replace by a user: 403, file unchanged", st == 403 and read_file(NAME) == data, (st, r))

# ---- the admin edits, compares and replaces ----------------------------------------------------------------------

st, r = call("POST", sub(NEW, "Edits"), {"baseHash": c["hash"], "edits": [
    {"type": "edit", "cueIndex": 1, "oldText": "Embedded line", "newText": "Embedded line, fixed"}]})
edited = read_file(NAME)
check("edit the copy: 200, saved like any external file", st == 200 and b"Embedded line, fixed" in edited, (st, r))
st, ex = call("GET", "/SubtitleQuickEdit/Extracted")
rec = [e for e in ex if e["fileName"] == NAME] if st == 200 else []
check("review list: one entry, made by the user, edited since, with its stream",
      len(ex) == 1 and len(rec) == 1 and rec[0]["userName"] == USER_NAME and rec[0]["edited"] and rec[0]["exists"]
      and rec[0]["streamIndex"] == NEW and rec[0]["sourceStreamIndex"] == EMB_BEFORE and not rec[0]["replaced"], (st, ex))
st, r = call("GET", "/SubtitleQuickEdit/Extracted", tok=USER)
check("review list: users -> 403", st == 403, (st, r))

st, p = call("GET", sub(EMB, "Extract"))
check("admin preview: the existing file, its stream and both sets of lines for the comparison",
      st == 200 and p["exists"] and p["existingIsExtracted"] and p["existingStreamIndex"] == NEW and p["canReplace"]
      and p["existingHash"] == sha(edited) and [q["text"] for q in p["cues"]] == ["Embedded line"]
      and [q["text"] for q in p["existingCues"]] == ["Embedded line, fixed"], (st, p))
st, r = call("POST", sub(EMB, "Extract"), {"replace": True, "expectedExistingHash": sha(data)})
check("admin replace with a stale hash: 409 FileChanged, file unchanged",
      st == 409 and code(r) == "FileChanged" and r.get("currentHash") == sha(edited) and read_file(NAME) == edited, (st, r))
st, x2 = call("POST", sub(EMB, "Extract"), {"replace": True, "expectedExistingHash": sha(edited)})
check("admin replace: 200, replaced, the file is the embedded track again, same stream",
      st == 200 and x2["replaced"] and read_file(NAME) == data and x2["streamIndex"] == NEW, (st, x2))
kept = [b for b in backups() if b.endswith("_replaced.srt")]
check("admin replace: the edited file is kept in the backups", len(kept) == 1 and host("cat", HOST_BACKUPS + "/" + kept[0]) == edited, backups())
st, ex = call("GET", "/SubtitleQuickEdit/Extracted")
check("review list: still one entry, now the admin's, marked as a replacement, not edited",
      st == 200 and len(ex) == 1 and ex[0]["replaced"] and not ex[0]["edited"] and ex[0]["userName"] != USER_NAME, (st, ex))

# ---- delete --------------------------------------------------------------------------------------------------------

RID = ex[0]["id"] if st == 200 and ex else "0" * 32
st, r = call("DELETE", "/SubtitleQuickEdit/Extracted/%s" % RID, tok=USER)
check("delete by a user: 403, file kept", st == 403 and host_exists(NAME), (st, r))
st, r = call("DELETE", "/SubtitleQuickEdit/Extracted/%s" % RID)
check("delete: 200, backed up, the file is gone", st == 200 and r["backedUp"] and r["fileName"] == NAME and not host_exists(NAME), (st, r))
gone = [b for b in backups() if b.endswith("_deleted.srt")]
check("delete: the last content is kept in the backups", len(gone) == 1 and host("cat", HOST_BACKUPS + "/" + gone[0]) == data, backups())
for _ in range(20):
    final = tracks()
    if all(t["streamIndex"] != NEW or not t["external"] for t in final) and len(final) == len(before):
        break
    time.sleep(0.5)
check("delete: the library dropped the copy, the embedded track is still there with no copy",
      len(final) == len(before) and any(not t["external"] and t.get("extractedStreamIndex") is None for t in final), final)
st, r = call("DELETE", "/SubtitleQuickEdit/Extracted/%s" % RID)
check("delete again: 404 ExtractedNotFound", st == 404 and code(r) == "ExtractedNotFound", (st, r))
st, ex = call("GET", "/SubtitleQuickEdit/Extracted")
check("review list: empty", st == 200 and ex == [], (st, ex))

# ---- log and clean up --------------------------------------------------------------------------------------------

time.sleep(1)
fresh = [line for line in plugin_log() if line not in set(LOG_BEFORE)]
errs = [line for line in fresh if "[ERR]" in line and "SubtitleQuickEdit" in line]
check("log: no plugin errors", not errs, errs)
check("log: the extraction is logged", any("extracted embedded subtitle stream" in line for line in fresh), fresh[-20:])

configure(**DEFAULTS)
subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
failed = [n for n, ok, _ in results if not ok]
print("\n%d checks, %d passed, %d failed" % (len(results), len(results) - len(failed), len(failed)))
sys.exit(1 if failed else 0)
