#!/usr/bin/env python3
"""Stage 2 integration tests: the plugin's server API against the THROWAWAY test Jellyfin.
Needs JF_BASE, SQE_HOST_EXEC and SQE_TEST_ROOT (see devtools/env.sh); SQE_LOG_CMD is optional.

Usage: python3 -I testdata/stage2_api_tests.py <secrets-dir>
Needs admin.pw/token, testuser.token and limiteduser.token in <secrets-dir> (bootstrap_test_server.py and
create_test_users.py write them). Resets the fixtures first. Prints PASS/FAIL per check; exit code 1 on any failure.
Never touches the live Jellyfin: every URL and path below is the test server's.
"""
import hashlib
import json
import os
import re
import shlex
import subprocess
import sys
import threading
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
HERE = os.path.dirname(os.path.abspath(__file__))
SECRETS = sys.argv[1]

results = []


def token(name):
    return open(os.path.join(SECRETS, name)).read()


ADMIN, USER, LIMITED = token("token"), token("testuser.token"), token("limiteduser.token")


def call(method, path, body=None, tok=ADMIN, raw=None, ctype="application/json"):
    data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", ctype)
    auth = 'MediaBrowser Client="s2", Device="sqe-tests", DeviceId="stage2-tests", Version="1.0"'
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
    """SQE_HOST_EXEC plus the command as one quoted string (see devtools/env.sh)."""
    return HOST_EXEC + [" ".join(shlex.quote(a) for a in args)]


def log_cmd():
    """Command that prints the test container's last 2000 log lines: SQE_LOG_CMD if set, else docker logs on the host."""
    custom = os.environ.get("SQE_LOG_CMD")
    if custom:
        return shlex.split(custom)
    return host_cmd("docker", "logs", "--tail", "2000", CONTAINER)


def host(*args):
    out = subprocess.run(host_cmd(*args), capture_output=True, check=False)
    if out.returncode != 0:
        raise RuntimeError("host command failed: %s %s" % (args, out.stderr.decode()[:200]))
    return out.stdout


def read_file(name):
    return host("cat", "%s/%s" % (HOST_MEDIA, name))


def stat_file(name):
    uid, gid, mode, inode = host("stat", "-c", "%u %g %a %i", "%s/%s" % (HOST_MEDIA, name)).decode().split()
    return {"uid": int(uid), "gid": int(gid), "mode": mode, "inode": int(inode)}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def writer_files():
    names = host("ls", "-A", HOST_MEDIA).decode().splitlines()
    return [n for n in names if n.startswith(".") and (n.endswith(".tmp") or n.endswith(".inplace"))]


def backups():
    try:
        return [n for n in host("ls", "-A", HOST_BACKUPS).decode().split("\n") if n]
    except RuntimeError:
        return []


def read_backup(name):
    return host("cat", HOST_BACKUPS + "/" + name)


def check(name, condition, detail=""):
    results.append((name, bool(condition), detail))
    print(("PASS  " if condition else "FAIL  ") + name + ("" if condition else "   <-- " + str(detail)))


def edits_url(item, idx):
    return "/SubtitleQuickEdit/Items/%s/Subtitles/%s/Edits" % (item, idx)


def cues_url(item, idx):
    return "/SubtitleQuickEdit/Items/%s/Subtitles/%s/Cues" % (item, idx)


def list_url(item):
    return "/SubtitleQuickEdit/Items/%s/Subtitles" % item


