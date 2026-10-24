#!/usr/bin/env python3
"""Stage 3 browser tests (3c button and panel, 3d editing, 3e saving, 3f adding, 3g phone width), in headless Chromium against the
THROWAWAY test Jellyfin. The saving checks change "TestWebm 01" subtitles through the editor and put the original text
back. The adding checks add a line to "TestWebm 03.en.srt", which the API cannot undo: run testdata/reset_webm03_srt.sh
before and after.

Runs inside the Playwright image (sqe-playwright:1.64.1, Playwright for Python) on a host that can reach JF_BASE:
  docker run --rm -i -e JF_BASE -v <folder holding testdata/browser>:/sqe:ro sqe-playwright:1.64.1 \
    python3 /sqe/testdata/browser/stage3_tests.py
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

def dump_failure(page, name):
    """Screenshot, panel status and debugState of a failed scenario's page; never raises."""
    try:
        shot(page, name + "-failed")
        state = page.evaluate("() => ({ status: (document.querySelector('.sqe-panel .sqe-status') || {}).textContent || null,"
                              " debug: window.SubtitleQuickEdit && window.SubtitleQuickEdit.debugState() })")
        print("     state at failure: " + json.dumps(state), flush=True)
    except Exception:
        pass


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


def admin_editing(browser, pages):
    """3d: Previous/Next, Apply, editing a line twice, clicking a row, undo; nothing is written to the file."""
    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)
    login(page, ADMIN_NAME, ADMIN_PW)
    movie = MOVIES["TestWebm 01"]
    en = [t for t in tracks(movie) if t["external"] and t["format"] == "srt" and t["editable"]][0]["streamIndex"]
    st, before_file = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (movie, en), token=TOKEN)
    all_cues = before_file["cues"]
    k = min(2, len(all_cues) - 2)
    play(page, movie)
    choose_subtitle(page, en)
    seek_into(page, all_cues[k])
    show_osd(page)
    wait_button(page, True, 10)
    show_osd(page)
    page.locator(".sqe-edit-button").first.click()
    page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)

    def shown():
        return page.evaluate("() => Array.from(document.querySelectorAll('.sqe-panel .sqe-cue')).map((c) =>"
                             " Number(c.getAttribute('data-cue-index')))")

    def at(cue):
        state = video_state(page)
        return state["paused"] and abs(state["time"] - cue["start"] / 1000.0) < 0.06

    page.locator(".sqe-next").click()
    page.wait_for_timeout(400)
    nxt = all_cues[k + 1]
    check("Next steps to the next line and seeks to its start", nxt["index"] in shown() and at(nxt),
          "shown %r, video %r" % (shown(), video_state(page)))
    page.locator(".sqe-prev").click()
    page.wait_for_timeout(200)
    page.locator(".sqe-prev").click()
    page.wait_for_timeout(400)
    prv = all_cues[k - 1]
    check("Previous steps back and seeks", prv["index"] in shown() and at(prv),
          "shown %r, video %r" % (shown(), video_state(page)))

    def rows():
        return page.evaluate("() => Array.from(document.querySelectorAll('.sqe-change')).map((r) =>"
                             " r.querySelector('.sqe-change-where').textContent + '|' + r.querySelector('.sqe-change-new').textContent)")

    box = page.locator(".sqe-panel .sqe-cue-text").first
    box.fill(prv["text"] + " X")
    box.press("Control+Enter")
    page.wait_for_timeout(200)
    r1 = rows()
    check("Apply adds a row to Changes this session", len(r1) == 1 and r1[0].startswith("Line %d" % prv["index"]), repr(r1))
    box = page.locator(".sqe-panel .sqe-cue-text").first
    box.fill(prv["text"] + " Y")
    page.locator(".sqe-apply").click()
    page.wait_for_timeout(200)
    r2 = rows()
    check("editing the same line again updates its row", len(r2) == 1 and r2[0].endswith(prv["text"] + " Y"), repr(r2))

    page.locator(".sqe-next").click()
    page.wait_for_timeout(300)
    page.locator(".sqe-change").first.click()
    page.wait_for_timeout(400)
    check("clicking a row seeks to that line", prv["index"] in shown() and at(prv),
          "shown %r, video %r" % (shown(), video_state(page)))
    page.locator(".sqe-undo").first.click()
    page.wait_for_timeout(200)
    check("undo removes the staged edit", rows() == [] and
          page.locator(".sqe-panel .sqe-cue-text").first.input_value() == prv["text"])
    st, after_file = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (movie, en), token=TOKEN)
    check("staging writes nothing to the file", after_file["hash"] == before_file["hash"])
    shot(page, "3d-editing")
    ctx.close()


