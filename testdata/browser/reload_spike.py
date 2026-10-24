#!/usr/bin/env python3
"""Stage 3d spike: which "Reload subtitles" method makes the player show a saved edit, on the THROWAWAY test Jellyfin.

Run like stage3_tests.py (same container, same stdin: the testadmin password, then a second line that is ignored).
It plays "TestWebm 02" with its English .srt, pauses inside a line, and three times changes that line on disk through
the plugin's Edits API, each time trying one method and reading what the player's text track holds afterwards:
  A  POST /Sessions/{id}/Command SetSubtitleStreamIndex (Off, then the track again), sent by the server to the page
  B  the subtitle menu: Off, then the track again, clicked like a user would
  C  in the page: fetch the plugin's Cues and replace the cues of the page's text track ("manualTrack")
It then restores the line's original text. Prints one result line per method; changes nothing else.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

from playwright.sync_api import sync_playwright

BASE = os.environ["JF_BASE"].rstrip("/")
SHOTS = os.environ.get("SQE_SHOTS")
ADMIN_PW = sys.stdin.readline().rstrip("\n")


def api(method, path, body=None, token=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    auth = 'MediaBrowser Client="s3d", Device="reload-spike", DeviceId="stage3d-spike", Version="1.0"'
    if token:
        auth += ', Token="%s"' % token
    req.add_header("Authorization", auth)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return r.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw) if raw else None
        except ValueError:
            return e.code, None


st, auth = api("POST", "/Users/AuthenticateByName", {"Username": "testadmin", "Pw": ADMIN_PW})
if st != 200:
    sys.exit("could not log in through the API (HTTP %s)" % st)
TOKEN, ADMIN_ID = auth["AccessToken"], auth["User"]["Id"]
st, found = api("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=TestWebm&userId=" + ADMIN_ID, token=TOKEN)
MOVIE = {m["Name"]: m["Id"] for m in (found or {}).get("Items", [])}.get("TestWebm 02") or sys.exit("TestWebm 02 not found")
st, tracks = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % MOVIE, token=TOKEN)
EN = [t for t in tracks if t["external"] and t["format"] == "srt" and t["editable"] and t.get("language") in ("en", "eng")]
EN = (EN or [t for t in tracks if t["external"] and t["format"] == "srt" and t["editable"]])[0]["streamIndex"]
CUES_PATH = "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (MOVIE, EN)
EDITS_PATH = "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Edits" % (MOVIE, EN)


def current():
    st, body = api("GET", CUES_PATH, token=TOKEN)
    if st != 200:
        sys.exit("Cues call failed (HTTP %s)" % st)
    return body


def set_text(cue_index, new_text):
    body = current()
    old = [c for c in body["cues"] if c["index"] == cue_index][0]["text"]
    st, res = api("POST", EDITS_PATH, {"baseHash": body["hash"],
                                       "edits": [{"type": "edit", "cueIndex": cue_index, "oldText": old, "newText": new_text}]},
                  token=TOKEN)
    if st != 200:
        sys.exit("Edits call failed (HTTP %s): %s" % (st, res))


def track_text(page, t):
    """The text the page's subtitle track holds at time t (seconds), or None."""
    return page.evaluate("""(t) => {
        const v = document.querySelector('video.htmlvideoplayer');
        const tr = Array.from(v.textTracks).find((x) => x.label === 'manualTrack');
        if (!tr || !tr.cues) { return null; }
        const c = Array.from(tr.cues).find((x) => x.startTime <= t && t < x.endTime);
        return c ? c.text : null;
    }""", t)


def video_state(page):
    return page.evaluate("() => { const v = document.querySelector('video.htmlvideoplayer');"
                         " return { paused: v.paused, time: Math.round(v.currentTime * 1000) }; }")


def show_osd(page):
    page.mouse.move(300, 300)
    page.mouse.move(320, 310)


def choose(page, index):
    show_osd(page)
    page.locator("button.btnSubtitles:visible").last.click()
    page.locator(".actionSheetMenuItem[data-id='%s']" % index).first.click()