def set_submissions(enabled):
    st, cfg = call("GET", "/Plugins/%s/Configuration" % PLUGIN_ID)
    cfg["AllowUserSubmissions"] = enabled
    return call("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, cfg)[0]


def plugin_log():
    out = subprocess.run(log_cmd(), capture_output=True, text=True).stdout
    return out.splitlines()


LOG_BEFORE = plugin_log()


# ---- setup -----------------------------------------------------------------------------------------------

subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
set_submissions(False)
st, items = call("GET", "/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path")
EP = {}
for it in items["Items"]:
    m = re.search(r"S01E0(\d)", it["Path"])
    EP[int(m.group(1))] = it["Id"]
check("three test episodes are in the library", sorted(EP) == [1, 2, 3], EP)
call("POST", "/Items/%s/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=None&ReplaceAllMetadata=false&ReplaceAllImages=false" % EP[3])
time.sleep(8)  # let Jellyfin notice the episode 3 subtitle fixtures that reset_fixtures.sh just rewrote


def track(item, lang, fmt=None):
    st, tracks = call("GET", list_url(item))
    for t in tracks:
        if t.get("language") == lang and (fmt is None or t["format"] == fmt):
            return t
    raise KeyError((item, lang))


# ---- plugin, settings page ---------------------------------------------------------------------------------

st, plugins = call("GET", "/Plugins")
mine = [p for p in plugins if p["Id"].replace("-", "").lower() == PLUGIN_ID.replace("-", "")]
check("the plugin is installed and active", mine and mine[0]["Name"] == "Subtitle Editor" and mine[0]["Status"] == "Active", mine)
st, cfg = call("GET", "/Plugins/%s/Configuration" % PLUGIN_ID)
check(
    "default settings: submissions off, 50 pending, original-only backups",
    st == 200 and cfg["AllowUserSubmissions"] is False and cfg["MaxPendingSubmissionsPerUser"] == 50
    and cfg["BackupMode"] == "OriginalOnly" and cfg["BackupKeepLastSaves"] == 5,
    cfg,
)
st, page = call("GET", "/web/ConfigurationPage?name=SubtitleQuickEdit", tok=None)
check("the settings page is served", st == 200 and "Subtitle Editor" in str(page), st)

# ---- listing ------------------------------------------------------------------------------------------------

st, tracks1 = call("GET", list_url(EP[1]))
check(
    "episode 1: five external srt tracks, all editable (the malformed one too)",
    st == 200 and len(tracks1) == 5 and all(t["external"] and t["format"] == "srt" and t["editable"] for t in tracks1),
    tracks1,
)
st, tracks3 = call("GET", list_url(EP[3]))
by_format = {(t["external"], t["format"], t.get("language")): t for t in tracks3}
check("episode 3 lists 5 subtitle tracks", st == 200 and len(tracks3) == 5, tracks3)
emb = [t for t in tracks3 if not t["external"]]
check("an embedded track is listed as not editable", len(emb) == 1 and not emb[0]["editable"] and "Embedded" in emb[0].get("reason"), emb)
ass = by_format.get((True, "ass", "jpn"))
vtt = by_format.get((True, "vtt", "deu"))
bad = by_format.get((True, "srt", "fra"))
good3 = by_format.get((True, "srt", "eng"))
check("an external .ass is editable (1.1)", ass and ass["editable"] and not ass.get("reason"), ass)
check("an external .vtt is editable (1.1)", vtt and vtt["editable"] and not vtt.get("reason"), vtt)
check("an unparseable .srt is not editable, with the parser's reason", bad and not bad["editable"] and bad.get("reason"), bad)
check("a good .srt next to them is editable", good3 and good3["editable"], good3)

# ---- cues ---------------------------------------------------------------------------------------------------

pl1 = track(EP[1], "pol")
orig_pl1 = read_file("TestShow - S01E01.pl.srt")
st, cues = call("GET", cues_url(EP[1], pl1["streamIndex"]))
polish_texts = [
    "Zażółć gęślą jaźń.", "Pchnąć w tę łódź jeża\nlub ośm skrzyń fig.", "Dzień dobry, wszystko w porządku?",
]
check("cues: hash is the SHA-256 of the file on disk", st == 200 and cues["hash"] == sha(orig_pl1), (st, cues if st != 200 else cues["hash"]))
check("cues: 12 cues with ms times and 1-based indexes", len(cues["cues"]) == 12 and cues["cues"][0]["index"] == 1
      and cues["cues"][0]["start"] == 2000 and cues["cues"][0]["end"] == 5000, cues["cues"][:1])
check("cues: Windows-1250 Polish text is decoded correctly, line breaks as \\n",
      [c["text"] for c in cues["cues"][:3]] == polish_texts, [c["text"] for c in cues["cues"][:3]])
check("cues: the detected encoding is reported", cues["encoding"].startswith("windows-1250") or "1250" in cues["encoding"], cues["encoding"])
check("cues of an .srt: format srt, times to the millisecond, no style, nothing read-only",
      cues.get("format") == "srt" and cues.get("timePrecisionMs") == 1
      and all("style" not in c and c.get("readOnly") is False for c in cues["cues"]), {k: cues.get(k) for k in ("format", "timePrecisionMs")})

st, r = call("GET", cues_url(EP[3], emb[0]["streamIndex"]))
check("cues of an embedded track: 400 NotEditable", st == 400 and r["code"] == "NotEditable", (st, r))
st, r = call("GET", cues_url(EP[3], ass["streamIndex"]))
check("cues of an .ass track: 200, format ass, 10 ms times, the style name",
      st == 200 and r["format"] == "ass" and r["timePrecisionMs"] == 10 and r["cues"][0]["style"] == "Default"
      and r["cues"][0]["text"] == "Styled line", (st, r))
st, r = call("GET", cues_url(EP[3], vtt["streamIndex"]))
check("cues of a .vtt track: 200, format vtt", st == 200 and r["format"] == "vtt" and r["cues"][0]["text"] == "VTT line"
      and r["cues"][0]["start"] == 1000, (st, r))
st, r = call("GET", cues_url(EP[3], bad["streamIndex"]))
check("cues of an unparseable .srt: 422 NotEditable", st == 422 and r["code"] == "NotEditable", (st, r))
st, r = call("GET", cues_url(EP[3], 99))
check("cues of a stream that does not exist: 404 StreamNotFound", st == 404 and r["code"] == "StreamNotFound", (st, r))
st, r = call("GET", cues_url("00000000000000000000000000000001", 0))
check("an unknown item id: 404 ItemNotFound", st == 404 and r["code"] == "ItemNotFound", (st, r))
st, series = call("GET", "/Items?Recursive=true&IncludeItemTypes=Series")
st, r = call("GET", list_url(series["Items"][0]["Id"]))
check("an id that is not a video (a series): 404 ItemNotFound", st == 404 and r["code"] == "ItemNotFound", (st, r))
st, r = call("GET", cues_url(EP[3], 0), tok=None)
check("no token: 401", st == 401, st)

# ---- access control -----------------------------------------------------------------------------------------

for who, tok in (("regular user", USER), ("library-restricted user", LIMITED)):
    st1, r1 = call("GET", list_url(EP[1]), tok=tok)
    st2, r2 = call("GET", cues_url(EP[1], pl1["streamIndex"]), tok=tok)
    st3, r3 = call("POST", edits_url(EP[1], pl1["streamIndex"]), {"baseHash": cues["hash"], "edits": []}, tok=tok)
    check("submissions off, %s: list, cues and edits are all 403" % who, (st1, st2, st3) == (403, 403, 403), (st1, st2, st3))
st, r = call("GET", list_url("00000000000000000000000000000001"), tok=USER)
check("submissions off: a non-admin cannot even tell whether an item exists (403, not 404)", st == 403, st)

check("settings: turn user submissions on", set_submissions(True) == 204)
st1, r1 = call("GET", list_url(EP[1]), tok=USER)
st2, r2 = call("GET", cues_url(EP[1], pl1["streamIndex"]), tok=USER)
st3, r3 = call("POST", edits_url(EP[1], pl1["streamIndex"]), {"baseHash": cues["hash"], "edits": [
    {"type": "edit", "cueIndex": 1, "oldText": polish_texts[0], "newText": "x"}]}, tok=USER)
check("submissions on, regular user: list and cues are 200, edits stay 403",
      (st1, st2, st3) == (200, 200, 403) and r2["hash"] == cues["hash"], (st1, st2, st3))
st1, r1 = call("GET", list_url(EP[1]), tok=LIMITED)
st2, r2 = call("GET", cues_url(EP[1], pl1["streamIndex"]), tok=LIMITED)
check("submissions on, user without library access: 404 ItemNotFound", (st1, st2) == (404, 404) and r1["code"] == "ItemNotFound", (st1, st2))
check("file untouched by the refused user attempts", sha(read_file("TestShow - S01E01.pl.srt")) == sha(orig_pl1))
check("settings: turn user submissions off again", set_submissions(False) == 204)
st1, _ = call("GET", cues_url(EP[1], pl1["streamIndex"]), tok=USER)
check("submissions off again: regular user is 403 again", st1 == 403, st1)

# ---- refusals leave everything untouched ---------------------------------------------------------------------

H = cues["hash"]
IDX = pl1["streamIndex"]


def post(body, item=EP[1], idx=IDX, **kw):
    return call("POST", edits_url(item, idx), body, **kw)


def refusal(name, body, status, code, extra=None, **kw):
    st, r = post(body, **kw)
    ok = st == status and isinstance(r, dict) and r.get("code") == code
    if extra:
        ok = ok and all(r.get(k) == v for k, v in extra.items())
    check("refused: " + name, ok, (st, r))


good_edit = {"type": "edit", "cueIndex": 2, "oldText": polish_texts[1], "newText": "Nowa linia"}
refusal("stale base hash -> 409 FileChanged with the current hash", {"baseHash": "0" * 64, "edits": [good_edit]}, 409, "FileChanged", {"currentHash": H})
refusal("old text no longer matches -> 409 TextMismatch naming the cue",
        {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 2, "oldText": "someone changed it", "newText": "x"}]}, 409, "TextMismatch", {"cueIndex": 2})
