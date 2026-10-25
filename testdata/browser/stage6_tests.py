#!/usr/bin/env python3
"""Stage 6 browser tests (1.2: making a subtitle track inside the video editable), in headless Chromium against the
THROWAWAY test Jellyfin. TestWebm 03 has an embedded WebVTT track: the test makes an editable copy of it through the
panel, checks that playback restarts on the copy and that the copy can be edited and saved, and deletes the copy
through the API at the end (its content goes to the plugin's backups).

Also reports (INFO) which subtitle Jellyfin picks when the movie is started again with the copy present, for the
default-track decision.

Run like stage5_tests.py, in the Playwright image on a host that can reach JF_BASE:
  docker run --rm -i -e JF_BASE -v <folder holding testdata/browser>:/sqe:ro sqe-playwright:1.64.1 \
    python3 /sqe/testdata/browser/stage6_tests.py
Reads two lines on stdin: the testadmin password, then the testuser password (unused). Prints no passwords or tokens.
Prints PASS/FAIL per check and INFO lines; exit code 1 on any failure. Optional: SQE_SHOTS=<folder> saves screenshots.
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
    auth = 'MediaBrowser Client="s6", Device="browser-tests", DeviceId="stage6-browser", Version="1.0"'
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


def tracks():
    st, body = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % MOVIE, token=TOKEN)
    if st != 200:
        sys.exit("Subtitles call failed (HTTP %s)" % st)
    return body


def embedded_track():
    found = [t for t in tracks() if not t["external"]]
    return found[0] if found else None


def delete_copies():
    """Deletes every editable copy of TestWebm 03 through the API. True when there is none left."""
    st, copies = api("GET", "/SubtitleQuickEdit/Extracted", token=TOKEN)
    for c in (copies or []) if st == 200 else []:
        if c["itemId"].replace("-", "") == MOVIE.replace("-", ""):
            api("DELETE", "/SubtitleQuickEdit/Extracted/%s" % c["id"], token=TOKEN)
    end = time.time() + 20
    while time.time() < end:
        e = embedded_track()
        if e is not None and e.get("extractedStreamIndex") is None:
            return True
        time.sleep(1)
    return False


def debug(page):
    return page.evaluate("() => window.SubtitleQuickEdit && window.SubtitleQuickEdit.debugState()") or {}


def button_label(page):
    return page.evaluate("() => { const b = document.querySelector('.sqe-edit-button'); return b && b.getAttribute('aria-label'); }")


def wait_label(page, label, seconds):
    end = time.time() + seconds
    while time.time() < end:
        show_osd(page)
        if button_label(page) == label:
            return True
        page.wait_for_timeout(250)
    return False


def status(page):
    return page.locator(".sqe-panel .sqe-status").text_content() or ""


def admin_extract(browser, pages):
    emb = embedded_track()
    if emb is None or not emb.get("extractable"):
        check("TestWebm 03 has an extractable embedded track", False, repr(emb))
        return
    if emb.get("extractedStreamIndex") is not None:
        info("a copy from an earlier run was there; deleting it first")
        if not delete_copies():
            check("the earlier copy could be deleted", False)
            return
    count_before = len(tracks())
    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)
    try:
        login(page, ADMIN_NAME, ADMIN_PW)
        play(page)
        choose_subtitle(page, emb["streamIndex"])
        seek_to(page, 2000)
        check("embedded: the button offers to make the subtitle editable", wait_label(page, "Make this subtitle editable", 15),
              repr(button_label(page)))
        show_osd(page)
        page.locator(".sqe-edit-button").first.click()
        page.wait_for_selector(".sqe-panel .sqe-make", timeout=20000)
        what = page.locator(".sqe-panel .sqe-extract-what").text_content() or ""
        info("panel: %r" % what)
        check("embedded: the panel names a SubRip copy next to the video", "TestWebm 03" in what and ".srt" in what, what)
        shot(page, "6c-make")
        # jellyfin-web either makes a new <video> or gives the one it has a new source ('emptied').
        page.evaluate("() => { const v = document.querySelector('video.htmlvideoplayer'); window.__sqeOldVideo = v;"
                      " window.__sqeEmptied = 0; v.addEventListener('emptied', () => { window.__sqeEmptied++; }); }")
        page.locator(".sqe-panel .sqe-make").click()
        restarted = True
        try:
            page.wait_for_function("() => { const v = document.querySelector('video.htmlvideoplayer');"
                                   " return v && (v !== window.__sqeOldVideo || window.__sqeEmptied > 0) && v.readyState >= 2; }",
                                   timeout=45000)
        except Exception:
            restarted = False
        how = page.evaluate("() => { const v = document.querySelector('video.htmlvideoplayer');"
                            " return { newElement: v !== window.__sqeOldVideo, emptied: window.__sqeEmptied, readyState: v ? v.readyState : null,"
                            " status: (document.querySelector('.sqe-panel .sqe-status') || {}).textContent || null,"
                            " toast: (document.querySelector('.sqe-toast') || {}).textContent || null }; }")
        info("restart: %s" % json.dumps(how))
        check("embedded: Make restarted playback", restarted, json.dumps(how))
        if not restarted:
            shot(page, "6c-no-restart")
            return
        toast = page.evaluate("() => (document.querySelector('.sqe-toast') || {}).textContent || ''")
        info("toast: %r" % toast)
        emb2 = embedded_track()
        new_index = emb2.get("extractedStreamIndex") if emb2 else None
        check("embedded: the library lists the copy as a new track", new_index is not None and len(tracks()) == count_before + 1,
              repr(emb2))
        check("embedded: the restarted playback is on the copy, near the same place",
              wait_label(page, "Edit subtitle line", 20) and (debug(page).get("target") or {}).get("streamIndex") == new_index,
              json.dumps(debug(page)))
        position = page.evaluate("() => document.querySelector('video.htmlvideoplayer').currentTime")
        info("restarted at %.1f s (was at 2.0 s)" % position)
        seek_to(page, 2000)
        show_osd(page)
        page.locator(".sqe-edit-button").first.click()
        page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)
        box = page.locator(".sqe-panel .sqe-cue .sqe-cue-text").first
        check("copy: the panel shows the embedded line", box.input_value() == "Embedded line", repr(box.input_value()))
        box.fill("Embedded line, fixed")
        box.press("Control+Enter")
        box.press("Control+s")
        page.wait_for_function("() => { const s = document.querySelector('.sqe-panel .sqe-status');"
                               " return s && s.textContent.indexOf('Saved') >= 0; }", timeout=15000)
        st, c = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (MOVIE, new_index), token=TOKEN)
        check("copy: Save writes the edited line to the copy", st == 200 and [q["text"] for q in c["cues"]] == ["Embedded line, fixed"],
              repr(c))
        shot(page, "6c-copy-saved")

        # Which subtitle does Jellyfin pick when the movie is started again, with the copy present? (D6, information only)
        page.goto(BASE + "/web/#/home", wait_until="domcontentloaded")
        page.wait_for_timeout(1000)
        play(page)
        page.wait_for_timeout(4000)
        info("started again: subtitle index %r (embedded %d, copy %d)"
             % ((debug(page).get("session") or {}).get("subtitleIndex"), emb2["streamIndex"] if emb2 else -1, new_index))
    finally:
        ctx.close()
        check("cleanup: the copy is deleted and the embedded track has no copy", delete_copies())
        check("cleanup: the track list is as before", len(tracks()) == count_before, "%d tracks" % len(tracks()))


with sync_playwright() as p:
    browser = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    pages = []
    try:
        admin_extract(browser, pages)
    except Exception as e:
        check("admin_extract ran to the end", False, type(e).__name__ + ": " + str(e).splitlines()[0][:200])
        if pages and not pages[-1].is_closed():
            try:
                shot(pages[-1], "admin_extract-failed")
                print("     status at failure: %r" % status(pages[-1]), flush=True)
            except Exception:
                pass
    browser.close()

print("%d of %d passed" % (sum(results), len(results)))
sys.exit(0 if results and all(results) else 1)
