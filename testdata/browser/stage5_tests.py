#!/usr/bin/env python3
"""Stage 5 browser tests (1.1: WebVTT and ASS/SSA in the player), in headless Chromium against the THROWAWAY test
Jellyfin. They edit "TestWebm 03.de.vtt" and "TestWebm 03.ja.ass" through the editor and put both back through the
plugin's Restore API at the end. Run testdata/reset_webm03_formats.sh before the first run (it writes the two files);
the tests ask Jellyfin to rescan the movie if it does not list the .ass yet.

Checks the "Show style tags" toggle (off by default: a leading tag block is hidden and kept, on: the full text), the
ASS style name and read-only drawings, the 10 ms hint in Add line for ASS, Save, and Reload subtitles. For ASS the web
player draws the subtitles itself (JASSUB), so the run also reports which renderer the page used and whether Reload
made the player fetch the file again (the 5d Reload spike).

Run like stage3_tests.py, in the Playwright image on a host that can reach JF_BASE:
  docker run --rm -i -e JF_BASE -v <folder holding testdata/browser>:/sqe:ro sqe-playwright:1.64.1 \
    python3 /sqe/testdata/browser/stage5_tests.py
Reads two lines on stdin: the testadmin password, then the testuser password (unused here, so the same input works for
both scripts). Prints no passwords or tokens. Prints PASS/FAIL per check and INFO lines; exit code 1 on any failure.
Optional: SQE_SHOTS=<folder> saves screenshots.
"""
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

from playwright.sync_api import sync_playwright

BASE = os.environ["JF_BASE"].rstrip("/")
SHOTS = os.environ.get("SQE_SHOTS")
ADMIN_NAME = "testadmin"
ADMIN_PW = sys.stdin.readline().rstrip("\n")
sys.stdin.readline()

results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + ("" if ok or not detail else "  (" + detail + ")"), flush=True)


def info(text):
    print("INFO " + text, flush=True)


def api(method, path, body=None, token=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    auth = 'MediaBrowser Client="s5", Device="browser-tests", DeviceId="stage5-browser", Version="1.0"'
    if token:
        auth += ', Token="%s"' % token
    req.add_header("Authorization", auth)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return r.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        return e.code, None


st, auth = api("POST", "/Users/AuthenticateByName", {"Username": ADMIN_NAME, "Pw": ADMIN_PW})
if st != 200:
    sys.exit("could not log in as %s through the API (HTTP %s)" % (ADMIN_NAME, st))
TOKEN, ADMIN_ID = auth["AccessToken"], auth["User"]["Id"]
st, found = api("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=TestWebm&userId=" + ADMIN_ID, token=TOKEN)
MOVIES = {m["Name"]: m["Id"] for m in (found or {}).get("Items", [])}
if "TestWebm 03" not in MOVIES:
    sys.exit("movie 'TestWebm 03' not found: run testdata/make_webm.sh and scan the TestWebm library")
MOVIE = MOVIES["TestWebm 03"]


def tracks():
    st, body = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % MOVIE, token=TOKEN)
    if st != 200:
        sys.exit("Subtitles call failed (HTTP %s)" % st)
    return body


def track_of(fmt):
    found = [t for t in tracks() if t["external"] and t["format"] == fmt]
    return found[0] if found else None


def cues_full(index):
    st, body = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (MOVIE, index), token=TOKEN)
    if st != 200:
        sys.exit("Cues call failed (HTTP %s)" % st)
    return body


def restore(index, original):
    now = cues_full(index)
    if now["hash"] == original["hash"]:
        return True
    st, _ = api("POST", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Restore" % (MOVIE, index),
                {"baseHash": now["hash"], "targetHash": original["hash"]}, token=TOKEN)
    return st == 200 and cues_full(index)["hash"] == original["hash"]


# Jellyfin only lists a new external file after a scan of the item.
if track_of("ass") is None:
    api("POST", "/Items/%s/Refresh?MetadataRefreshMode=Default&ImageRefreshMode=None" % MOVIE, token=TOKEN)
    end = time.time() + 60
    while track_of("ass") is None and time.time() < end:
        time.sleep(2)
VTT, ASS = track_of("vtt"), track_of("ass")
if VTT is None or ASS is None:
    sys.exit("TestWebm 03 has no external .vtt or .ass: run testdata/reset_webm03_formats.sh in the test container")