refusal("a Cyrillic line in a Windows-1250 file -> 422 Unrepresentable",
        {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 2, "oldText": polish_texts[1], "newText": "Привет"}]}, 422, "Unrepresentable", {"cueIndex": 2})
refusal("a blank line inside cue text -> 400 InvalidEdit",
        {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 2, "oldText": polish_texts[1], "newText": "a\n\nb"}]}, 400, "InvalidEdit")
refusal("empty new text -> 400 InvalidEdit",
        {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 2, "oldText": polish_texts[1], "newText": ""}]}, 400, "InvalidEdit")
refusal("a cue that does not exist -> 400 CueNotFound",
        {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 99, "oldText": "x", "newText": "y"}]}, 400, "CueNotFound", {"cueIndex": 99})
refusal("an added line that ends before it starts -> 400 InvalidEdit naming its clientId",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "a", "startMs": 5000, "endMs": 4000, "text": "x"}]}, 400, "InvalidEdit", {"clientId": "a"})
refusal("an added line shorter than 100 ms -> 400 InvalidEdit naming its clientId",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "a", "startMs": 5000, "endMs": 5050, "text": "x"}]}, 400, "InvalidEdit", {"clientId": "a"})
refusal("an added line past the video's runtime (60 s) -> 400 InvalidEdit naming its clientId",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "a", "startMs": 59000, "endMs": 61000, "text": "x"}]}, 400, "InvalidEdit", {"clientId": "a"})
refusal("an added line with a blank line in its text -> 400 InvalidEdit naming its clientId",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "blank", "startMs": 5000, "endMs": 6000, "text": "a\n\nb"}]}, 400, "InvalidEdit", {"clientId": "blank"})
refusal("the second of two bad added lines is the one named (the first is fine)",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "ok1", "startMs": 5000, "endMs": 6000, "text": "fine"},
                                  {"type": "add", "clientId": "bad2", "startMs": 7000, "endMs": 7010, "text": "short"}]}, 400, "InvalidEdit", {"clientId": "bad2"})
