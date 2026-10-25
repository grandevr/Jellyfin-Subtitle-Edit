#!/usr/bin/env python3
"""Stage 5 integration tests: WebVTT and ASS/SSA editing through the plugin's API, against the THROWAWAY test Jellyfin.
Needs JF_BASE, SQE_HOST_EXEC and SQE_TEST_ROOT (see devtools/env.sh); SQE_LOG_CMD is optional.

Usage: python3 -I testdata/stage5_api_tests.py <secrets-dir>
Needs token and testuser.token in <secrets-dir>. Resets the fixtures and the plugin settings first and last, and in
between writes a richer .vtt and .ass over episode 3's "de.vtt" and "ja.ass" (same names, so Jellyfin's track list
does not change). Prints PASS/FAIL per check; exit code 1 on any failure.
Never touches the live Jellyfin: every URL and path below is the test server's.
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
VTT_NAME = "TestShow - S01E03.de.vtt"
ASS_NAME = "TestShow - S01E03.ja.ass"

results = []


def token(name):
    return open(os.path.join(SECRETS, name)).read()


ADMIN, USER = token("token"), token("testuser.token")


def call(method, path, body=None, tok=ADMIN):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    auth = 'MediaBrowser Client="s5", Device="ct111", DeviceId="stage5-tests", Version="1.0"'
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


def sub(item, idx, part):
    return "/SubtitleQuickEdit/Items/%s/Subtitles/%s/%s" % (item, idx, part)


def cues(idx, tok=ADMIN):
    return call("GET", sub(EP3, idx, "Cues"), tok=tok)


def edit(cue_index, old, new):
    return {"type": "edit", "cueIndex": cue_index, "oldText": old, "newText": new}


def add(client_id, start, end, text):
    return {"type": "add", "clientId": client_id, "startMs": start, "endMs": end, "text": text}


def refused(name, idx, body, status, code):
    st, r = call("POST", sub(EP3, idx, "Edits"), body)
    check("refused: " + name, st == status and isinstance(r, dict) and r.get("code") == code, (st, r))


# LF, a header with a setting, NOTE, STYLE and REGION blocks, a cue id, cue settings, tags, and mm:ss times.
VTT = (
    "WEBVTT - stage 5 fixture\n"
    "Kind: captions\n"
    "\n"
    "NOTE kept as it is\n"
    "\n"
    "STYLE\n"
    "::cue { color: yellow }\n"
    "\n"
    "REGION\n"
    "id:top width:40%\n"
    "\n"
    "intro\n"
    "00:01.000 --> 00:03.000 align:start line:0\n"
    "VTT line\n"
    "\n"
    "00:00:04.000 --> 00:00:06.000\n"
    "<i>Styled VTT line</i>\n"
    "\n"
    "00:00:07.000 --> 00:00:09.000\n"
    "<v Anna>Anna speaks</v>\n"
    "two lines\n"
).encode("utf-8")

# UTF-8 with a BOM and CRLF, styles, a Comment line, override tags, commas in the text, \N, a drawing, a [Fonts]-like
# section after [Events].
ASS = (
    "﻿[Script Info]\r\n"
    "; stage 5 fixture\r\n"
    "ScriptType: v4.00+\r\n"
    "PlayResX: 640\r\n"
    "PlayResY: 360\r\n"
    "\r\n"
    "[V4+ Styles]\r\n"
    "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, "
    "StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n"
    "Style: Default,Arial,24,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\r\n"
    "Style: Sign,Arial,20,&H0000FFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,8,10,10,10,1\r\n"
    "\r\n"
    "[Events]\r\n"
    "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n"
    "Comment: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,not a cue\r\n"
    "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Plain ASS line\r\n"
    "Dialogue: 0,0:00:04.00,0:00:06.00,Sign,Anna,0,0,0,,{\\an8\\c&H00FFFF&}Top sign, with a comma\r\n"
    "Dialogue: 0,0:00:07.00,0:00:09.00,Default,,0,0,0,,Two lines\\Nin one cue\r\n"
    "Dialogue: 1,0:00:10.00,0:00:12.00,Sign,,0,0,0,,{\\p1}m 0 0 l 100 0 100 100 0 100{\\p0}\r\n"
    "\r\n"
    "[Aegisub Project Garbage]\r\n"
    "Last Style Storage: Default\r\n"
).encode("utf-8")

LOG_BEFORE = plugin_log()

# ---- setup -----------------------------------------------------------------------------------------------------

subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
check("settings: start from the defaults", configure(**DEFAULTS) == 204)
st, items = call("GET", "/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path")
EP3 = [it["Id"] for it in items["Items"] if "S01E03" in it["Path"]][0]
st, ep3 = call("GET", "/Items?Ids=%s&Fields=RunTimeTicks" % EP3)
RUNTIME_MS = (ep3["Items"][0].get("RunTimeTicks") or 0) // 10000
# Episode 3's video is about 3 s long (make_ep3.sh cuts it to its embedded subtitle), and added lines must end by then.
check("episode 3 runs at least 2.95 s, so the added lines below fit", RUNTIME_MS >= 2950, RUNTIME_MS)
write_fixture(VTT_NAME, VTT)
write_fixture(ASS_NAME, ASS)
st, tracks = call("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % EP3)
VI = next(t["streamIndex"] for t in tracks if t["external"] and t["format"] == "vtt")
AI = next(t["streamIndex"] for t in tracks if t["external"] and t["format"] == "ass")
check("the richer fixtures are in place", read_file(VTT_NAME) == VTT and read_file(ASS_NAME) == ASS)

# ---- WebVTT ------------------------------------------------------------------------------------------------------

st, c = cues(VI)
check("vtt cues: format vtt, 1 ms times, utf-8", st == 200 and c["format"] == "vtt" and c["timePrecisionMs"] == 1
      and c["encoding"].startswith("utf-8") and c["hash"] == sha(VTT), (st, c))
check("vtt cues: three cues, the NOTE, STYLE and REGION blocks are not cues, mm:ss times read",
      st == 200 and [(q["start"], q["end"], q["text"]) for q in c["cues"]] == [
          (1000, 3000, "VTT line"), (4000, 6000, "<i>Styled VTT line</i>"), (7000, 9000, "<v Anna>Anna speaks</v>\ntwo lines")],
      c.get("cues") if isinstance(c, dict) else c)
H = c["hash"]
refused("vtt: '-->' in the text -> 400 InvalidEdit", VI, {"baseHash": H, "edits": [edit(1, "VTT line", "a --> b")]}, 400, "InvalidEdit")
refused("vtt: a blank line in the text -> 400 InvalidEdit", VI, {"baseHash": H, "edits": [edit(1, "VTT line", "a\n\nb")]}, 400, "InvalidEdit")
refused("vtt: an added line with '-->' -> 400 InvalidEdit", VI, {"baseHash": H, "edits": [add("x", 2000, 2900, "-->")]}, 400, "InvalidEdit")
check("vtt: the refusals changed nothing and made no backup", read_file(VTT_NAME) == VTT and not backups(), backups())

st, r = call("POST", sub(EP3, VI, "Edits"), {"baseHash": H, "keepRestorePoint": True, "edits": [
    edit(1, "VTT line", "VTT line, fixed"), edit(2, "<i>Styled VTT line</i>", "<i>Styled line, fixed</i>"),
    add("v1", 2000, 2900, "Added VTT line")]})
want = (VTT.replace(b"\nVTT line\n", b"\nVTT line, fixed\n")
        .replace(b"<i>Styled VTT line</i>", b"<i>Styled line, fixed</i>")
        .replace(b"\n\n00:00:04.000", b"\n\n00:00:02.000 --> 00:00:02.900\nAdded VTT line\n\n00:00:04.000"))
now = read_file(VTT_NAME)
check("vtt save: 200, only the edited text changes and the new cue goes before the next later cue (exact bytes)",
      st == 200 and now == want and r["hash"] == sha(want), (st, r, now[-120:]))
check("vtt save: the header, NOTE, STYLE, REGION, the cue id and its settings are kept",
      now.startswith(b"WEBVTT - stage 5 fixture\nKind: captions\n\nNOTE kept as it is\n\nSTYLE\n")
      and b"\nintro\n00:01.000 --> 00:03.000 align:start line:0\n" in now)
st, c2 = cues(VI)
check("vtt save: the cues read back with the new line second",
      [q["text"] for q in c2["cues"]] == ["VTT line, fixed", "Added VTT line", "<i>Styled line, fixed</i>",
                                          "<v Anna>Anna speaks</v>\ntwo lines"] and c2["cues"][1]["start"] == 2000, c2)
vtt_backups = [b for b in backups() if b.endswith(".vtt")]
check("vtt save: the original and a restore point are backed up with a .vtt extension",
      any(b.endswith("_original.vtt") for b in vtt_backups) and any(b.endswith("_restore.vtt") for b in vtt_backups)
      and all(b.endswith(".vtt") for b in backups()), backups())
st, r = call("POST", sub(EP3, VI, "Restore"), {"baseHash": c2["hash"], "targetHash": sha(VTT)})
check("vtt restore: back to the original bytes", st == 200 and r["restored"] and read_file(VTT_NAME) == VTT, (st, r))

# ---- ASS/SSA -----------------------------------------------------------------------------------------------------

st, c = cues(AI)
check("ass cues: format ass, 10 ms times, utf-8", st == 200 and c["format"] == "ass" and c["timePrecisionMs"] == 10
      and c["encoding"].startswith("utf-8") and c["hash"] == sha(ASS), (st, c))
got = [(q["start"], q["end"], q.get("style"), q["readOnly"], q["text"]) for q in c["cues"]] if st == 200 else c
check("ass cues: the Comment line is not a cue; styles, \\N as a line break, the drawing read-only",
      got == [(1000, 3000, "Default", False, "Plain ASS line"),
              (4000, 6000, "Sign", False, "{\\an8\\c&H00FFFF&}Top sign, with a comma"),
              (7000, 9000, "Default", False, "Two lines\nin one cue"),
              (10000, 12000, "Sign", True, "{\\p1}m 0 0 l 100 0 100 100 0 100{\\p0}")], got)
H = c["hash"]
drawing = c["cues"][3]["text"]
refused("ass: editing a drawing -> 400 InvalidEdit", AI, {"baseHash": H, "edits": [edit(4, drawing, "x")]}, 400, "InvalidEdit")
refused("ass: a control character -> 400 InvalidEdit", AI, {"baseHash": H, "edits": [edit(1, "Plain ASS line", "a\u0007b")]}, 400, "InvalidEdit")
refused("ass: an added line past the end of the video -> 400 InvalidEdit", AI,
        {"baseHash": H, "edits": [add("late", RUNTIME_MS - 500, RUNTIME_MS + 500, "x")]}, 400, "InvalidEdit")
check("ass: the refusals changed nothing", read_file(ASS_NAME) == ASS)

st, r = call("POST", sub(EP3, AI, "Edits"), {"baseHash": H, "edits": [
    edit(2, "{\\an8\\c&H00FFFF&}Top sign, with a comma", "{\\an8\\c&H00FFFF&}Top sign, fixed"),
    edit(3, "Two lines\nin one cue", "Two lines\nin one cue\n\nand a gap"),
    add("a1", 2504, 2906, "Added, with a comma\nand a break")]})
want = (ASS.replace(b"}Top sign, with a comma\r\n", b"}Top sign, fixed\r\n")
        .replace(b",,Two lines\\Nin one cue\r\n", b",,Two lines\\Nin one cue\\N\\Nand a gap\r\n")
        .replace(b",,Plain ASS line\r\n",
                 b",,Plain ASS line\r\nDialogue: 0,0:00:02.50,0:00:02.91,Default,,0,0,0,,Added, with a comma\\Nand a break\r\n"))
now = read_file(ASS_NAME)
check("ass save: 200, only the Text fields change, the BOM and CRLF are kept, the new line follows the one before it "
      "with its fields and rounded times (exact bytes)", st == 200 and now == want and r["hash"] == sha(want), (st, r, now[-300:]))
st, c2 = cues(AI)
check("ass save: the new line reads back at 2.50-2.91 s, between lines 1 and 2",
      st == 200 and [(q["start"], q["end"], q["text"]) for q in c2["cues"]][1] == (2500, 2910, "Added, with a comma\nand a break")
      and len(c2["cues"]) == 5, c2)
check("ass save: the original is backed up with an .ass extension, byte for byte",
      any(b.endswith("_original.ass") for b in backups()), backups())
orig = [b for b in backups() if b.endswith("_original.ass")]
check("ass save: the backup holds the original bytes", orig and host("cat", HOST_BACKUPS + "/" + orig[0]) == ASS)

# A submission on an ASS track, approved by the admin.
check("settings: user submissions on", configure(AllowUserSubmissions=True) == 204)
st, c3 = cues(AI, tok=USER)
st, s1 = call("POST", sub(EP3, AI, "Submissions"), {"baseHash": c3["hash"], "note": "ASS fix",
                                                    "edits": [edit(1, "Plain ASS line", "Plain line, from a user")]}, tok=USER)
check("ass submit: a user submits a change (200, Pending), the file is unchanged",
      st == 200 and s1["state"] == "Pending" and read_file(ASS_NAME) == want, (st, s1))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s1["id"], {})
now = read_file(ASS_NAME)
check("ass approve: applied, and only that Text field changes",
      st == 200 and r["applied"] and now == want.replace(b",,Plain ASS line\r\n", b",,Plain line, from a user\r\n"), (st, r))
st, c4 = cues(AI)
st, r = call("POST", sub(EP3, AI, "Restore"), {"baseHash": c4["hash"], "targetHash": sha(ASS)})
check("ass restore: back to the original bytes", st == 200 and r["restored"] and read_file(ASS_NAME) == ASS, (st, r))

# ---- log and clean up --------------------------------------------------------------------------------------------

time.sleep(1)
fresh = [line for line in plugin_log() if line not in set(LOG_BEFORE)]
errs = [line for line in fresh if "[ERR]" in line and "SubtitleQuickEdit" in line]
check("log: no plugin errors", not errs, errs)

configure(**DEFAULTS)
subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
failed = [n for n, ok, _ in results if not ok]
print("\n%d checks, %d passed, %d failed" % (len(results), len(results) - len(failed), len(failed)))
sys.exit(1 if failed else 0)