VTT_CUES = [(1000, 3000, "VTT line"), (4000, 6000, "<i>Styled VTT line</i>"), (7000, 9000, "<v Anna>Anna speaks</v>")]
ASS_TEXTS = ["Plain ASS line", "{\\an8\\c&H00FFFF&}Top sign, with a comma", "Two lines\nin one cue",
             "{\\p1}m 0 0 l 100 0 100 100 0 100{\\p0}"]


# ---- browser helpers (as in stage3_tests.py) -------------------------------------------------------------------

def shot(page, name):
    if SHOTS:
        page.screenshot(path=os.path.join(SHOTS, name + ".png"))


def login(page, name, pw):
    page.goto(BASE + "/web/#/login", wait_until="domcontentloaded")
    password = page.locator("input[type=password]:visible")
    try:
        password.first.wait_for(timeout=8000)
    except Exception:
        page.get_by_role("button", name="Manual Login").click()
        password.first.wait_for(timeout=8000)
    page.locator("input[type=text]:visible, input:not([type]):visible").first.fill(name)
    password.first.fill(pw)
    password.first.press("Enter")
    page.wait_for_function("() => window.ApiClient && window.ApiClient.isLoggedIn && window.ApiClient.isLoggedIn()",
                           timeout=20000)
    page.wait_for_function("() => location.hash.indexOf('#/home') === 0", timeout=20000)


def show_osd(page):
    page.mouse.move(300, 300)
    page.mouse.move(320, 310)


def play(page):
    page.goto(BASE + "/web/#/details?id=" + MOVIE, wait_until="domcontentloaded")
    page.locator("button.btnPlay:visible").first.click(timeout=15000)
    page.wait_for_function("() => { const v = document.querySelector('video.htmlvideoplayer');"
                           " return v && v.readyState >= 2; }", timeout=30000)


def wait_button(page, present, seconds):
    end = time.time() + seconds
    while time.time() < end:
        show_osd(page)
        if (page.evaluate("() => document.querySelectorAll('.sqe-edit-button').length") > 0) == present:
            return True
        page.wait_for_timeout(250)
    return False


def choose_subtitle(page, index):
    show_osd(page)
    page.locator("button.btnSubtitles:visible").last.click()
    page.locator(".actionSheetMenuItem[data-id='%s']" % index).first.click()
    page.wait_for_timeout(500)


def seek_to(page, ms):
    page.evaluate("""(t) => new Promise((resolve) => {
        const v = document.querySelector('video.htmlvideoplayer');
        v.pause();
        v.addEventListener('seeked', () => resolve(), { once: true });
        v.currentTime = t;
    })""", ms / 1000.0)


def drawing_info(page):
    """What draws the subtitle now: the text track's mode, JASSUB's element, canvases near the video."""
    return page.evaluate("""() => { const v = document.querySelector('video.htmlvideoplayer');
        const tr = v ? Array.from(v.textTracks).find((x) => x.label === 'manualTrack') : null;
        return { manualTrack: tr ? tr.mode : null, jassub: document.querySelectorAll('.JASSUB').length,
                 canvases: document.querySelectorAll('.videoPlayerContainer canvas').length,
                 customElement: Boolean(document.querySelector('.videoSubtitles .videoSubtitlesInner')) }; }""")


def open_panel(page, index, ms):
    play(page)
    choose_subtitle(page, index)
    seek_to(page, ms)
    shown = wait_button(page, True, 15)
    info("stream %d: edit button %s; drawing %s" % (index, "shown" if shown else "NOT shown", json.dumps(drawing_info(page))))
    show_osd(page)
    page.locator(".sqe-edit-button").first.click()
    page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)


def status(page):
    return page.locator(".sqe-panel .sqe-status").text_content() or ""


def wait_status(page, text, seconds=15):
    page.wait_for_function("(t) => { const s = document.querySelector('.sqe-panel .sqe-status');"
                           " return s && s.textContent.indexOf(t) >= 0; }", arg=text, timeout=seconds * 1000)


def first_box(page):
    return page.locator(".sqe-panel .sqe-cue .sqe-cue-text").first


def tags_toggle(page):
    return page.evaluate("""() => { const l = document.querySelector('.sqe-panel .sqe-tags');
        const b = document.querySelector('.sqe-panel .sqe-tags-toggle');
        return { shown: Boolean(l) && !l.hidden && l.offsetParent !== null, checked: Boolean(b && b.checked) }; }""")


def renderer(page):
    state = page.evaluate("() => window.SubtitleQuickEdit && window.SubtitleQuickEdit.debugState()")
    return (state or {}).get("renderer")