refusal("two added lines sharing a clientId -> 400 InvalidEdit naming it",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "dup", "startMs": 5000, "endMs": 6000, "text": "one"},
                                  {"type": "add", "clientId": "dup", "startMs": 7000, "endMs": 8000, "text": "two"}]}, 400, "InvalidEdit", {"clientId": "dup"})
refusal("an added line with missing fields -> 400 InvalidEdit naming its clientId",
        {"baseHash": H, "edits": [{"type": "add", "clientId": "half", "startMs": 5000}]}, 400, "InvalidEdit", {"clientId": "half"})
refusal("an added line without a clientId -> 400 InvalidEdit, clientId null",
        {"baseHash": H, "edits": [{"type": "add", "startMs": 5000, "endMs": 6000, "text": "x"}]}, 400, "InvalidEdit", {"clientId": None})
refusal("an invalid edit of an existing cue -> 400 InvalidEdit, clientId null",
        {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 2, "oldText": polish_texts[1], "newText": "a\n\nb"}]}, 400, "InvalidEdit", {"clientId": None})
refusal("a stale base hash -> 409 FileChanged, clientId null",
        {"baseHash": "0" * 64, "edits": [{"type": "add", "clientId": "late", "startMs": 5000, "endMs": 6000, "text": "x"}]}, 409, "FileChanged", {"clientId": None})
