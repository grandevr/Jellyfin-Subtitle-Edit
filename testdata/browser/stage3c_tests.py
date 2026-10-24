#!/usr/bin/env python3
"""Stage 3c browser tests: the edit button and the panel shell, in headless Chromium against the THROWAWAY test Jellyfin.

Runs inside the Playwright image (sqe-playwright:1.64.1, Playwright for Python) on a host that can reach JF_BASE:
  docker run --rm -i -e JF_BASE -v <folder holding testdata/browser>:/sqe:ro sqe-playwright:1.64.1 \
    python3 /sqe/testdata/browser/stage3c_tests.py
(devtools/sync.sh skips testdata/, so copy testdata/browser to the test host first.)
Reads two lines on stdin: the testadmin password, then the testuser password. Prints neither, and no tokens or URLs
with tokens. Needs the TestWebm library (testdata/make_webm.sh). Prints PASS/FAIL per check; exit code 1 on any failure.
Optional: SQE_SHOTS=<folder> saves a screenshot after each scenario, and one where a scenario fails.
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
ADMIN_NAME, USER_NAME = "testadmin", "testuser"
ADMIN_PW = sys.stdin.readline().rstrip("\n")
USER_PW = sys.stdin.readline().rstrip("\n")

results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + ("" if ok or not detail else "  (" + detail + ")"), flush=True)


# ---- server API (as testadmin) -------------------------------------------------------------------------------------

def api(method, path, body=None, token=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    auth = 'MediaBrowser Client="s3c", Device="browser-tests", DeviceId="stage3c-tests", Version="1.0"'
    if token:
        auth += ', Token="%s"' % token
    req.add_header("Authorization", auth)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, None


st, auth = api("POST", "/Users/AuthenticateByName", {"Username": ADMIN_NAME, "Pw": ADMIN_PW})
if st != 200:
    sys.exit("could not log in as %s through the API (HTTP %s)" % (ADMIN_NAME, st))
TOKEN, ADMIN_ID = auth["AccessToken"], auth["User"]["Id"]

st, found = api("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=TestWebm&userId=" + ADMIN_ID, token=TOKEN)
MOVIES = {m["Name"]: m["Id"] for m in (found or {}).get("Items", [])}
for name in ("TestWebm 01", "TestWebm 03"):
    if name not in MOVIES:
        sys.exit("movie %r not found: run testdata/make_webm.sh and scan the TestWebm library" % name)


def tracks(item_id):
    st, body = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles" % item_id, token=TOKEN)
    if st != 200:
        sys.exit("Subtitles call failed for %s (HTTP %s)" % (item_id, st))
    return body


def cues(item_id, index):
    st, body = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (item_id, index), token=TOKEN)
    if st != 200:
        sys.exit("Cues call failed (HTTP %s)" % st)
    return body["cues"]


# ---- browser helpers -----------------------------------------------------------------------------------------------

def shot(page, name):
    if SHOTS:
        page.screenshot(path=os.path.join(SHOTS, name + ".png"))


def login(page, name, pw):
    page.goto(BASE + "/web/#/login", wait_until="domcontentloaded")
    password = page.locator("input[type=password]:visible")
    try:
        password.first.wait_for(timeout=8000)
    except Exception:
        # A server that lists users shows tiles first; the manual form is behind a button.
        page.get_by_role("button", name="Manual Login").click()
        password.first.wait_for(timeout=8000)
    page.locator("input[type=text]:visible, input:not([type]):visible").first.fill(name)
    password.first.fill(pw)
    password.first.press("Enter")
    page.wait_for_function("() => window.ApiClient && window.ApiClient.isLoggedIn && window.ApiClient.isLoggedIn()",
                           timeout=20000)
    # jellyfin-web redirects to the home page after login; navigating before that lands would be overwritten by it.
    page.wait_for_function("() => location.hash.indexOf('#/home') === 0", timeout=20000)


def show_osd(page):
    page.mouse.move(300, 300)
    page.mouse.move(320, 310)


def play(page, item_id):
    page.goto(BASE + "/web/#/details?id=" + item_id, wait_until="domcontentloaded")
    page.locator("button.btnPlay:visible").first.click(timeout=15000)
    page.wait_for_function("() => { const v = document.querySelector('video.htmlvideoplayer');"
                           " return v && v.readyState >= 2; }", timeout=30000)


def button_count(page):
    return page.evaluate("() => document.querySelectorAll('.sqe-edit-button').length")


def wait_button(page, present, seconds):
    """Polls for the edit button (keeping the OSD shown). True if it reached the wanted state in time."""
    end = time.time() + seconds
    while time.time() < end:
        show_osd(page)
        if (button_count(page) > 0) == present:
            return True
        page.wait_for_timeout(250)
    return False


def choose_subtitle(page, index):
    show_osd(page)
    page.locator("button.btnSubtitles:visible").last.click()
    page.locator(".actionSheetMenuItem[data-id='%s']" % index).first.click()
    page.wait_for_timeout(500)


def seek_into(page, cue):
    page.evaluate("""(t) => new Promise((resolve) => {
        const v = document.querySelector('video.htmlvideoplayer');
        v.pause();
        v.addEventListener('seeked', () => resolve(), { once: true });
        v.currentTime = t;
    })""", (cue["start"] + cue["end"]) / 2000.0)


def video_state(page):
    return page.evaluate("""() => { const v = document.querySelector('video.htmlvideoplayer');
        return { paused: v.paused, muted: v.muted, time: v.currentTime, fullscreen: Boolean(document.fullscreenElement) }; }""")


# ---- scenarios -----------------------------------------------------------------------------------------------------

def admin_desktop(browser, pages):
    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)
    login(page, ADMIN_NAME, ADMIN_PW)

    movie = MOVIES["TestWebm 01"]
    en = [t for t in tracks(movie) if t["external"] and t["format"] == "srt" and t["editable"]]
    play(page, movie)
    choose_subtitle(page, en[0]["streamIndex"])
    check("button shows for an admin with an external .srt selected", wait_button(page, True, 15))
    check("the button sits in the OSD button row",
          page.evaluate("() => Boolean(document.querySelector('.videoOsdBottom .osdControls .buttons .sqe-edit-button'))"))
    check("exactly one edit button", button_count(page) == 1, "count %d" % button_count(page))

    all_cues = cues(movie, en[0]["streamIndex"])
    target = all_cues[min(2, len(all_cues) - 1)]
    seek_into(page, target)
    page.evaluate("() => document.querySelector('video.htmlvideoplayer').play()")
    page.wait_for_timeout(100)
    show_osd(page)
    page.locator(".sqe-edit-button").first.click()
    page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)
    state = video_state(page)
    check("clicking the button pauses playback", state["paused"])
    shown = page.evaluate("""() => Array.from(document.querySelectorAll('.sqe-panel .sqe-cue')).map((c) =>
        ({ index: Number(c.getAttribute('data-cue-index')), text: c.querySelector('textarea').value }))""")
    check("the panel shows the cue on screen",
          len(shown) >= 1 and shown[0]["index"] == target["index"] and shown[0]["text"] == target["text"],
          "shown %r, expected line %d" % ([s["index"] for s in shown], target["index"]))
    check("the cue's text box has focus",
          page.evaluate("() => document.activeElement && document.activeElement.classList.contains('sqe-cue-text')"))
    shot(page, "3c-admin-panel")

    before = video_state(page)
    box = page.locator(".sqe-panel .sqe-cue-text").first
    box.press("End")
    for key in ("Space", "f", "k", "m", "j", "l", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Enter"):
        box.press(key)
    page.wait_for_timeout(500)
    after = video_state(page)
    check("typing in the panel never triggers player shortcuts",
          after["paused"] and not after["fullscreen"] and after["muted"] == before["muted"]
          and abs(after["time"] - before["time"]) < 0.05,
          "before %r after %r" % (before, after))
    check("typed keys still reach the text box", box.input_value().count("f") > target["text"].count("f"))

    box.press("Escape")
    page.wait_for_timeout(300)
    check("Esc closes the panel", page.locator(".sqe-panel").count() == 0)
    check("Esc did not leave the player",
          page.evaluate("() => Boolean(document.querySelector('video.htmlvideoplayer'))"))

    # Tracks the button must stay hidden for, then one it must show for.
    movie3 = MOVIES["TestWebm 03"]
    play(page, movie3)
    t3 = tracks(movie3)
    embedded = [t for t in t3 if not t["external"]]
    vtt = [t for t in t3 if t["external"] and t["format"] != "srt"]
    srt = [t for t in t3 if t["external"] and t["format"] == "srt" and t["editable"]]
    if embedded:
        choose_subtitle(page, embedded[0]["streamIndex"])
        check("button hidden for an embedded subtitle", wait_button(page, False, 4) and not wait_button(page, True, 3))
    else:
        check("movie 3 has an embedded subtitle track", False, "none found")
    if vtt:
        choose_subtitle(page, vtt[0]["streamIndex"])
        check("button hidden for a non-.srt subtitle", wait_button(page, False, 4) and not wait_button(page, True, 3))
    else:
        check("movie 3 has an external .vtt track", False, "none found")
    if srt:
        choose_subtitle(page, srt[0]["streamIndex"])
        check("button appears after switching to the .srt", wait_button(page, True, 6))
    choose_subtitle(page, -1)
    check("button hidden with subtitles off", wait_button(page, False, 4) and not wait_button(page, True, 3))
    shot(page, "3c-admin-movie3")
    ctx.close()


def admin_phone(browser, pages):
    ctx = browser.new_context(viewport={"width": 390, "height": 844}, has_touch=True)
    page = ctx.new_page()
    pages.append(page)
    login(page, ADMIN_NAME, ADMIN_PW)
    movie = MOVIES["TestWebm 01"]
    en = [t for t in tracks(movie) if t["external"] and t["format"] == "srt" and t["editable"]]
    play(page, movie)
    choose_subtitle(page, en[0]["streamIndex"])
    ok = wait_button(page, True, 15)
    check("phone width: the button shows", ok)
    if ok:
        show_osd(page)
        page.locator(".sqe-edit-button").first.click()
        page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)
        box = page.locator(".sqe-panel").bounding_box()
        check("phone width: the panel is a full-width bottom sheet",
              box is not None and box["width"] >= 389 and abs(box["y"] + box["height"] - 844) <= 1
              and box["height"] <= 0.61 * 844, "box %r" % box)
        check("phone width: the text box is visible", page.locator(".sqe-panel .sqe-cue-text").first.is_visible())
        shot(page, "3c-phone-panel")
    ctx.close()


def non_admin(browser, pages):
    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)
    login(page, USER_NAME, USER_PW)
    play(page, MOVIES["TestWebm 01"])
    hidden = wait_button(page, False, 2) and not wait_button(page, True, 8)
    check("button hidden for a non-admin", hidden)
    state = page.evaluate("() => window.SubtitleQuickEdit && window.SubtitleQuickEdit.debugState()")
    check("the client knows the user is not an admin", bool(state) and state["isAdmin"] is False, repr(state))
    shot(page, "3c-non-admin")
    ctx.close()


with sync_playwright() as p:
    browser = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    for scenario in (admin_desktop, admin_phone, non_admin):
        pages = []
        try:
            scenario(browser, pages)
        except Exception as e:  # report and carry on with the next scenario
            check(scenario.__name__ + " ran to the end", False, type(e).__name__ + ": " + str(e).splitlines()[0][:200])
            if pages:
                try:
                    shot(pages[-1], scenario.__name__ + "-failed")
                    state = pages[-1].evaluate("() => window.SubtitleQuickEdit && window.SubtitleQuickEdit.debugState()")
                    print("     state at failure: " + json.dumps(state), flush=True)
                except Exception:
                    pass
            for page in pages:
                try:
                    page.context.close()
                except Exception:
                    pass
    browser.close()

print("%d of %d passed" % (sum(results), len(results)))
sys.exit(0 if all(results) else 1)