def manual_track_text(page, ms):
    return page.evaluate("""(t) => { const v = document.querySelector('video.htmlvideoplayer');
        const tr = Array.from(v.textTracks).find((x) => x.label === 'manualTrack');
        const c = tr && tr.cues ? Array.from(tr.cues).find((x) => x.startTime <= t && t < x.endTime) : null;
        return c ? c.text : null; }""", ms / 1000.0)


def new_context(browser, pages):
    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)
    page.on("dialog", lambda d: d.accept())
    return page


# ---- scenarios -----------------------------------------------------------------------------------------------------

def admin_vtt(browser, pages):
    index = VTT["streamIndex"]
    original = cues_full(index)
    if [(c["start"], c["end"], c["text"]) for c in original["cues"]] != VTT_CUES:
        check("TestWebm 03.de.vtt is as reset_webm03_formats.sh writes it (run that script first)", False,
              repr([c["text"] for c in original["cues"]]))
        return
    page = new_context(browser, pages)
    try:
        login(page, ADMIN_NAME, ADMIN_PW)
        open_panel(page, index, 5000)
        check("vtt: the button and panel open for an external .vtt", page.locator(".sqe-panel").count() == 1)
        info("vtt: the page draws this subtitle with renderer %r" % renderer(page))
        t = tags_toggle(page)
        check("vtt: the \"Show style tags\" toggle is shown, off by default", t["shown"] and not t["checked"], repr(t))
        check("vtt: with tags hidden, the box shows the text without <i>…</i>, marked styled",
              first_box(page).input_value() == "Styled VTT line"
              and page.locator(".sqe-panel .sqe-cue .sqe-badge-styled").count() >= 1, repr(first_box(page).input_value()))
        page.locator(".sqe-panel .sqe-tags-toggle").check()
        page.wait_for_timeout(200)
        check("vtt: with tags shown, the box shows the full text", first_box(page).input_value() == "<i>Styled VTT line</i>",
              repr(first_box(page).input_value()))
        page.locator(".sqe-panel .sqe-tags-toggle").uncheck()
        page.wait_for_timeout(200)
        shot(page, "5d-vtt-panel")

        box = first_box(page)
        box.fill("Styled line, fixed")
        box.press("Control+Enter")
        page.wait_for_timeout(200)
        if renderer(page) == "native":
            check("vtt: an applied edit shows in the player",
                  "Styled line, fixed" in (manual_track_text(page, 5000) or ""), repr(manual_track_text(page, 5000)))
        box.press("Control+s")
        wait_status(page, "Saved")
        after = cues_full(index)
        check("vtt: Save keeps the hidden tags around the new text and changes only that line",
              [c["text"] for c in after["cues"]] == ["VTT line", "<i>Styled line, fixed</i>", "<v Anna>Anna speaks</v>"],
              repr([c["text"] for c in after["cues"]]))
        page.locator(".sqe-reload").click()
        wait_status(page, "reloaded", 25)
        check("vtt: Reload subtitles finishes", "reloaded" in status(page), status(page))
        if renderer(page) == "native":
            check("vtt: after Reload the player holds the saved text",
                  "Styled line, fixed" in (manual_track_text(page, 5000) or ""), repr(manual_track_text(page, 5000)))

        # Tags shown: an edit that changes the tags themselves.
        page.locator(".sqe-panel .sqe-tags-toggle").check()
        page.wait_for_timeout(200)
        box = first_box(page)
        box.fill("<b>Styled line, fixed</b>")
        box.press("Control+Enter")
        box.press("Control+s")
        wait_status(page, "Saved")
        check("vtt: with tags shown, the tags can be edited too", cues_full(index)["cues"][1]["text"] == "<b>Styled line, fixed</b>",
              repr(cues_full(index)["cues"][1]["text"]))
        shot(page, "5d-vtt-saved")
    finally:
        check("vtt: the file is put back to the original", restore(index, original))