refusal("an unknown edit type -> 400 InvalidEdit", {"baseHash": H, "edits": [{"type": "delete", "cueIndex": 1}]}, 400, "InvalidEdit")
refusal("an edit with missing fields -> 400 InvalidEdit", {"baseHash": H, "edits": [{"type": "edit", "cueIndex": 1}]}, 400, "InvalidEdit")
refusal("no edits -> 400 InvalidEdit", {"baseHash": H, "edits": []}, 400, "InvalidEdit")
refusal("no baseHash -> 400 InvalidRequest", {"edits": [good_edit]}, 400, "InvalidRequest")
refusal("the same cue edited twice -> 400 InvalidEdit", {"baseHash": H, "edits": [good_edit, good_edit]}, 400, "InvalidEdit")
refusal("an embedded track -> 400 NotEditable", {"baseHash": H, "edits": [good_edit]}, 400, "NotEditable", item=EP[3], idx=emb[0]["streamIndex"])
refusal("an unparseable .srt -> 422 NotEditable", {"baseHash": H, "edits": [good_edit]}, 422, "NotEditable", item=EP[3], idx=bad["streamIndex"])
refusal("a stream index that does not exist -> 404 StreamNotFound", {"baseHash": H, "edits": [good_edit]}, 404, "StreamNotFound", idx=99)
refusal("an unknown item -> 404 ItemNotFound", {"baseHash": H, "edits": [good_edit]}, 404, "ItemNotFound", item="00000000000000000000000000000001")
def oversize_status():
    """Announce a 1.3 MB body, send only the first 64 KB, and read the status line the server answers with."""
    import socket
    host_, port_ = BASE.replace("http://", "").split(":")
    body_head = b'{"baseHash":"' + b"x" * 65000
    head = ("POST %s HTTP/1.1\r\nHost: %s\r\nContent-Type: application/json\r\nContent-Length: 1300000\r\n"
            "Authorization: MediaBrowser Client=\"s2\", Device=\"sqe-tests\", DeviceId=\"stage2-tests\", Version=\"1.0\", Token=\"%s\"\r\n\r\n"
            % (edits_url(EP[1], IDX), BASE.replace("http://", ""), ADMIN)).encode()
    sock = socket.create_connection((host_, int(port_)), timeout=30)
    try:
        sock.sendall(head + body_head)
        return sock.recv(200).split(b"\r\n")[0].decode()
    finally:
        sock.close()


status_line = oversize_status()
check("a request over 1 MB is refused with 413", " 413 " in status_line, status_line)
st, r = post(None, raw=b"this is not json", ctype="application/json")
check("a body that is not JSON is a 400", st == 400, st)
check("none of the refusals changed the file, left a temp file or made a backup",
      sha(read_file("TestShow - S01E01.pl.srt")) == sha(orig_pl1) and not writer_files() and not backups(),
      (writer_files(), backups()))

# ---- a real save: edit only, byte-exact, in-place path (group 1000 is not the Jellyfin user's) ------------------------

en2 = track(EP[2], "eng")
orig_en2 = read_file("TestShow - S01E02.en.srt")
before = stat_file("TestShow - S01E02.en.srt")
st, c2 = call("GET", cues_url(EP[2], en2["streamIndex"]))
old_text = "This is a two-line cue\nwith a line break."
new_text = "Fixed two-line cue\nwith ünïcode"
check("episode 2 english: CRLF file, cue 2 is the two-line cue", c2["cues"][1]["text"] == old_text, c2["cues"][1])
st, r = call("POST", edits_url(EP[2], en2["streamIndex"]), {
    "baseHash": c2["hash"], "edits": [{"type": "edit", "cueIndex": 2, "oldText": old_text, "newText": new_text}]})