def restore_texts(item_id, index, original_cues):
    """Puts every line of a subtitle back to its original text through the API (an edit with the old text is a no-op)."""
    body = cues_full(item_id, index)
    back = {c["index"]: c["text"] for c in original_cues}
    edits = [{"type": "edit", "cueIndex": c["index"], "oldText": c["text"], "newText": back[c["index"]]}
             for c in body["cues"] if c["index"] in back and c["text"] != back[c["index"]]]
    if edits:
        st, _ = api("POST", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Edits" % (item_id, index),
                    {"baseHash": body["hash"], "edits": edits}, token=TOKEN)
        if st != 200:
            print("     WARNING: could not restore the test subtitle (HTTP %s)" % st, flush=True)


def cues_full(item_id, index):
    st, body = api("GET", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Cues" % (item_id, index), token=TOKEN)
    if st != 200:
        sys.exit("Cues call failed (HTTP %s)" % st)
    return body


def api_edit(item_id, index, cue_index, new_text):
    body = cues_full(item_id, index)
    old = [c for c in body["cues"] if c["index"] == cue_index][0]["text"]
    st, res = api("POST", "/SubtitleQuickEdit/Items/%s/Subtitles/%d/Edits" % (item_id, index),
                  {"baseHash": body["hash"], "edits": [{"type": "edit", "cueIndex": cue_index, "oldText": old, "newText": new_text}]},
                  token=TOKEN)
    return st, res


def admin_saving(browser, pages):
    """3e: Save, Reload subtitles, a file changed on disk, drafts across a page reload, a Windows-1250 round trip."""
    movie = MOVIES["TestWebm 01"]
    t1 = tracks(movie)
    srt = [t for t in t1 if t["external"] and t["format"] == "srt" and t["editable"]]
    en = ([t for t in srt if t.get("language") in ("en", "eng")] or srt)[0]["streamIndex"]
    pl = [t for t in srt if t.get("language") in ("pl", "pol")]
    pl = pl[0]["streamIndex"] if pl else None
    original_en = cues_full(movie, en)
    original_pl = cues_full(movie, pl) if pl is not None else None
    dialogs = []
    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)
    page.on("dialog", lambda d: (dialogs.append(d.type), d.accept()))

    def status():
        return page.locator(".sqe-panel .sqe-status").text_content() or ""

    def wait_status(text, seconds=15):
        page.wait_for_function("(t) => { const s = document.querySelector('.sqe-panel .sqe-status');"
                               " return s && s.textContent.indexOf(t) >= 0; }", arg=text, timeout=seconds * 1000)

    def open_at(index, cue):
        play(page, movie)
        choose_subtitle(page, index)
        seek_into(page, cue)
        wait_button(page, True, 10)
        show_osd(page)
        page.locator(".sqe-edit-button").first.click()
        page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)

    def rows():
        return page.evaluate("() => Array.from(document.querySelectorAll('.sqe-change')).map((r) =>"
                             " ({ text: r.querySelector('.sqe-change-new').textContent,"
                             " status: r.querySelector('.sqe-badge').textContent }))")

    try:
        login(page, ADMIN_NAME, ADMIN_PW)
        cues0 = original_en["cues"]
        k = min(3, len(cues0) - 2)
        cue = cues0[k]
        open_at(en, cue)

        # Save: only the edited line changes in the file.
        box = page.locator(".sqe-panel .sqe-cue-text").first
        box.fill(cue["text"] + " (saved)")
        box.press("Control+Enter")
        box.press("Control+s")
        wait_status("Saved")
        after = cues_full(movie, en)
        changed = [c["index"] for c, o in zip(after["cues"], cues0) if c != o]
        check("Save writes the staged edit and only that line changes",
              len(after["cues"]) == len(cues0) and changed == [cue["index"]] and
              after["cues"][k]["text"] == cue["text"] + " (saved)", "changed lines %r" % changed)
        check("the saved row is marked saved", any(r["status"] == "saved" for r in rows()), repr(rows()))

        # Reload subtitles: the player shows the new text without restarting playback.
        page.evaluate("() => { window.__sqeVideo = document.querySelector('video.htmlvideoplayer'); }")
        before = video_state(page)
        page.locator(".sqe-reload").click()
        wait_status("Subtitles reloaded", 20)
        held = page.evaluate("""(t) => { const v = document.querySelector('video.htmlvideoplayer');
            const tr = Array.from(v.textTracks).find((x) => x.label === 'manualTrack');
            const c = tr && tr.cues ? Array.from(tr.cues).find((x) => x.startTime <= t && t < x.endTime) : null;
            return { text: c ? c.text : null, same: v === window.__sqeVideo }; }""", (cue["start"] + cue["end"]) / 2000.0)
        now_state = video_state(page)
        check("Reload subtitles shows the new text without restarting playback",
              held["text"] is not None and "(saved)" in held["text"] and held["same"] and now_state["paused"] and
              abs(now_state["time"] - before["time"]) < 0.06, "track %r, video %r -> %r" % (held, before, now_state))

        # The file changes on disk mid-session: Save says so and writes nothing.
        page.locator(".sqe-next").click()
        page.wait_for_timeout(300)
        nxt = cues0[k + 1]
        box = page.locator(".sqe-panel .sqe-cue-text").first
        box.fill(nxt["text"] + " (staged)")
        page.locator(".sqe-apply").click()
        st, _ = api_edit(movie, en, cues0[k - 1]["index"], cues0[k - 1]["text"] + " (changed on disk)")
        changed_hash = cues_full(movie, en)["hash"]
        page.locator(".sqe-save").click()
        wait_status("changed on disk")
        check("a file changed on disk gives the \"file changed\" message and writes nothing",
              st == 200 and cues_full(movie, en)["hash"] == changed_hash)
        check("the edit that still matches stays staged", any(r["text"].endswith("(staged)") and r["status"] == "staged" for r in rows()),
              repr(rows()))

        # Unsaved edits survive a page reload (and leaving asks first).
        page.reload(wait_until="domcontentloaded")
        page.wait_for_function("() => window.ApiClient && window.ApiClient.isLoggedIn && window.ApiClient.isLoggedIn()", timeout=20000)
        page.wait_for_timeout(1500)
        open_at(en, nxt)
        check("unsaved edits survive a page reload",
              "Restored 1" in status() and any(r["text"].endswith("(staged)") and r["status"] == "staged" for r in rows()),
              "status %r, rows %r" % (status(), rows()))
        check("reloading with unsaved edits asks first", "beforeunload" in dialogs, repr(dialogs))
        page.locator(".sqe-undo").first.click()

        # A Windows-1250 file with Polish characters round-trips.
        if pl is None:
            check("TestWebm 01 has a Polish .srt", False, "none found")
        else:
            pcues = original_pl["cues"]
            pcue = pcues[min(1, len(pcues) - 1)]
            page.keyboard.press("Escape")
            choose_subtitle(page, pl)
            seek_into(page, pcue)
            wait_button(page, True, 10)
            show_osd(page)
            page.locator(".sqe-edit-button").first.click()
            page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)
            box = page.locator(".sqe-panel .sqe-cue-text").first
            box.fill(pcue["text"] + " zażółć gęślą jaźń")
            box.press("Control+s")
            wait_status("Saved")
            pafter = cues_full(movie, pl)
            check("a Windows-1250 file with Polish characters round-trips",
                  pafter["encoding"] == original_pl["encoding"] and
                  [c for c in pafter["cues"] if c["index"] == pcue["index"]][0]["text"] == pcue["text"] + " zażółć gęślą jaźń",
                  "encoding %s -> %s" % (original_pl["encoding"], pafter["encoding"]))
        shot(page, "3e-saving")
    except Exception:
        dump_failure(page, "admin_saving")  # here, because the finally below closes the page
        raise
    finally:
        restore_texts(movie, en, original_en["cues"])
        if original_pl is not None:
            restore_texts(movie, pl, original_pl["cues"])
        restored = cues_full(movie, en)["hash"] == original_en["hash"] and (
            original_pl is None or cues_full(movie, pl)["hash"] == original_pl["hash"])
        check("the test subtitles are back to their original bytes", restored)
        ctx.close()