def admin_ass(browser, pages):
    index = ASS["streamIndex"]
    original = cues_full(index)
    if [c["text"] for c in original["cues"]] != ASS_TEXTS:
        check("TestWebm 03.ja.ass is as reset_webm03_formats.sh writes it (run that script first)", False,
              repr([c["text"] for c in original["cues"]]))
        return
    page = new_context(browser, pages)
    sub_requests = []
    page.on("request", lambda r: sub_requests.append(r.url) if re.search(r"/Subtitles/%d/" % index, r.url) else None)
    try:
        login(page, ADMIN_NAME, ADMIN_PW)
        open_panel(page, index, 5000)
        drew = renderer(page)
        info("ass: the page draws this subtitle with renderer %r (none = JASSUB draws it, no text track)" % drew)
        check("ass: the panel shows the line's style name", (page.locator(".sqe-panel .sqe-cue .sqe-cue-style").first.text_content() or "") == "Sign")
        t = tags_toggle(page)
        check("ass: the \"Show style tags\" toggle is shown, off by default", t["shown"] and not t["checked"], repr(t))
        check("ass: with tags hidden, the box shows the text without the {\\an8…} block",
              first_box(page).input_value() == "Top sign, with a comma", repr(first_box(page).input_value()))
        page.locator(".sqe-panel .sqe-tags-toggle").check()
        page.wait_for_timeout(200)
        check("ass: with tags shown, the box shows the override block", first_box(page).input_value() == ASS_TEXTS[1],
              repr(first_box(page).input_value()))
        page.locator(".sqe-panel .sqe-tags-toggle").uncheck()
        page.wait_for_timeout(200)
        shot(page, "5d-ass-panel")

        box = first_box(page)
        box.fill("Top sign, fixed")
        box.press("Control+Enter")
        page.wait_for_timeout(300)
        if drew == "none":
            check("ass: the staged note says the player keeps the old text until Save and Reload (it draws ASS itself)",
                  "Save, then Reload subtitles" in status(page), status(page))
        else:
            check("ass: the staged note says it is shown in the player", "shown in the player" in status(page), status(page))
        box.press("Control+s")
        wait_status(page, "Saved")
        after = cues_full(index)
        check("ass: Save keeps the override block and changes only that line",
              [c["text"] for c in after["cues"]] == [ASS_TEXTS[0], "{\\an8\\c&H00FFFF&}Top sign, fixed"] + ASS_TEXTS[2:],
              repr([c["text"] for c in after["cues"]]))

        # The Reload spike: does switching the subtitle off and on make the player fetch the file again?
        before = len(sub_requests)
        page.locator(".sqe-reload").click()
        try:
            wait_status(page, "reloaded", 25)
        except Exception:
            pass
        fetched = len(sub_requests) - before
        info("ass: Reload subtitles -> status %r, %d subtitle request(s) after the click, renderer %r"
             % (status(page), fetched, renderer(page)))
        check("ass: Reload subtitles finishes without an error", "reloaded" in status(page), status(page))
        check("ass: Reload made the player fetch the subtitle file again", fetched >= 1, "%d requests" % fetched)
        shot(page, "5d-ass-reloaded")

        # A drawing is read-only.
        page.locator(".sqe-panel .sqe-tags-toggle").uncheck()
        for _ in range(2):
            page.locator(".sqe-next").click()
            page.wait_for_timeout(400)
        ro = page.evaluate("""() => Array.from(document.querySelectorAll('.sqe-panel .sqe-cue')).map((c) =>
            ({ index: Number(c.getAttribute('data-cue-index')), readOnly: c.querySelector('textarea').readOnly,
               hint: (c.querySelector('.sqe-hint') || {}).textContent || '' }))""")
        drawing = [r for r in ro if r["index"] == 4]
        check("ass: a drawing line is shown read-only with a hint",
              drawing and drawing[0]["readOnly"] and "drawing" in drawing[0]["hint"], repr(ro))

        # Add line: the form says ASS keeps times to 10 ms.
        page.locator(".sqe-add").click()
        page.wait_for_selector(".sqe-new-line", timeout=5000)
        hint = page.locator(".sqe-new-line").text_content() or ""
        check("ass: Add line says the file keeps times to 10 ms", "10" in hint and "ms" in hint, hint[:200])
        shot(page, "5d-ass-add")
    finally:
        check("ass: the file is put back to the original", restore(index, original))


with sync_playwright() as p:
    browser = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    for scenario in (admin_vtt, admin_ass):
        pages = []
        try:
            scenario(browser, pages)
        except Exception as e:
            check(scenario.__name__ + " ran to the end", False, type(e).__name__ + ": " + str(e).splitlines()[0][:200])
            if pages and not pages[-1].is_closed():
                try:
                    shot(pages[-1], scenario.__name__ + "-failed")
                    print("     status at failure: %r" % status(pages[-1]), flush=True)
                except Exception:
                    pass
        finally:
            for page in pages:
                try:
                    page.context.close()
                except Exception:
                    pass
    browser.close()

print("%d of %d passed" % (sum(results), len(results)))
sys.exit(0 if all(results) else 1)