after_bytes = read_file("TestShow - S01E02.en.srt")
expected = orig_en2.replace(old_text.replace("\n", "\r\n").encode(), new_text.replace("\n", "\r\n").encode(), 1)
after = stat_file("TestShow - S01E02.en.srt")
check("save: 200 with hash, saved cue index and write method", st == 200 and r["savedCueIndexes"] == [2] and r["hash"] == sha(after_bytes), (st, r))
check("save: the file is byte-for-byte the original with only that cue's text replaced (CRLF kept)", after_bytes == expected and after_bytes != orig_en2)
check("save: the group-mismatch fallback was used (in place, same inode), owner/group/mode untouched",
      r["writeMethod"] == "InPlaceCopy" and after == before, (r.get("writeMethod"), before, after))
check("save: no temp or recovery file is left behind", not writer_files(), writer_files())
bk = backups()
bk_en2 = [b for b in bk if b.startswith(EP[2] + "_%d_" % en2["streamIndex"])]
check("save: the first save made one original backup, named item_stream_pathid_time_original.srt, with the exact old bytes",
      len(bk_en2) == 1 and re.fullmatch(EP[2] + r"_%d_[0-9a-f]{8}_\d{8}T\d{9}Z_original\.srt" % en2["streamIndex"], bk_en2[0])
      and read_backup(bk_en2[0]) == orig_en2, bk_en2)
st, js = call("GET", "/Videos/%s/%s/Subtitles/%d/0/Stream.js" % (EP[2], EP[2], en2["streamIndex"]))
texts = [e["Text"] for e in js["TrackEvents"]]
check("save: Jellyfin serves the new text at once, without a refresh or restart", any("Fixed two-line cue" in t for t in texts), texts[:3])
st, c2b = call("GET", cues_url(EP[2], en2["streamIndex"]))
check("save: the Cues call now returns the new hash and text", c2b["hash"] == r["hash"] and c2b["cues"][1]["text"] == new_text, c2b["cues"][1])

st, r2 = call("POST", edits_url(EP[2], en2["streamIndex"]), {
    "baseHash": r["hash"], "edits": [{"type": "edit", "cueIndex": 1, "oldText": "Hello there.", "newText": "Hello again."}]})
check("a second save from the new hash works and makes no second original backup",
      st == 200 and len([b for b in backups() if b.startswith(EP[2] + "_%d_" % en2["streamIndex"])]) == 1, (st, r2))
st, r3 = call("POST", edits_url(EP[2], en2["streamIndex"]), {
    "baseHash": r["hash"], "edits": [{"type": "edit", "cueIndex": 1, "oldText": "Hello there.", "newText": "late"}]})
check("saving again from the old hash is a 409 FileChanged", st == 409 and r3["code"] == "FileChanged" and r3["currentHash"] == r2["hash"], (st, r3))

# ---- adding a line: UTF-8 BOM + CRLF file --------------------------------------------------------------------------

de1 = track(EP[1], "deu")
orig_de1 = read_file("TestShow - S01E01.de.srt")
st, cd = call("GET", cues_url(EP[1], de1["streamIndex"]))
st, r = call("POST", edits_url(EP[1], de1["streamIndex"]), {
    "baseHash": cd["hash"], "edits": [{"type": "add", "clientId": "n1", "startMs": 30000, "endMs": 31500, "text": "Neue Zeile ß\nzweite Zeile"}]})
new_de1 = read_file("TestShow - S01E01.de.srt")
check("add: 200 and the new cue is position 8 (after the cue starting at 29 s)", st == 200 and r["addedCues"] == {"n1": 8} and r["savedCueIndexes"] == [8], (st, r))
check("add: BOM kept, CRLF kept, and all 13 cues renumbered 1..13",
      new_de1[:3] == b"\xef\xbb\xbf" and b"\r\n" in new_de1 and b"\n" not in new_de1.replace(b"\r\n", b"")
      and re.findall(rb"(?m)^(\d+)\r$", new_de1[3:]) == [str(i).encode() for i in range(1, 14)], re.findall(rb"(?m)^(\d+)\r$", new_de1[3:]))
old_lines = [l for l in orig_de1.decode("utf-8-sig").split("\r\n") if l and not l.isdigit()]
new_lines = [l for l in new_de1.decode("utf-8-sig").split("\r\n") if l and not l.isdigit()]
added = ["00:00:30,000 --> 00:00:31,500", "Neue Zeile ß", "zweite Zeile"]
check("add: every original time line and text line is still there, unchanged and in order; only the new block is extra",
      [l for l in new_lines if l not in added] == old_lines, None)