WEBM03_SRT_CUES = [(1000, 3000, "Good line one"), (4000, 6000, "Good line two")]  # as testdata/reset_webm03_srt.sh writes it


def admin_adding(browser, pages):
    """3f: add a line to "TestWebm 03.en.srt" (prefill, nudges, bad times, Preview, Save, renumbering, Reload).
    The API cannot remove a line, so the file stays changed: testdata/reset_webm03_srt.sh puts it back."""
    movie = MOVIES["TestWebm 03"]
    srt = [t for t in tracks(movie) if t["external"] and t["format"] == "srt" and t["editable"]]
    index = srt[0]["streamIndex"]
    before = cues_full(movie, index)
    if [(c["start"], c["end"], c["text"]) for c in before["cues"]] != WEBM03_SRT_CUES:
        check("TestWebm 03.en.srt is as reset_webm03_srt.sh writes it (run that script first)", False,
              "%d lines" % len(before["cues"]))
        return

    ctx = browser.new_context(viewport={"width": 1280, "height": 720})
    page = ctx.new_page()
    pages.append(page)

    def field(key):
        return page.locator(".sqe-new-line .sqe-" + key).input_value()

    def field_error(key):
        return page.evaluate("(k) => { const x = document.getElementById('sqe-new-' + k).closest('.sqe-field')"
                             ".querySelector('.sqe-field-error'); return x.hidden ? '' : x.textContent; }", key)

    def status():
        return page.locator(".sqe-panel .sqe-status").text_content() or ""

    def wait_status(text, seconds=15):
        page.wait_for_function("(t) => { const s = document.querySelector('.sqe-panel .sqe-status');"
                               " return s && s.textContent.indexOf(t) >= 0; }", arg=text, timeout=seconds * 1000)

    try:
        login(page, ADMIN_NAME, ADMIN_PW)
        play(page, movie)
        choose_subtitle(page, index)
        seek_into(page, {"start": 200, "end": 200})
        wait_button(page, True, 15)
        show_osd(page)
        page.locator(".sqe-edit-button").first.click()
        page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)
        page.locator(".sqe-add").click()
        page.wait_for_selector(".sqe-new-line", timeout=5000)

        # Prefill: start at the current time, end at the next line's start (it comes before start + 2 s).
        check("Add line prefills start at the current time and ends at the next line",
              field("start") == "00:00:00,200" and field("end") == "00:00:01,000",
              "start %s, end %s" % (field("start"), field("end")))
        page.locator(".sqe-start-plus").click()
        page.locator(".sqe-start-plus").click()
        page.locator(".sqe-start-minus").click()
        page.locator(".sqe-end-minus").click()
        check("the nudge buttons move start and end by 100 ms",
              field("start") == "00:00:00,300" and field("end") == "00:00:00,900",
              "start %s, end %s" % (field("start"), field("end")))

        # Bad times give a clear message and stage nothing.
        box = page.locator(".sqe-new-text")
        box.fill("Added by the 3f test")
        page.locator(".sqe-new-line .sqe-end").fill("00:00:00,100")
        page.locator(".sqe-apply").click()
        msg_order = field_error("end")
        page.locator(".sqe-new-line .sqe-end").fill("00:00:00,350")
        page.locator(".sqe-apply").click()
        msg_short = field_error("end")
        page.locator(".sqe-new-line .sqe-end").fill("05:00:00,000")
        page.locator(".sqe-apply").click()
        msg_runtime = field_error("end")
        check("bad times are refused with a clear message",
              msg_order == "A new line must end after it starts." and msg_short == "A new line must be at least 100 ms long."
              and msg_runtime == "A new line must not end after the end of the video." and page.locator(".sqe-change").count() == 0,
              "%r / %r / %r" % (msg_order, msg_short, msg_runtime))

        # Preview plays from 1 s before the start (here 0) to the end, then pauses.
        page.locator(".sqe-new-line .sqe-end").fill("00:00:00,900")
        page.locator(".sqe-preview").click()
        wait_status("Preview done", 10)
        st = video_state(page)
        check("Preview plays the new line's span and pauses at its end", st["paused"] and 0.85 <= st["time"] <= 1.4, repr(st))

        # Apply, then Save: the line lands first and every line is renumbered.
        page.locator(".sqe-apply").click()
        page.locator(".sqe-save").click()
        wait_status("Saved")
        after = cues_full(movie, index)
        got = [(c["index"], c["start"], c["end"], c["text"]) for c in after["cues"]]
        want = [(1, 300, 900, "Added by the 3f test")] + [(i + 2, a, b, t) for i, (a, b, t) in enumerate(WEBM03_SRT_CUES)]
        check("the saved line lands in time order and the lines are renumbered 1..n, nothing else changes", got == want, repr(got))
        rows = page.evaluate("() => Array.from(document.querySelectorAll('.sqe-change-where')).map((e) => e.textContent)")
        check("the changes list shows the new line's number", any(r.startswith("Line 1 (new)") for r in rows), repr(rows))

        # Reload: the new line shows on screen at its time.
        page.locator(".sqe-reload").click()
        wait_status("Subtitles reloaded", 20)
        held = page.evaluate("""() => { const v = document.querySelector('video.htmlvideoplayer');
            const tr = Array.from(v.textTracks).find((x) => x.label === 'manualTrack');
            const c = tr && tr.cues ? Array.from(tr.cues).find((x) => x.startTime <= 0.6 && 0.6 < x.endTime) : null;
            return c ? c.text : null; }""")
        check("after Reload subtitles, the new line is in the player's track at its time",
              held is not None and "Added by the 3f test" in held, repr(held))
        shot(page, "3f-adding")
    except Exception:
        dump_failure(page, "admin_adding")
        raise
    finally:
        ctx.close()