def report(name, page, t, expected, before):
    seen = None
    deadline = time.time() + 8
    while time.time() < deadline:
        seen = track_text(page, t)
        if seen is not None and expected in seen:
            break
        page.wait_for_timeout(250)
    after = video_state(page)
    page.wait_for_timeout(4000)
    later = track_text(page, t)
    print("%s  updated=%s  stays_updated=%s  paused %s->%s  position %d->%d ms  (track text now: %r)" % (
        name, seen is not None and expected in seen, later is not None and expected in later,
        before["paused"], after["paused"], before["time"], after["time"], later), flush=True)
    if SHOTS:
        page.screenshot(path=os.path.join(SHOTS, "3d-reload-%s.png" % name[0]))


with sync_playwright() as p:
    browser = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    page = browser.new_page(viewport={"width": 1280, "height": 720})
    page.goto(BASE + "/web/#/login", wait_until="domcontentloaded")
    page.locator("input[type=password]:visible").first.wait_for(timeout=10000)
    page.locator("input[type=text]:visible, input:not([type]):visible").first.fill("testadmin")
    page.locator("input[type=password]:visible").first.fill(ADMIN_PW)
    page.locator("input[type=password]:visible").first.press("Enter")
    page.wait_for_function("() => location.hash.indexOf('#/home') === 0", timeout=20000)
    page.goto(BASE + "/web/#/details?id=" + MOVIE, wait_until="domcontentloaded")
    page.locator("button.btnPlay:visible").first.click(timeout=15000)
    page.wait_for_function("() => { const v = document.querySelector('video.htmlvideoplayer'); return v && v.readyState >= 2; }",
                           timeout=30000)
    choose(page, EN)
    page.wait_for_timeout(1500)

    cues = current()["cues"]
    cue = cues[min(3, len(cues) - 1)]
    original = cue["text"]
    t = (cue["start"] + cue["end"]) / 2000.0
    page.evaluate("(t) => { const v = document.querySelector('video.htmlvideoplayer'); v.pause(); v.currentTime = t; }", t)
    page.wait_for_timeout(1000)
    print("line %d at %.3f s; the track holds %r" % (cue["index"], t, track_text(page, t)), flush=True)

    try:
        device = page.evaluate("() => ApiClient.deviceId()")
        st, sessions = api("GET", "/Sessions?DeviceId=" + urllib.request.quote(device), token=TOKEN)
        session_id = sessions[0]["Id"] if st == 200 and sessions else None

        # A: the server tells the page to switch the subtitle off and back on.
        set_text(cue["index"], original + " [A]")
        before = video_state(page)
        if session_id:
            api("POST", "/Sessions/%s/Command" % session_id,
                {"Name": "SetSubtitleStreamIndex", "Arguments": {"Index": "-1"}}, token=TOKEN)
            page.wait_for_timeout(1500)
            api("POST", "/Sessions/%s/Command" % session_id,
                {"Name": "SetSubtitleStreamIndex", "Arguments": {"Index": str(EN)}}, token=TOKEN)
            report("A session command", page, t, "[A]", before)
        else:
            print("A session command  skipped: no session found for the page", flush=True)

        # B: the subtitle menu, Off then the track again.
        set_text(cue["index"], original + " [B]")
        before = video_state(page)
        choose(page, -1)
        page.wait_for_timeout(1000)
        choose(page, EN)
        report("B subtitle menu", page, t, "[B]", before)

        # C: replace the cues of the page's text track with the plugin's.
        set_text(cue["index"], original + " [C]")
        before = video_state(page)
        page.evaluate("""(path) => ApiClient.getJSON(ApiClient.getUrl(path)).then((body) => {
            const v = document.querySelector('video.htmlvideoplayer');
            const tr = Array.from(v.textTracks).find((x) => x.label === 'manualTrack');
            Array.from(tr.cues || []).forEach((c) => tr.removeCue(c));
            body.cues.forEach((c) => tr.addCue(new VTTCue(c.start / 1000, c.end / 1000, c.text)));
        })""", CUES_PATH.lstrip("/"))
        report("C replace track cues", page, t, "[C]", before)
    finally:
        set_text(cue["index"], original)
        print("restored line %d" % cue["index"], flush=True)
        browser.close()