st, cd2 = call("GET", cues_url(EP[1], de1["streamIndex"]))
check("add: cues now has 13 entries with the new one at index 8", len(cd2["cues"]) == 13 and cd2["cues"][7]["text"] == "Neue Zeile ß\nzweite Zeile"
      and cd2["cues"][7]["start"] == 30000 and cd2["hash"] == r["hash"], cd2["cues"][7])

# ---- the atomic-rename path (episode 3's files belong to the Jellyfin user's own group) ---------------------------------

orig_e3 = read_file("TestShow - S01E03.en.srt")
before3 = stat_file("TestShow - S01E03.en.srt")
st, c3 = call("GET", cues_url(EP[3], good3["streamIndex"]))
st, r = call("POST", edits_url(EP[3], good3["streamIndex"]), {
    "baseHash": c3["hash"], "edits": [{"type": "edit", "cueIndex": 1, "oldText": "Good line one", "newText": "Better line one"}]})
after3 = stat_file("TestShow - S01E03.en.srt")
check("atomic path: matching group -> renamed over the original (new inode), owner, group and mode kept",
      st == 200 and r["writeMethod"] == "AtomicRename" and after3["inode"] != before3["inode"]
      and (after3["uid"], after3["gid"], after3["mode"]) == (before3["uid"], before3["gid"], before3["mode"]), (st, r, before3, after3))
check("atomic path: content is exactly the original with that text replaced",
      read_file("TestShow - S01E03.en.srt") == orig_e3.replace(b"Good line one", b"Better line one"))

# ---- the folder is not writable ----------------------------------------------------------------------------------

it2 = track(EP[2], "ita")
st, ci = call("GET", cues_url(EP[2], it2["streamIndex"]))
orig_it2 = read_file("TestShow - S01E02.it.srt")
host("chmod", "555", HOST_MEDIA)
try:
    st, r = call("POST", edits_url(EP[2], it2["streamIndex"]), {
        "baseHash": ci["hash"], "edits": [{"type": "edit", "cueIndex": 1, "oldText": "Hello there.", "newText": "x"}]})
finally:
    host("chmod", "755", HOST_MEDIA)
check("unwritable folder: a clear 500 NotWritable that names the folder and the user, not a generic error",
      st == 500 and r["code"] == "NotWritable" and "Season 01" in r["message"] and "not writable" in r["message"], (st, r))
check("unwritable folder: the file is unchanged and nothing is left behind", read_file("TestShow - S01E02.it.srt") == orig_it2 and not writer_files())

# ---- simultaneous saves -------------------------------------------------------------------------------------------

statuses = []


def racer(text):
    s, _ = call("POST", edits_url(EP[2], it2["streamIndex"]), {
        "baseHash": ci["hash"], "edits": [{"type": "edit", "cueIndex": 1, "oldText": "Hello there.", "newText": text}]})
    statuses.append(s)


threads = [threading.Thread(target=racer, args=("racer %d" % i,)) for i in range(4)]
[t.start() for t in threads]
[t.join() for t in threads]
check("four simultaneous saves from the same hash: exactly one 200, the rest 409", sorted(statuses) == [200, 409, 409, 409], statuses)
check("...and the file holds exactly one winner's text, intact", re.search(rb"racer \d", read_file("TestShow - S01E02.it.srt")) is not None
      and not writer_files(), None)

# ---- log hygiene --------------------------------------------------------------------------------------------------

new_log = plugin_log()
before_set = set(LOG_BEFORE)
fresh = [l for l in new_log if l not in before_set]
errs = [l for l in fresh if "[ERR]" in l and "SubtitleQuickEdit" in l]
check("the server log gained exactly one plugin error: the deliberate NotWritable save", len(errs) == 1 and "NotWritable" in errs[0], errs)
saves = [l for l in fresh if "saved 1 edit(s) to subtitle stream" in l]
check("the log records the five successful saves", len(saves) == 5, len(saves))

subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)  # leave the fixtures pristine for the next user
failed = [n for n, ok, _ in results if not ok]
print("\n%d checks, %d passed, %d failed" % (len(results), len(results) - len(failed), len(failed)))
sys.exit(1 if failed else 0)