PHONE_LAYOUT = """() => {
    const panel = document.querySelector('.sqe-panel');
    const visible = (e) => e.getClientRects().length > 0;
    const small = Array.from(panel.querySelectorAll('button')).filter(visible)
        .filter((b) => b.getBoundingClientRect().height < 44).map((b) => b.className + ' ' + Math.round(b.getBoundingClientRect().height));
    const rows = Array.from(panel.querySelectorAll('.sqe-time-row')).map((r) =>
        new Set(Array.from(r.children).map((c) => Math.round(c.getBoundingClientRect().top))).size);
    return { small: small, rows: rows, overflow: panel.scrollWidth - panel.clientWidth };
}"""


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
        page.locator(".sqe-edit-button").first.tap()
        page.wait_for_selector(".sqe-panel .sqe-cue-text", timeout=10000)
        box = page.locator(".sqe-panel").bounding_box()
        check("phone width: the panel is a full-width bottom sheet",
              box is not None and box["width"] >= 389 and abs(box["y"] + box["height"] - 844) <= 1
              and box["height"] <= 0.61 * 844, "box %r" % box)
        check("phone width: the text box is visible", page.locator(".sqe-panel .sqe-cue-text").first.is_visible())
        shot(page, "3c-phone-panel")
        page.locator(".sqe-add").tap()
        page.wait_for_selector(".sqe-new-line", timeout=5000)
        layout = page.evaluate(PHONE_LAYOUT)
        check("phone width: every panel button is at least 44 px high", layout["small"] == [], repr(layout["small"]))
        check("phone width: each time field and its buttons fit on one row", layout["rows"] == [1, 1], repr(layout["rows"]))
        check("phone width: nothing in the panel scrolls sideways", layout["overflow"] <= 0, repr(layout["overflow"]))
        shot(page, "3g-phone-form")
        page.locator(".sqe-cancel").tap()
        page.locator(".sqe-close").tap()
        check("phone width: tapping close closes the panel", page.locator(".sqe-panel").count() == 0)
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
    for scenario in (admin_desktop, admin_editing, admin_saving, admin_adding, admin_phone, non_admin):
        pages = []
        try:
            scenario(browser, pages)
        except Exception as e:  # report and carry on with the next scenario
            check(scenario.__name__ + " ran to the end", False, type(e).__name__ + ": " + str(e).splitlines()[0][:200])
            if pages and not pages[-1].is_closed():
                dump_failure(pages[-1], scenario.__name__)
            for page in pages:
                try:
                    page.context.close()
                except Exception:
                    pass
    browser.close()

print("%d of %d passed" % (sum(results), len(results)))
sys.exit(0 if all(results) else 1)
