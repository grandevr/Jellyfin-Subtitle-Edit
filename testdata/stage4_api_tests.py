#!/usr/bin/env python3
"""Stage 4 integration tests: user edits, submissions and their review, against the THROWAWAY test Jellyfin.
Needs JF_BASE, SQE_HOST_EXEC and SQE_TEST_ROOT (see devtools/env.sh); SQE_LOG_CMD is optional.

Usage: python3 -I testdata/stage4_api_tests.py <secrets-dir>
Needs token, testuser.token and limiteduser.token in <secrets-dir>. testuser can see the test library; limiteduser
cannot. Resets the fixtures, the submissions and the plugin settings first and last. Prints PASS/FAIL per check;
exit code 1 on any failure. Never touches the live Jellyfin: every URL and path below is the test server's.
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
HOST_DATA = TEST_ROOT + "/config/data/SubtitleQuickEdit"
HERE = os.path.dirname(os.path.abspath(__file__))
SECRETS = sys.argv[1]

results = []


def token(name):
    return open(os.path.join(SECRETS, name)).read()


ADMIN, USER, LIMITED = token("token"), token("testuser.token"), token("limiteduser.token")


def call(method, path, body=None, tok=ADMIN, raw=None):
    data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    auth = 'MediaBrowser Client="s4", Device="ct111", DeviceId="stage4-tests", Version="1.0"'
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


def listing(folder):
    try:
        return [n for n in host("ls", "-A", folder).decode().split("\n") if n]
    except RuntimeError:
        return []


def sha(data):
    return hashlib.sha256(data).hexdigest()


def check(name, condition, detail=""):
    results.append((name, bool(condition), detail))
    print(("PASS  " if condition else "FAIL  ") + name + ("" if condition else "   <-- " + str(detail)))


DEFAULTS = {"AllowUserSubmissions": False, "UserEdits": "Off", "UserEditUserIds": [], "MaxPendingSubmissionsPerUser": 50}


def configure(**settings):
    st, cfg = call("GET", "/Plugins/%s/Configuration" % PLUGIN_ID)
    cfg.update(settings)
    return call("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, cfg)[0]


def sub(item, idx, part):
    return "/SubtitleQuickEdit/Items/%s/Subtitles/%s/%s" % (item, idx, part)


def me(tok):
    return call("GET", "/SubtitleQuickEdit/Me", tok=tok)[1]


def cues(item, idx, tok=ADMIN):
    return call("GET", sub(item, idx, "Cues"), tok=tok)


def edit(cue_index, old, new):
    return {"type": "edit", "cueIndex": cue_index, "oldText": old, "newText": new}


LOG_BEFORE = plugin_log()

# ---- setup -----------------------------------------------------------------------------------------------

subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
check("settings: start from the defaults", configure(**DEFAULTS) == 204)
st, items = call("GET", "/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path")
EP = {int(re.search(r"S01E0(\d)", it["Path"]).group(1)): it["Id"] for it in items["Items"]}
st, users = call("GET", "/Users")
UID = {u["Name"]: u["Id"] for u in users}
check("the test users exist", "testuser" in UID and "limiteduser" in UID, sorted(UID))


def track(item, *langs):
    st, tracks = call("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % item)
    return next(t["streamIndex"] for t in tracks if t.get("language") in langs)


EN2, PL2, DE2, CS2 = track(EP[2], "eng"), track(EP[2], "pol"), track(EP[2], "deu", "ger"), track(EP[2], "ces", "cze")
FILE = {EN2: "TestShow - S01E02.en.srt", PL2: "TestShow - S01E02.pl.srt", DE2: "TestShow - S01E02.de.srt",
        CS2: "TestShow - S01E02.cs.srt"}

# ---- Me ----------------------------------------------------------------------------------------------------

m = me(ADMIN)
check("Me, admin: isAdmin and canEdit, not canSubmit, with a pending count",
      m["isAdmin"] and m["canEdit"] and not m["canSubmit"] and m["pendingReviews"] == 0 and m["maxNoteLength"] == 500, m)
m = me(USER)
check("Me, user with everything off: no rights, no pending count",
      not m["isAdmin"] and not m["canEdit"] and not m["canSubmit"] and m["pendingReviews"] is None, m)
st, _ = call("GET", "/SubtitleQuickEdit/Me", tok=None)
check("Me without a token: 401", st == 401, st)

# ---- allow user edits: all users -------------------------------------------------------------------------------

check("settings: user edits for all users", configure(UserEdits="AllUsers") == 204)
m = me(USER)
check("Me, user: canEdit, not canSubmit, not admin", m["canEdit"] and not m["canSubmit"] and not m["isAdmin"], m)
st, c = cues(EP[2], PL2, tok=USER)
orig_pl2 = read_file(FILE[PL2])
st, r = call("POST", sub(EP[2], PL2, "Edits"), {"baseHash": c["hash"], "keepRestorePoint": True,
                                                "edits": [edit(1, c["cues"][0]["text"], "Edited by a user")]}, tok=USER)
check("user edits on: a user saves directly (200), and the file changes", st == 200 and b"Edited by a user" in read_file(FILE[PL2]), (st, r))
st, r2 = call("POST", sub(EP[2], PL2, "Restore"), {"baseHash": r["hash"], "targetHash": c["hash"]}, tok=USER)
check("user edits on: the user can revert to the original too", st == 200 and r2["restored"] and read_file(FILE[PL2]) == orig_pl2, (st, r2))
st, r = call("POST", sub(EP[2], PL2, "Submissions"), {"baseHash": c["hash"], "edits": [edit(1, c["cues"][0]["text"], "x")]}, tok=USER)
check("user edits on: a user who can save directly cannot submit (403)", st == 403 and r["code"] == "Forbidden", (st, r))
st, r = cues(EP[2], PL2, tok=LIMITED)
check("user edits on: a user without library access still gets 404 ItemNotFound", st == 404 and r["code"] == "ItemNotFound", (st, r))
st, _ = call("GET", "/SubtitleQuickEdit/Submissions", tok=USER)
check("user edits on: the review list stays admin-only (403)", st == 403, st)
st, cfg = call("GET", "/Plugins/%s/Configuration" % PLUGIN_ID)
st2 = call("POST", "/Plugins/%s/Configuration" % PLUGIN_ID, cfg, tok=USER)[0]
check("user edits on: a user still cannot change the plugin settings (403)", st2 == 403, st2)

# ---- allow user edits: selected users --------------------------------------------------------------------------

check("settings: user edits for limiteduser only", configure(UserEdits="SelectedUsers", UserEditUserIds=[UID["limiteduser"]]) == 204)
st, c = cues(EP[2], PL2)
st, r = call("POST", sub(EP[2], PL2, "Edits"), {"baseHash": c["hash"], "edits": [edit(1, c["cues"][0]["text"], "x")]}, tok=USER)
check("selected users: a user not on the list is refused (403)", st == 403 and me(USER)["canEdit"] is False, (st, r))
check("selected users: the listed user has canEdit", me(LIMITED)["canEdit"] is True, me(LIMITED))
check("settings: user edits for testuser only", configure(UserEditUserIds=[UID["testuser"]]) == 204)
st, r = call("POST", sub(EP[2], PL2, "Edits"), {"baseHash": c["hash"], "edits": [edit(1, c["cues"][0]["text"], "Listed user")]}, tok=USER)
check("selected users: a listed user saves directly (200)", st == 200, (st, r))
check("settings: submissions on as well", configure(AllowUserSubmissions=True) == 204)
check("selected users + submissions: the listed user edits, the other one submits",
      me(USER)["canEdit"] and not me(USER)["canSubmit"] and me(LIMITED)["canSubmit"] and not me(LIMITED)["canEdit"], (me(USER), me(LIMITED)))
check("settings: user edits off, submissions on", configure(UserEdits="Off", UserEditUserIds=[]) == 204)
st, r = call("POST", sub(EP[2], PL2, "Edits"), {"baseHash": c["hash"], "edits": [edit(1, c["cues"][0]["text"], "x")]}, tok=USER)
check("user edits off again: the user's save is refused (403) with a hint to submit", st == 403 and "submit" in r["message"], (st, r))

# ---- submitting --------------------------------------------------------------------------------------------

m = me(USER)
check("Me, user with submissions on: canSubmit only", m["canSubmit"] and not m["canEdit"] and not m["isAdmin"], m)
st, c = cues(EP[2], EN2, tok=USER)
H, T = c["hash"], [q["text"] for q in c["cues"]]
orig_en2 = read_file(FILE[EN2])
st, s1 = call("POST", sub(EP[2], EN2, "Submissions"), {
    "baseHash": H, "note": "Typo fixes",
    "edits": [edit(2, T[1], "A better two-line cue\nwith a fix."), {"type": "add", "clientId": "n1", "startMs": 60, "endMs": 1500, "text": "New first line"}]},
    tok=USER)
check("submit: 200, pending, with the note, the user, the item name and the stream",
      st == 200 and s1["state"] == "Pending" and s1["note"] == "Typo fixes" and s1["userName"] == "testuser"
      and "S01E02" in (s1.get("itemName") or "") and s1["streamIndex"] == EN2 and s1["baseHash"] == H, (st, s1))
check("submit: the changes are stored with the old text and start time",
      st == 200 and s1["edits"][0] == {**s1["edits"][0], "type": "edit", "cueIndex": 2, "oldText": T[1], "cueStart": c["cues"][1]["start"],
                                       "outcome": "Pending"}
      and s1["edits"][1]["type"] == "add" and s1["edits"][1]["text"] == "New first line", s1.get("edits") if isinstance(s1, dict) else s1)
check("submit: the file is not changed", read_file(FILE[EN2]) == orig_en2)
check("submit: one submission file is stored, no temp file", len(listing(HOST_DATA + "/submissions")) == 1, listing(HOST_DATA + "/submissions"))
st, log = call("GET", "/System/ActivityLog/Entries?startIndex=0&limit=20")
entries = [e for e in log.get("Items", []) if e.get("Type") == "SubtitleQuickEditSubmission"]
check("submit: Jellyfin's activity log has an entry naming the user", entries and "testuser" in entries[0]["Name"], entries[:1])
check("Me, admin: pendingReviews is now 1", me(ADMIN)["pendingReviews"] == 1, me(ADMIN))
st, mine = call("GET", "/SubtitleQuickEdit/Submissions/Mine", tok=USER)
check("Mine: the user sees their submission", st == 200 and [x["id"] for x in mine] == [s1["id"]], (st, mine))
st, mine = call("GET", "/SubtitleQuickEdit/Submissions/Mine", tok=LIMITED)
check("Mine: another user sees none of it", st == 200 and mine == [], (st, mine))


def submit(body, item=EP[2], idx=EN2, tok=USER):
    return call("POST", sub(item, idx, "Submissions"), body, tok=tok)


def refused(name, resp, status, code):
    st, r = resp
    check("submit refused: " + name, st == status and isinstance(r, dict) and r.get("code") == code, (st, r))


refused("stale hash -> 409 FileChanged", submit({"baseHash": "0" * 64, "edits": [edit(1, T[0], "x")]}), 409, "FileChanged")
refused("old text no longer matches -> 409 TextMismatch", submit({"baseHash": H, "edits": [edit(1, "not it", "x")]}), 409, "TextMismatch")
refused("a 501-character note -> 400 TextTooLong", submit({"baseHash": H, "note": "n" * 501, "edits": [edit(1, T[0], "x")]}), 400, "TextTooLong")
refused("no changes -> 400 InvalidEdit", submit({"baseHash": H, "edits": []}), 400, "InvalidEdit")
refused("an admin submits -> 403 (admins save directly)", submit({"baseHash": H, "edits": [edit(1, T[0], "x")]}, tok=ADMIN), 403, "Forbidden")
refused("a user without library access -> 404 ItemNotFound", submit({"baseHash": H, "edits": [edit(1, T[0], "x")]}, tok=LIMITED), 404, "ItemNotFound")

check("settings: at most 2 pending per user", configure(MaxPendingSubmissionsPerUser=2) == 204)
st, s2 = submit({"baseHash": H, "edits": [edit(3, T[2], "Second submission")]})
check("quota: a second submission is accepted", st == 200, (st, s2))
refused("quota: a third -> 429 TooManyPending", submit({"baseHash": H, "edits": [edit(4, T[3], "Third")]}), 429, "TooManyPending")
check("settings: back to 50 pending per user", configure(MaxPendingSubmissionsPerUser=50) == 204)

# ---- withdrawing -------------------------------------------------------------------------------------------

st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Withdraw" % s2["id"], tok=LIMITED)
check("withdraw: someone else's submission -> 404", st == 404 and r["code"] == "SubmissionNotFound", (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Withdraw" % s2["id"], tok=USER)
check("withdraw: the user takes back their own (200, Withdrawn)", st == 200 and r["state"] == "Withdrawn" and r["decidedBy"] == "testuser", (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Withdraw" % s2["id"], tok=USER)
check("withdraw: twice -> 409 NotPending", st == 409 and r["code"] == "NotPending", (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/not-an-id/Withdraw", tok=USER)
check("withdraw: a malformed id -> 404", st == 404, (st, r))

# ---- the review list ---------------------------------------------------------------------------------------

st, pend = call("GET", "/SubtitleQuickEdit/Submissions")
check("review list: pending by default, just the first submission", st == 200 and [x["id"] for x in pend] == [s1["id"]], (st, pend))
st, dec = call("GET", "/SubtitleQuickEdit/Submissions?status=decided")
check("review list: decided holds the withdrawn one", st == 200 and [x["id"] for x in dec] == [s2["id"]], (st, dec))
st, both = call("GET", "/SubtitleQuickEdit/Submissions?status=all")
check("review list: all holds both", st == 200 and len(both) == 2, (st, both))
st, r = call("GET", "/SubtitleQuickEdit/Submissions?status=bogus")
check("review list: an unknown status -> 400", st == 400, (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s1["id"], {}, tok=USER)
check("approve by a user -> 403", st == 403, st)
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s1["id"], {"edits": [5]})
check("approve: a change that does not exist -> 400 InvalidChoice", st == 400 and r["code"] == "InvalidChoice", (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Reject" % s1["id"], {"reason": "r" * 501})
check("reject: a 501-character reason -> 400 TextTooLong", st == 400 and r["code"] == "TextTooLong", (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % ("0" * 32), {})
check("approve: an unknown submission -> 404", st == 404 and r["code"] == "SubmissionNotFound", (st, r))

# ---- approving after the file changed (the admin added a line first, so positions moved) -----------------------

st, c = cues(EP[2], EN2)
st, r = call("POST", sub(EP[2], EN2, "Edits"), {"baseHash": c["hash"], "edits": [
    {"type": "add", "clientId": "a", "startMs": 100, "endMs": 1000, "text": "Admin's own line"}]})
check("an admin adds a line before cue 1 directly (positions shift by one)", st == 200 and r["addedCues"] == {"a": 1}, (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s1["id"], {})
after = read_file(FILE[EN2]).decode("utf-8")
check("approve all: 200, applied, Approved, by the admin", st == 200 and r["applied"] and r["submission"]["state"] == "Approved"
      and r["submission"]["decidedBy"] and all(e["outcome"] == "Applied" for e in r["submission"]["edits"]), (st, r))
check("approve all: the moved line got the new text, the new line and the admin's line are both there",
      "A better two-line cue\r\nwith a fix." in after and "New first line" in after and "Admin's own line" in after
      and T[1].replace("\n", "\r\n") not in after, after[:400])
st, c2 = cues(EP[2], EN2)
check("approve all: the hash returned is the file's", c2["hash"] == r.get("hash"), (c2["hash"], r.get("hash")))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s1["id"], {})
check("approve again -> 409 NotPending", st == 409 and r["code"] == "NotPending", (st, r))
check("Me, admin: pendingReviews back to 0", me(ADMIN)["pendingReviews"] == 0, me(ADMIN))

# ---- a conflict: the admin changed the very line first --------------------------------------------------------

st, c = cues(EP[2], DE2, tok=USER)
D = [q["text"] for q in c["cues"]]
st, s3 = submit({"baseHash": c["hash"], "edits": [edit(5, D[4], "User's version")]}, idx=DE2)
st, r = call("POST", sub(EP[2], DE2, "Edits"), {"baseHash": c["hash"], "edits": [edit(5, D[4], "Admin's version")]})
check("conflict setup: a submission, then the admin edits the same line directly", st == 200 and s3["state"] == "Pending", (st, r))
de_before = read_file(FILE[DE2])
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s3["id"], {})
check("conflict: approve writes nothing, stays Pending, the change is marked Conflict with a message",
      st == 200 and not r["applied"] and r["submission"]["state"] == "Pending" and r["submission"]["edits"][0]["outcome"] == "Conflict"
      and r["submission"]["edits"][0]["message"] and read_file(FILE[DE2]) == de_before, (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Reject" % s3["id"], {"reason": "Already fixed"})
check("reject: 200, Rejected, with the reason", st == 200 and r["state"] == "Rejected" and r["reason"] == "Already fixed", (st, r))
st, mine = call("GET", "/SubtitleQuickEdit/Submissions/Mine", tok=USER)
check("Mine: the user sees the rejection and its reason",
      any(x["id"] == s3["id"] and x["state"] == "Rejected" and x["reason"] == "Already fixed" for x in mine), mine)

# ---- approving only some of the changes -------------------------------------------------------------------------

st, c = cues(EP[2], CS2, tok=USER)
C = [q["text"] for q in c["cues"]]
st, s4 = submit({"baseHash": c["hash"], "edits": [edit(1, C[0], "Zmena jedna"), edit(2, C[1], "Zmena dva")]}, idx=CS2)
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s4["id"], {"edits": [0]})
cs_after = read_file(FILE[CS2])
check("approve some: PartlyApproved, Applied + NotChosen, only the chosen change written",
      st == 200 and r["applied"] and r["submission"]["state"] == "PartlyApproved"
      and [e["outcome"] for e in r["submission"]["edits"]] == ["Applied", "NotChosen"]
      and b"Zmena jedna" in cs_after and b"Zmena dva" not in cs_after, (st, r))

# ---- submissions turned off --------------------------------------------------------------------------------------

st, c = cues(EP[2], EN2, tok=USER)
st, s5 = submit({"baseHash": c["hash"], "edits": [edit(6, c["cues"][5]["text"], "Waiting")]})
check("off setup: one more pending submission", st == 200, (st, s5))
check("settings: submissions off", configure(AllowUserSubmissions=False) == 204)
st, r = submit({"baseHash": c["hash"], "edits": [edit(6, c["cues"][5]["text"], "x")]})
check("submissions off: submitting -> 403", st == 403, (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Approve" % s5["id"], {})
check("submissions off: approving -> 409 SubmissionsOff", st == 409 and r["code"] == "SubmissionsOff", (st, r))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Reject" % s5["id"], {})
check("submissions off: rejecting -> 409 SubmissionsOff", st == 409 and r["code"] == "SubmissionsOff", (st, r))
st, pend = call("GET", "/SubtitleQuickEdit/Submissions")
check("submissions off: the admin can still see the pending one", st == 200 and [x["id"] for x in pend] == [s5["id"]], (st, pend))
st, mine = call("GET", "/SubtitleQuickEdit/Submissions/Mine", tok=USER)
check("submissions off: the user can still see their submissions", st == 200 and len(mine) == 5, (st, len(mine) if isinstance(mine, list) else mine))
st, r = call("POST", "/SubtitleQuickEdit/Submissions/%s/Withdraw" % s5["id"], tok=USER)
check("submissions off: the user can still withdraw", st == 200 and r["state"] == "Withdrawn", (st, r))

# ---- storage and log -------------------------------------------------------------------------------------------

files = listing(HOST_DATA + "/submissions")
check("storage: five submission files, named by id, no temp files",
      len(files) == 5 and all(re.fullmatch(r"[0-9a-f]{32}\.json", f) for f in files), files)
time.sleep(1)
fresh = [line for line in plugin_log() if line not in set(LOG_BEFORE)]
errs = [line for line in fresh if "[ERR]" in line and "SubtitleQuickEdit" in line]
check("log: no plugin errors", not errs, errs)
check("log: the submissions, the withdrawals and the decisions are logged",
      len([x for x in fresh if "for review (" in x]) == 5 and len([x for x in fresh if "withdrew submission" in x]) == 2
      and len([x for x in fresh if "approved submission" in x]) == 2 and len([x for x in fresh if "rejected submission" in x]) == 1,
      [x for x in fresh if "submission" in x][-12:])

# ---- clean up -----------------------------------------------------------------------------------------------------

configure(**DEFAULTS)
subprocess.run([os.path.join(HERE, "reset_fixtures.sh")], check=True)
failed = [n for n, ok, _ in results if not ok]
print("\n%d checks, %d passed, %d failed" % (len(results), len(results) - len(failed), len(failed)))
sys.exit(1 if failed else 0)
