/*
 * Subtitle Editor page code: the edit button in the player and the side panel.
 * Served after logic.js in the same file (see ClientAssetsController), so window.SqeLogic is already defined.
 * What jellyfin-web 12.2 looks like (selectors, load order, Sessions lag) is recorded in docs/client-notes.md.
 * Stage 3e: editing, Previous/Next, Apply, the "Changes this session" list, Save, Reload subtitles, conflicts
 * (409) and drafts that survive a page reload.
 */
(function () {
    'use strict';

    // ---- selectors and settings: everything that depends on jellyfin-web's markup is here --------------------------

    const SEL = {
        video: 'video.htmlvideoplayer',
        osdButtonRow: '.videoOsdBottom .osdControls .buttons',
        subtitleButton: 'button.btnSubtitles',
        osdButton: '.videoOsdBottom button',
        menuItem: '.actionSheetMenuItem[data-id]',
        customSubtitle: '.videoSubtitles .videoSubtitlesInner', // jellyfin-web's own subtitle element (custom renderer)
    };
    const BODY_IDLE_CLASS = 'mouseIdle'; // on <body> while the OSD is hidden
    const TEXT_TRACK_LABEL = 'manualTrack'; // the one text track jellyfin-web fills with the chosen subtitle's cues
    const FLOATING_AFTER_MS = 3000; // no OSD button row this long after playback started: use the floating button
    const MENU_WINDOW_MS = 60000; // a menu choice counts as a subtitle choice this long after the subtitle button was clicked
    const SESSION_RETRY_MS = [500, 1000, 2000, 4000]; // Sessions may not show the item at the very start of playback
    const REFRESH_DELAY_MS = 100; // coalesces bursts of DOM changes into one refresh
    const RELOAD_OFF_MS = 1500; // Reload: how long the subtitle stays off before it is switched on again
    const RELOAD_CHECK_MS = 5000; // Reload: how long to wait for the player to show the saved text before the fallback
    const NUDGE_MS = 100; // the new-line form's -100 ms / +100 ms buttons
    const SEEK_INSIDE_MS = 200; // Previous, Next and a row click seek this far into a line, so the player shows that line
    const PREVIEW_LEAD_MS = 1000; // Preview starts this long before the new line
    const LOG = '[SubtitleQuickEdit]';

    const L = window.SqeLogic;
    if (!L) {
        console.error(LOG, 'the logic module is missing; the editor is off.');
        return;
    }

    console.log('SubtitleQuickEdit client loaded');

    const state = {
        client: null,
        userId: null,
        isAdmin: false,
        me: null, // the server's Me answer: what this user may do
        mode: null, // 'edit' (save directly), 'submit' (send for review) or null (no editor), from L.editorMode
        video: null, // the current <video>; a new one is created for every playback
        playbackSeq: 0, // bumped on every new playback, so late answers for an old one are ignored
        playbackStartedAt: 0,
        session: null, // { id, itemId, mediaSourceId, subtitleIndex, runtimeMs, requestedAt }
        sessionTried: false, // whether this playback's Sessions lookup has been started
        menu: null, // { index, at }: the last subtitle choice made in the subtitle menu
        lastOsdButton: null, // { kind: 'subtitles' | 'other', at }
        tracks: {}, // itemId -> promise of the plugin's subtitle list
        cues: {}, // 'itemId:streamIndex' -> { hash, encoding, format, timePrecisionMs, cues }, fetched once per playback
        models: {}, // 'itemId:streamIndex' -> StagingModel with the staged changes, kept for the life of the page
        target: null, // { itemId, streamIndex, track } when the active track is editable, else null
        panel: null, // the open panel's elements, or null
        resumeOnClose: false,
        preview: null, // the timer that pauses the video at the end of a new line's Preview
        showSavedText: false, // custom renderer: show the file's saved text until Reload subtitles (the player has the old)
    };

    // ---- small helpers -----------------------------------------------------------------------------------------------

    function now() {
        return Date.now();
    }

    function sameId(a, b) {
        return typeof a === 'string' && typeof b === 'string' &&
            a.replace(/-/g, '').toLowerCase() === b.replace(/-/g, '').toLowerCase();
    }

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) {
            node.className = className;
        }

        if (text !== undefined && text !== null) {
            node.textContent = text;
        }

        return node;
    }

    /** GET a server path as the logged-in user. Never logs the URL (media URLs carry the token). */
    function apiGet(path, params) {
        const client = window.ApiClient;
        if (!client) {
            return Promise.reject(new Error('Not connected to the server.'));
        }

        return client.getJSON(client.getUrl(path, params));
    }

    /** POST JSON as the logged-in user; resolves with the parsed answer (or null), rejects with the Response. */
    /** POSTs body as JSON. Resolves with the parsed reply, or with undefined when noReply (the server answers 204). */
    function apiPost(path, body, params, noReply) {
        const client = window.ApiClient;
        if (!client) {
            return Promise.reject(new Error('Not connected to the server.'));
        }

        return client.ajax({
            type: 'POST',
            url: client.getUrl(path, params),
            data: body === undefined ? undefined : JSON.stringify(body),
            contentType: 'application/json',
            // ApiClient parses the reply whenever dataType is 'json', and an empty 204 reply then fails to parse.
            dataType: body === undefined || noReply ? undefined : 'json',
        });
    }

    /** The status and error body of a rejected request: { status, body } (status 0 when it never reached the server). */
    function errorDetails(err) {
        if (err && typeof err.status === 'number' && typeof err.json === 'function') {
            return err.json().then(function (body) {
                return { status: err.status, body: body || {} };
            }, function () {
                return { status: err.status, body: {} };
            });
        }

        return Promise.resolve({ status: 0, body: { message: (err && err.message) || 'The request failed.' } });
    }

    /** A readable message from a rejected request: the plugin's error body when there is one. */
    function describeError(err) {
        if (err && typeof err.json === 'function') {
            return err.json().then(function (body) {
                return (body && body.message) || ('The server answered ' + err.status + '.');
            }, function () {
                return 'The server answered ' + err.status + '.';
            });
        }

        return Promise.resolve((err && err.message) || 'The request failed.');
    }

    // ---- who is logged in ------------------------------------------------------------------------------------------

    function checkUser() {
        const client = window.ApiClient;
        const userId = client && typeof client.getCurrentUserId === 'function' ? client.getCurrentUserId() : null;
        if (userId === state.userId) {
            return;
        }

        state.userId = userId;
        state.isAdmin = false;
        state.me = null;
        state.mode = null;
        state.tracks = {};
        state.cues = {};
        state.models = {};
        if (!userId) {
            scheduleRefresh();
            return;
        }

        loadMe().then(function () {
            scheduleRefresh();
        });
    }

    /** Asks the server what this user may do (the settings can change at any time). Resolves with the answer or null. */
    function loadMe() {
        const userId = state.userId;
        return apiGet('SubtitleQuickEdit/Me').then(function (me) {
            if (state.userId === userId) {
                state.me = me || null;
                state.isAdmin = Boolean(me && me.isAdmin);
                state.mode = L.editorMode(me);
            }

            return me || null;
        }, function () {
            return null; // Not known: no editor.
        });
    }

    // ---- which item and subtitle track are playing -----------------------------------------------------------------

    function loadSession(seq, attempt) {
        const client = window.ApiClient;
        if (!client) {
            return Promise.resolve();
        }

        const requestedAt = now();
        return apiGet('Sessions', { DeviceId: client.deviceId() }).then(function (sessions) {
            if (seq !== state.playbackSeq) {
                return;
            }

            const mine = (sessions || []).filter(function (s) {
                return s && s.NowPlayingItem && s.PlayState;
            })[0];
            if (!mine) {
                const retry = SESSION_RETRY_MS[attempt || 0];
                if (retry !== undefined) {
                    return new Promise(function (resolve) {
                        setTimeout(resolve, retry);
                    }).then(function () {
                        return loadSession(seq, (attempt || 0) + 1);
                    });
                }

                state.session = null;
                return evaluate();
            }

            const ticks = mine.NowPlayingItem.RunTimeTicks;
            state.session = {
                id: mine.Id,
                itemId: mine.NowPlayingItem.Id,
                mediaSourceId: mine.PlayState.MediaSourceId,
                subtitleIndex: typeof mine.PlayState.SubtitleStreamIndex === 'number' ? mine.PlayState.SubtitleStreamIndex : -1,
                runtimeMs: typeof ticks === 'number' ? Math.floor(ticks / 10000) : null,
                requestedAt: requestedAt,
            };
            return evaluate();
        }, function () {
            if (seq === state.playbackSeq) {
                state.session = null;
                return evaluate();
            }
        });
    }

    /** False when the page shows that subtitles are off (the text track is disabled); true otherwise or when unknown. */
    function textTrackShowing(video) {
        const tracks = video && video.textTracks;
        if (!tracks) {
            return true;
        }

        for (let i = 0; i < tracks.length; i++) {
            if (tracks[i].label === TEXT_TRACK_LABEL) {
                return tracks[i].mode !== 'disabled';
            }
        }

        return true;
    }

    /** The subtitle stream index that is on now, or null when there is none or it is not known. */
    function activeStreamIndex() {
        if (!textTrackShowing(state.video)) {
            return null;
        }

        const s = state.session;
        const resolved = L.resolveSubtitleTrack({
            sessionIndex: s ? s.subtitleIndex : null,
            sessionRequestedAtMs: s ? s.requestedAt : null,
            menuIndex: state.menu ? state.menu.index : null,
            menuClickedAtMs: state.menu ? state.menu.at : null,
        });
        return typeof resolved.index === 'number' && resolved.index >= 0 ? resolved.index : null;
    }

    function subtitleList(itemId) {
        if (!state.tracks[itemId]) {
            state.tracks[itemId] = apiGet('SubtitleQuickEdit/Items/' + encodeURIComponent(itemId) + '/Subtitles').catch(function (err) {
                delete state.tracks[itemId];
                throw err;
            });
        }

        return state.tracks[itemId];
    }

    /** Works out whether the active track is editable and updates the button. Resolves with state.target. */
    function evaluate() {
        const seq = state.playbackSeq;
        const s = state.session;
        const index = activeStreamIndex();
        if (!state.mode || !s || index === null || !sameId(s.mediaSourceId, s.itemId)) {
            // No item, no subtitle, or an alternate version (only the item's own streams are addressable).
            state.target = null;
            updateButton();
            return Promise.resolve(null);
        }

        return subtitleList(s.itemId).then(function (list) {
            if (seq !== state.playbackSeq || activeStreamIndex() !== index) {
                return state.target;
            }

            const track = (list || []).filter(function (t) {
                return t.streamIndex === index;
            })[0];
            state.target = track && track.editable ? { itemId: s.itemId, streamIndex: index, track: track } : null;
            updateButton();
            return state.target;
        }, function () {
            if (seq === state.playbackSeq) {
                state.target = null;
                updateButton();
            }

            return null;
        });
    }

    // ---- following the subtitle menu (Sessions shows a change only after a few seconds) ------------------------------

    document.addEventListener('click', function (e) {
        const target = e.target instanceof Element ? e.target : null;
        if (!target) {
            return;
        }

        const osdButton = target.closest(SEL.osdButton);
        if (osdButton && !osdButton.classList.contains('sqe-edit-button')) {
            state.lastOsdButton = { kind: osdButton.matches(SEL.subtitleButton) ? 'subtitles' : 'other', at: now() };
            return;
        }

        const item = target.closest(SEL.menuItem);
        if (!item || !state.lastOsdButton || state.lastOsdButton.kind !== 'subtitles' ||
            now() - state.lastOsdButton.at > MENU_WINDOW_MS) {
            return;
        }

        const index = L.parseMenuId(item.getAttribute('data-id'));
        if (index === null) {
            return; // "Secondary Subtitles" opens another menu; the editor only follows the primary track
        }

        state.menu = { index: index, at: now() };
        state.lastOsdButton = null;
        if (state.panel) {
            closePanel(); // the panel showed the old track's cues
        }

        evaluate();
        // Confirm with Sessions once it has caught up.
        const seq = state.playbackSeq;
        setTimeout(function () {
            if (seq === state.playbackSeq) {
                loadSession(seq);
            }
        }, L.SESSIONS_LAG_MS + 1000);
    }, true);

    // ---- the button ------------------------------------------------------------------------------------------------

    function makeButton(floating) {
        const button = el('button', floating ? 'sqe-edit-button sqe-floating' : 'sqe-edit-button autoSize paper-icon-button-light');
        button.type = 'button';
        const label = state.mode === 'submit' ? 'Suggest a subtitle fix' : 'Edit subtitle line';
        button.title = label;
        button.setAttribute('aria-label', label);
        const icon = el('span', floating ? 'material-icons edit' : 'xlargePaperIconButton material-icons edit');
        icon.setAttribute('aria-hidden', 'true');
        button.appendChild(icon);
        button.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openPanel();
        });
        return button;
    }

    function removeButtons(except) {
        document.querySelectorAll('.sqe-edit-button').forEach(function (b) {
            if (b !== except) {
                b.remove();
            }
        });
    }

    /** The bottom button row of the OSD view that is in use (old views can linger hidden in the DOM). */
    function currentButtonRow() {
        const rows = document.querySelectorAll(SEL.osdButtonRow);
        return rows.length > 0 ? rows[rows.length - 1] : null;
    }

    function updateButton() {
        if (!state.target || !state.video) {
            removeButtons(null);
            return;
        }

        const row = currentButtonRow();
        if (row) {
            let button = row.querySelector('.sqe-edit-button');
            if (!button) {
                button = makeButton(false);
                const subtitleButton = row.querySelector(SEL.subtitleButton);
                if (subtitleButton && subtitleButton.parentNode === row) {
                    row.insertBefore(button, subtitleButton.nextSibling);
                } else {
                    row.appendChild(button);
                }
            }

            removeButtons(button);
            return;
        }

        if (now() - state.playbackStartedAt < FLOATING_AFTER_MS) {
            setTimeout(scheduleRefresh, FLOATING_AFTER_MS);
            return;
        }

        let floating = document.querySelector('.sqe-edit-button.sqe-floating');
        if (!floating) {
            floating = makeButton(true);
            document.body.appendChild(floating);
        }

        removeButtons(floating);
    }

    // ---- drafts: staged changes survive a page reload ---------------------------------------------------------------

    const drafts = L.createDraftStore(function () {
        return window.localStorage;
    });

    // "Show style tags" (ASS and WebVTT): off by default, remembered in this browser.
    const SHOW_TAGS_KEY = 'sqe-show-style-tags';

    function showTagsSetting() {
        try {
            return window.localStorage.getItem(SHOW_TAGS_KEY) === '1';
        } catch (e) {
            return false;
        }
    }

    function rememberShowTags(on) {
        try {
            window.localStorage.setItem(SHOW_TAGS_KEY, on ? '1' : '0');
        } catch (e) {
            // Storage blocked: the toggle still works on this page.
        }
    }

    // ---- the session's original: what "Revert to original" puts back -------------------------------------------------
    // The hash of each track as it was the first time this browser tab loaded it. Kept in sessionStorage, so it lasts
    // through page reloads but not into a new tab; held in memory too, for when storage is blocked.

    const baselines = {};

    function baselineKey(target) {
        return 'sqe-baseline:' + state.userId + ':' + target.itemId + ':' + target.streamIndex;
    }

    function baselineHash(target) {
        const key = baselineKey(target);
        try {
            const stored = window.sessionStorage.getItem(key);
            if (stored) {
                baselines[key] = stored;
            }
        } catch (e) {
            // storage blocked: the copy in memory is used
        }

        return baselines[key] || null;
    }

    /** Records hash as the track's original unless one is already known for this session. */
    function rememberBaseline(target, hash) {
        if (!hash || baselineHash(target)) {
            return;
        }

        const key = baselineKey(target);
        baselines[key] = hash;
        try {
            window.sessionStorage.setItem(key, hash);
        } catch (e) {
            // storage blocked or full: the copy in memory lasts until the page is reloaded
        }
    }

    function persist(target, model) {
        drafts.save(state.userId, target.itemId, target.streamIndex, model.serialize());
        refreshOverlay();
    }

    // ---- staged changes on screen: what Apply stages shows in the player before it is saved ------------------------
    // jellyfin-web draws subtitles one of two ways: cues in a native text track ("manualTrack"), or its own element
    // that it fills on every timeupdate (the "custom" renderer, a user setting). Native cues are changed in place; the
    // custom element is overwritten after jellyfin-web's own timeupdate handler (ours is registered later, so runs after).

    function customSubtitleElement() {
        return document.querySelector(SEL.customSubtitle);
    }

    /** 'native', 'custom' or 'none': how the page shows the subtitle now. */
    function subtitleRenderer() {
        const track = manualTrack(state.video);
        if (track && track.mode !== 'disabled') {
            return 'native';
        }

        return customSubtitleElement() ? 'custom' : 'none';
    }

    /** The playing track's cues and its changes to show: { cues, edits, adds }, or null. */
    function overlaySource() {
        const target = state.target;
        const key = target ? cacheKey(target) : null;
        const model = key ? state.models[key] : null;
        const loaded = key ? state.cues[key] : null;
        if (!model || !loaded) {
            return null;
        }

        const rows = model.snapshot();
        return {
            format: loaded.format,
            cues: loaded.cues,
            edits: rows.filter(function (r) {
                return r.type === 'edit' && r.status === 'staged';
            }),
            // Saved new lines too: the player only has them after Reload subtitles.
            adds: rows.filter(function (r) {
                return r.type === 'add' && (r.status === 'staged' || r.status === 'saved');
            }),
        };
    }

    const DIRECTION_MARKS = /[\u200e\u200f\u202a-\u202e]/g;

    function editFor(src, cue) {
        return src.edits.filter(function (r) {
            return r.cueIndex === cue.index;
        })[0] || null;
    }

    /** Native track: staged text replaces the cue's text, staged new lines become cues; undone changes go back. */
    function overlayNative(track, src) {
        const byStart = {};
        const added = {};
        Array.prototype.slice.call(track.cues || []).forEach(function (c) {
            if (c.sqeAdded) {
                added[c.sqeAdded] = c;
                return;
            }

            const key = Math.round(c.startTime * 1000);
            (byStart[key] = byStart[key] || []).push(c);
        });
        const seen = {};
        src.cues.forEach(function (cue) {
            const key = Math.round(cue.start);
            const n = seen[key] || 0;
            seen[key] = n + 1;
            const shown = byStart[key] && byStart[key][n];
            if (!shown) {
                return; // not in the player's track (for example a subtitle offset shifts every cue)
            }

            const edit = editFor(src, cue);
            if (edit || shown.sqeTouched) {
                const want = L.screenText(edit ? edit.newText : cue.text, src.format);
                if (String(shown.text).replace(DIRECTION_MARKS, '') !== want) {
                    shown.text = want;
                }

                shown.sqeTouched = Boolean(edit);
            }
        });
        const wanted = {};
        src.adds.forEach(function (row) {
            wanted[row.clientId] = true;
            const existing = added[row.clientId];
            const shownText = L.screenText(row.text, src.format);
            const already = (byStart[Math.round(row.startMs)] || []).some(function (c) {
                return String(c.text).replace(DIRECTION_MARKS, '') === shownText;
            });
            if (existing) {
                existing.startTime = row.startMs / 1000;
                existing.endTime = row.endMs / 1000;
                existing.text = shownText;
            } else if (!already) {
                const cue = new window.VTTCue(row.startMs / 1000, row.endMs / 1000, shownText);
                cue.sqeAdded = row.clientId;
                track.addCue(cue);
            }
        });
        Object.keys(added).forEach(function (clientId) {
            if (!wanted[clientId]) {
                track.removeCue(added[clientId]);
            }
        });
    }

    /** Cue text as jellyfin-web would show it: escaped, line breaks, and <i>, <b>, <u> kept. */
    function cueMarkup(text) {
        return text.split('\n').map(function (line) {
            return line.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                .replace(/&lt;(\/?)([ibu])&gt;/gi, '<$1$2>');
        }).join('<br>');
    }

    /** Custom renderer: shows the staged (or, after a save, the saved) text of the line at the current time. */
    function overlayCustom(element, src) {
        const t = currentMs();
        const first = src.cues.filter(function (c) {
            return c.start <= t && t <= c.end; // jellyfin-web shows the first line in the file that covers the time
        })[0] || null;
        const edit = first ? editFor(src, first) : null;
        const add = src.adds.filter(function (r) {
            return r.startMs <= t && t <= r.endMs;
        })[0] || null;
        let text = null;
        if (edit) {
            text = edit.newText;
        } else if (state.showSavedText || element.sqeOverridden) {
            text = first ? first.text : (add ? add.text : null);
        } else if (!first && add) {
            text = add.text;
        }

        if (text === null && !element.sqeOverridden) {
            return; // nothing of ours here: jellyfin-web's text stays
        }

        element.sqeOverridden = Boolean(edit || state.showSavedText || (!first && add));
        if (text === null) {
            element.classList.add('hide');
        } else {
            element.innerHTML = cueMarkup(L.screenText(text, src.format));
            element.classList.remove('hide');
        }
    }

    /** What a staging note says: shown in the player, unless the player draws the subtitle itself (ASS). */
    function stagedNote(what) {
        return subtitleRenderer() === 'none'
            ? what + '. Save writes it to the file, and Reload subtitles then shows it.'
            : what + ' and shown in the player. Save writes it to the file.';
    }

    function refreshOverlay() {
        const src = overlaySource();
        if (!src || !state.video) {
            return;
        }

        const renderer = subtitleRenderer();
        if (renderer === 'native') {
            overlayNative(manualTrack(state.video), src);
        } else if (renderer === 'custom') {
            overlayCustom(customSubtitleElement(), src);
        }
    }

    function onVideoTime() {
        if (subtitleRenderer() === 'custom') {
            refreshOverlay();
        }
    }

    function anyUnsaved() {
        return Object.keys(state.models).some(function (key) {
            return state.models[key].hasUnsaved();
        });
    }

    // Leaving or reloading the page with unsaved changes asks first. (The drafts are kept either way and come back
    // the next time the panel opens on that subtitle.)
    window.addEventListener('beforeunload', function (e) {
        if (anyUnsaved()) {
            e.preventDefault();
            e.returnValue = '';
        }
    });

    // ---- the panel -------------------------------------------------------------------------------------------------

    function cacheKey(target) {
        return target.itemId + ':' + target.streamIndex;
    }

    function loadCues(target) {
        const key = cacheKey(target);
        if (state.cues[key]) {
            return Promise.resolve(state.cues[key]);
        }

        return apiGet('SubtitleQuickEdit/Items/' + encodeURIComponent(target.itemId) + '/Subtitles/' + target.streamIndex + '/Cues')
            .then(function (body) {
                const loaded = {
                    hash: body.hash,
                    encoding: body.encoding,
                    format: body.format || 'srt',
                    timePrecisionMs: body.timePrecisionMs || 1,
                    cues: body.cues || [],
                };
                state.cues[key] = loaded;
                rememberBaseline(target, loaded.hash);
                return loaded;
            });
    }

    /**
     * The staged changes of a track. Kept for the life of the page, so closing the panel does not lose them; the first
     * time, a draft left by an earlier page is restored. Returns { model, restored } (restored: a status note or null).
     */
    function modelFor(target, loaded) {
        const key = cacheKey(target);
        let model = state.models[key];
        let restored = null;
        if (!model) {
            model = new L.StagingModel({ runtimeMs: state.session ? state.session.runtimeMs : null, format: loaded.format });
            const draft = drafts.load(state.userId, target.itemId, target.streamIndex);
            if (draft) {
                const summary = model.restore(draft, loaded.cues, loaded.hash);
                if (summary.restored.length + summary.failed.length > 0) {
                    restored = 'Restored ' + summary.restored.length + ' unsaved change' + (summary.restored.length === 1 ? '' : 's') +
                        ' from before.' + (summary.failed.length > 0 ? ' ' + summary.failed.length + ' no longer match the file.' : '');
                }
            } else {
                model.setCues(loaded.cues, loaded.hash);
            }

            state.models[key] = model;
        } else if (model.baseHash !== loaded.hash) {
            // The file changed since these edits were staged: keep what still matches, mark the rest failed.
            model.reconcile(loaded.cues, loaded.hash);
        } else {
            model.setCues(loaded.cues, loaded.hash);
        }

        persist(target, model);
        return { model: model, restored: restored };
    }

    function button(className, text, title, onClick) {
        const b = el('button', 'sqe-button ' + className, text);
        b.type = 'button';
        if (title) {
            b.title = title;
        }

        b.addEventListener('click', onClick);
        return b;
    }

    function buildPanel() {
        const submitting = state.mode === 'submit';
        const root = el('aside', 'sqe-panel' + (submitting ? ' sqe-submit-mode' : ''));
        root.setAttribute('role', 'dialog');
        root.setAttribute('aria-label', 'Subtitle Editor');
        const header = el('header', 'sqe-header');
        const title = el('h2', 'sqe-title', submitting ? 'Suggest a subtitle fix' : 'Edit subtitle line');
        const close = el('button', 'sqe-close', '×');
        close.type = 'button';
        close.title = 'Close (Esc)';
        close.setAttribute('aria-label', 'Close');
        close.addEventListener('click', function () {
            closePanel();
        });
        header.appendChild(title);
        header.appendChild(close);
        const status = el('p', 'sqe-status');
        status.setAttribute('role', 'status');
        const nav = el('div', 'sqe-nav');
        nav.appendChild(button('sqe-prev', '‹ Previous', 'Previous line', function () {
            step(-1);
        }));
        nav.appendChild(button('sqe-add', '+ Add line', 'Add a new line at the current time', function () {
            openForm(null);
        }));
        nav.appendChild(button('sqe-next', 'Next ›', 'Next line', function () {
            step(1);
        }));
        const tags = el('label', 'sqe-tags');
        const tagsBox = el('input', 'sqe-tags-toggle');
        tagsBox.type = 'checkbox';
        tagsBox.checked = showTagsSetting();
        tagsBox.addEventListener('change', function () {
            rememberShowTags(tagsBox.checked);
            toggleTags();
        });
        tags.appendChild(tagsBox);
        tags.appendChild(document.createTextNode(' Show style tags'));
        tags.title = 'Show and edit the styling codes in each line, such as {\\an8} or <i>';
        tags.hidden = true;
        const cues = el('div', 'sqe-cues');
        const actions = el('div', 'sqe-actions');
        actions.appendChild(button('sqe-apply', 'Apply', 'Stage the change (Ctrl+Enter)', function () {
            applyCurrent();
        }));
        let note = null;
        if (submitting) {
            actions.appendChild(button('sqe-submit sqe-primary', 'Submit for review',
                'Send the staged changes to an admin, who decides whether they go into the file (Ctrl+S)', function () {
                    submitChanges();
                }));
            note = el('textarea', 'sqe-note');
            note.rows = 2;
            note.maxLength = (state.me && state.me.maxNoteLength) || 500;
            note.placeholder = 'Note for the admin (optional)';
            note.setAttribute('aria-label', 'Note for the admin');
            actions.insertBefore(note, actions.firstChild);
        } else {
            actions.appendChild(button('sqe-save sqe-primary', 'Save', 'Write the staged changes to the file (Ctrl+S)', function () {
                saveChanges();
            }));
            actions.appendChild(button('sqe-reload', 'Reload subtitles', 'Make the player show the saved file', function () {
                reloadSubtitles();
            }));
            actions.appendChild(button('sqe-revert', 'Revert to original',
                'Put the file back as it was before your changes this session, and drop unsaved changes', function () {
                    revertToOriginal();
                }));
        }

        const changes = el('section', 'sqe-changes');
        const changesTitle = el('h3', 'sqe-changes-title', submitting ? 'Changes to submit' : 'Changes this session');
        const list = el('ol', 'sqe-change-list');
        changes.appendChild(changesTitle);
        changes.appendChild(list);
        const mine = el('section', 'sqe-mine');
        mine.appendChild(el('h3', 'sqe-mine-title', 'Your submissions for this subtitle'));
        const mineList = el('ol', 'sqe-mine-list');
        mine.appendChild(mineList);
        const pending = el('a', 'sqe-pending');
        pending.href = '#/configurationpage?name=SubtitleQuickEdit';
        pending.hidden = true;
        pending.addEventListener('click', function () {
            closePanel();
        });
        root.appendChild(header);
        root.appendChild(pending);
        if (submitting) {
            root.appendChild(el('p', 'sqe-hint', 'Your changes go to an admin, who decides whether they go into the subtitle file.'));
        }

        root.appendChild(status);
        root.appendChild(nav);
        root.appendChild(tags);
        root.appendChild(cues);
        root.appendChild(actions);
        root.appendChild(changes);
        root.appendChild(mine);
        nav.hidden = true;
        actions.hidden = true;
        changes.hidden = true;
        mine.hidden = true;
        return {
            root: root, status: status, nav: nav, tags: tags, tagsBox: tagsBox, cues: cues, actions: actions, changes: changes,
            list: list, note: note,
            mine: mine, mineList: mineList, pending: pending, submitting: submitting, shown: [], form: null,
        };
    }

    /** The panel's main button: Save, or Submit for review. */
    function primaryAction() {
        if (state.panel && state.panel.submitting) {
            submitChanges();
        } else {
            saveChanges();
        }
    }

    /** Admins: a link to the review list in the settings page, when submissions wait. */
    function showPendingReviews() {
        const panel = state.panel;
        const count = state.me && typeof state.me.pendingReviews === 'number' ? state.me.pendingReviews : 0;
        if (!panel) {
            return;
        }

        panel.pending.hidden = count === 0;
        panel.pending.textContent = count === 1 ? '1 submission is waiting for your review'
            : count + ' submissions are waiting for your review';
    }

    function showStatus(text, isError) {
        if (!state.panel) {
            return;
        }

        state.panel.status.textContent = text || '';
        state.panel.status.classList.toggle('sqe-error', Boolean(isError));
    }

    const MODE_NOTES = {
        previous: 'No line on screen: this is the previous line.',
        next: 'No line yet: this is the first line.',
    };

    /** The unsaved row of a cue, if it has one. */
    function stagedRowFor(model, cue) {
        return model.snapshot().filter(function (r) {
            return r.type === 'edit' && r.cueIndex === cue.index && r.status !== 'saved';
        })[0] || null;
    }

    /** True when the panel hides the style tags at the edges of each line (an ASS or WebVTT file, toggle off). */
    function hidesTags(panel) {
        return Boolean(panel.loaded && L.hasStyleTags(panel.loaded.format) && !panel.tagsBox.checked);
    }

    /**
     * Shows cues with a text box each. pending (optional) maps a cue index to text to show instead of the staged or
     * file text, so switching "Show style tags" keeps what was typed.
     */
    function renderCues(shown, note, pending) {
        const panel = state.panel;
        const box = panel.cues;
        box.textContent = '';
        stopPreview();
        panel.form = null;
        panel.shown = shown.cues;
        panel.nav.hidden = false;
        panel.actions.hidden = shown.cues.length === 0;
        if (shown.cues.length === 0) {
            showStatus('This subtitle has no lines.');
            return;
        }

        showStatus(note === undefined ? (MODE_NOTES[shown.mode] || '') : note);
        shown.cues.forEach(function (cue) {
            const row = stagedRowFor(panel.model, cue);
            const item = el('section', 'sqe-cue');
            item.setAttribute('data-cue-index', String(cue.index));
            const heading = el('div', 'sqe-cue-heading');
            heading.appendChild(el('span', 'sqe-cue-number', 'Line ' + cue.index));
            heading.appendChild(el('span', 'sqe-cue-time', L.formatTime(cue.start) + ' → ' + L.formatTime(cue.end)));
            if (cue.style) {
                heading.appendChild(el('span', 'sqe-cue-style', cue.style));
            }

            if (row) {
                heading.appendChild(el('span', 'sqe-badge sqe-badge-' + row.status, row.status));
            }

            const full = pending && pending[cue.index] !== undefined ? pending[cue.index] : (row ? row.newText : cue.text);
            const parts = hidesTags(panel) ? L.splitStyleTags(full, panel.loaded.format) : { prefix: '', body: full, suffix: '' };
            if (parts.prefix || parts.suffix) {
                const styled = el('span', 'sqe-badge sqe-badge-styled', 'styled');
                styled.title = 'This line has style tags, kept as they are. Turn on "Show style tags" to edit them.';
                heading.appendChild(styled);
            }

            const text = el('textarea', 'sqe-cue-text');
            text.value = parts.body;
            text.rows = Math.max(2, parts.body.split('\n').length);
            text.setAttribute('aria-label', 'Text of line ' + cue.index);
            item.sqeParts = parts;
            const error = el('p', 'sqe-field-error');
            error.hidden = true;
            item.appendChild(heading);
            item.appendChild(text);
            if (cue.readOnly) {
                text.readOnly = true;
                item.appendChild(el('p', 'sqe-hint', 'This line is a drawing, so it cannot be edited here.'));
            }

            item.appendChild(error);
            box.appendChild(item);
        });
        updateNav();
        const first = box.querySelector('textarea:not([readonly])');
        if (first) {
            first.focus();
        }
    }

    /** The full text a cue's box stands for: the hidden style tags put back around what is typed. */
    function boxText(item) {
        const parts = item.sqeParts || { prefix: '', suffix: '' };
        return parts.prefix + item.querySelector('textarea').value + parts.suffix;
    }

    /** "Show style tags" changed: shows the boxes again in the other mode, keeping what was typed. */
    function toggleTags() {
        const panel = state.panel;
        if (!panel || panel.form || !panel.shown || panel.shown.length === 0) {
            return;
        }

        const pending = {};
        panel.cues.querySelectorAll('.sqe-cue').forEach(function (item) {
            pending[Number(item.getAttribute('data-cue-index'))] = boxText(item);
        });
        renderCues({ mode: 'active', cues: panel.shown }, '', pending);
    }

    function updateNav() {
        const panel = state.panel;
        const cues = panel.loaded ? panel.loaded.cues : [];
        panel.nav.querySelector('.sqe-prev').disabled = !L.stepCue(cues, panel.shown, -1);
        panel.nav.querySelector('.sqe-next').disabled = !L.stepCue(cues, panel.shown, 1);
        panel.nav.querySelector('.sqe-add').disabled = !panel.model;
    }

    /** Moves the video to a time (ms) and keeps it paused. */
    function seekTo(ms) {
        const video = state.video;
        if (video && typeof ms === 'number') {
            video.currentTime = ms / 1000;
        }
    }

    /**
     * A moment just inside a line: a little after its start. At the exact start the player may still show the line
     * before (jellyfin-web's own renderer counts a line's end as on screen, and a seek can land a frame early).
     */
    function insideLine(start, end) {
        return start + Math.min(SEEK_INSIDE_MS, Math.max(0, (end - start) / 2));
    }

    /** Shows a cue: seeks just inside it and shows every line on screen at that moment (overlaps included). */
    function showCue(cue) {
        const at = insideLine(cue.start, cue.end);
        seekTo(at);
        renderCues(L.cueForTime(state.panel.loaded.cues, at), '');
    }

    function step(direction) {
        const panel = state.panel;
        if (!panel || !panel.loaded) {
            return;
        }

        const target = L.stepCue(panel.loaded.cues, panel.shown, direction);
        if (target) {
            showCue(target);
        }
    }

    /** Stages every changed text box (the Apply button and Ctrl+Enter). */
    function applyEdits() {
        const panel = state.panel;
        if (!panel || !panel.model) {
            return;
        }

        let staged = 0;
        let removed = 0;
        let failed = 0;
        panel.cues.querySelectorAll('.sqe-cue').forEach(function (item) {
            const index = Number(item.getAttribute('data-cue-index'));
            const cue = panel.shown.filter(function (c) {
                return c.index === index;
            })[0];
            const text = item.querySelector('textarea');
            const error = item.querySelector('.sqe-field-error');
            if (!cue || cue.readOnly) {
                return;
            }

            const value = boxText(item);
            const row = stagedRowFor(panel.model, cue);
            if (L.normalizeText(value) === (row ? row.newText : L.normalizeText(cue.text))) {
                return; // unchanged since the last Apply
            }

            const parts = item.sqeParts;
            const result = parts && (parts.prefix || parts.suffix) && L.normalizeText(text.value).trim().length === 0
                ? { ok: false, error: { message: 'A line must keep some text besides its style tags.' } }
                : panel.model.stageEdit(cue, value);
            if (!result.ok) {
                error.textContent = result.error.message;
                error.hidden = false;
                failed++;
                return;
            }

            error.hidden = true;
            if (result.row) {
                staged++;
            } else if (result.removed) {
                removed++;
            }
        });
        persist(panel.target, panel.model);
        renderChanges();
        if (failed === 0) {
            renderCues({ mode: 'active', cues: panel.shown }, staged + removed === 0 ? 'Nothing changed.' :
                (staged > 0 ? stagedNote('Staged') + ' ' : '') + (removed > 0 ? 'Back to the original text, so the change was dropped.' : ''));
        }
    }

    // ---- adding a line ---------------------------------------------------------------------------------------------

    function currentMs() {
        return L.secondsToMs(state.video ? state.video.currentTime : 0) || 0;
    }

    /** The form's values: { startMs, endMs, text }; a time that does not parse is null. */
    function readForm() {
        const form = state.panel.form;
        return { startMs: L.parseTime(form.start.value), endMs: L.parseTime(form.end.value), text: form.text.value };
    }

    function fieldError(key, message) {
        const error = state.panel.form.errors[key];
        error.textContent = message || '';
        error.hidden = !message;
    }

    function setField(input, key, ms) {
        input.value = L.formatTime(ms);
        fieldError(key, null);
        updateFormWarning();
    }

    /** Shows the overlap warning for the times in the form (existing cues and other staged new lines). */
    function updateFormWarning() {
        const panel = state.panel;
        const form = panel.form;
        const line = readForm();
        let warning = null;
        if (line.startMs !== null && line.endMs !== null && line.endMs > line.startMs) {
            warning = L.overlapWarning(L.findOverlaps(panel.loaded.cues, line.startMs, line.endMs),
                panel.model.stagedOverlaps(line.startMs, line.endMs, form.clientId).length);
        }

        form.warning.textContent = warning || '';
        form.warning.hidden = !warning;
    }

    /** A time field with its "Set to now" and -100 ms / +100 ms buttons. */
    function timeField(form, key, label, ms) {
        const wrap = el('div', 'sqe-field');
        const input = el('input', 'sqe-time sqe-' + key);
        input.type = 'text';
        input.id = 'sqe-new-' + key;
        input.value = L.formatTime(ms);
        input.autocomplete = 'off';
        input.spellcheck = false;
        input.placeholder = 'hh:mm:ss,mmm';
        input.addEventListener('input', function () {
            fieldError(key, null);
            updateFormWarning();
        });
        const caption = el('label', 'sqe-label', label);
        caption.htmlFor = input.id;
        const row = el('div', 'sqe-time-row');
        function nudge(delta) {
            const value = L.parseTime(input.value);
            setField(input, key, L.nudgeTime(value === null ? currentMs() : value, delta, state.panel.model.runtimeMs));
        }

        row.appendChild(input);
        row.appendChild(button('sqe-nudge sqe-' + key + '-minus', '−100 ms', label + ' 100 ms earlier', function () {
            nudge(-NUDGE_MS);
        }));
        row.appendChild(button('sqe-nudge sqe-' + key + '-plus', '+100 ms', label + ' 100 ms later', function () {
            nudge(NUDGE_MS);
        }));
        row.appendChild(button('sqe-now sqe-' + key + '-now', 'Set to now', 'Use the current playback time', function () {
            setField(input, key, currentMs());
        }));
        const error = el('p', 'sqe-field-error');
        error.hidden = true;
        wrap.appendChild(caption);
        wrap.appendChild(row);
        wrap.appendChild(error);
        form[key] = input;
        form.errors[key] = error;
        return wrap;
    }

    /**
     * Opens the new-line form in place of the cue boxes: empty and prefilled at the current time, or with a staged (or
     * failed) new line from the changes list so its text or timing can be changed.
     */
    function openForm(row) {
        const panel = state.panel;
        if (!panel || !panel.model || panel.busy) {
            return;
        }

        stopPreview();
        let line;
        if (row) {
            line = { startMs: row.startMs, endMs: row.endMs, text: row.text };
        } else {
            const d = L.defaultNewLine(panel.loaded.cues, currentMs(), panel.model.runtimeMs);
            line = { startMs: d.startMs, endMs: d.endMs, text: '' };
        }

        const form = { clientId: row ? row.clientId : null, errors: {} };
        panel.form = form;
        const box = panel.cues;
        box.textContent = '';
        const item = el('section', 'sqe-new-line');
        const heading = el('div', 'sqe-cue-heading');
        heading.appendChild(el('span', 'sqe-cue-number', 'New line'));
        if (row) {
            heading.appendChild(el('span', 'sqe-badge sqe-badge-' + row.status, row.status));
        }

        item.appendChild(heading);
        item.appendChild(timeField(form, 'start', 'Start', line.startMs));
        item.appendChild(timeField(form, 'end', 'End', line.endMs));
        const textWrap = el('div', 'sqe-field');
        const text = el('textarea', 'sqe-cue-text sqe-new-text');
        text.id = 'sqe-new-text';
        text.value = line.text;
        text.rows = Math.max(2, line.text.split('\n').length);
        const caption = el('label', 'sqe-label', 'Text');
        caption.htmlFor = text.id;
        const textError = el('p', 'sqe-field-error');
        textError.hidden = true;
        text.addEventListener('input', function () {
            fieldError('text', null);
        });
        textWrap.appendChild(caption);
        textWrap.appendChild(text);
        textWrap.appendChild(textError);
        item.appendChild(textWrap);
        form.text = text;
        form.errors.text = textError;
        if (panel.loaded.timePrecisionMs > 1) {
            item.appendChild(el('p', 'sqe-hint', 'This file keeps times to ' + panel.loaded.timePrecisionMs +
                ' ms, so the times are rounded to that when saved.'));
        }

        form.warning = el('p', 'sqe-warning');
        form.warning.hidden = true;
        item.appendChild(form.warning);
        const buttons = el('div', 'sqe-form-buttons');
        buttons.appendChild(button('sqe-preview', 'Preview', 'Play from 1 s before the start to the end', function () {
            preview();
        }));
        buttons.appendChild(button('sqe-cancel', 'Cancel', 'Back to the lines', function () {
            renderCues({ mode: 'active', cues: state.panel.shown }, '');
        }));
        item.appendChild(buttons);
        box.appendChild(item);
        panel.actions.hidden = false;
        if (row && row.message) {
            showStatus(row.message, true);
        } else {
            showStatus(row ? 'Change the times or the text, then Apply.' : 'Set the times and type the text, then Apply.');
        }

        updateFormWarning();
        text.focus();
    }

    /** Stages the form's new line, or updates the staged one it was opened from. True when it was staged. */
    function applyForm() {
        const panel = state.panel;
        const form = panel.form;
        const line = readForm();
        ['start', 'end', 'text'].forEach(function (key) {
            fieldError(key, null);
        });
        const result = form.clientId ? panel.model.updateAdd(form.clientId, line) : panel.model.stageAdd(line);
        if (!result.ok) {
            const shown = {};
            result.errors.forEach(function (e) {
                if (form.errors[e.field] && !shown[e.field]) {
                    shown[e.field] = true;
                    fieldError(e.field, e.message);
                }
            });
            showStatus(shown.start || shown.end || shown.text ? 'Fix the marked fields, then Apply.' : result.errors[0].message, true);
            return false;
        }

        persist(panel.target, panel.model);
        renderChanges();
        renderCues(L.cueForTime(panel.loaded.cues, result.row.startMs),
            form.clientId ? 'New line updated.' : stagedNote('New line staged'));
        return true;
    }

    /**
     * The Apply button and Ctrl+Enter: stages the form or the changed text boxes. For Save, an untouched empty form is
     * left out. False when the form could not be staged.
     */
    function applyCurrent(forSave) {
        const panel = state.panel;
        if (!panel || !panel.model) {
            return false;
        }

        if (panel.form) {
            if (forSave && !panel.form.clientId && panel.form.text.value.trim() === '') {
                return true;
            }

            return applyForm();
        }

        applyEdits();
        return true;
    }

    /** Plays from 1 s before the new line's start to its end, then pauses, so the timing can be checked by ear. */
    function preview() {
        const video = state.video;
        const line = readForm();
        if (!video) {
            return;
        }

        if (line.startMs === null || line.endMs === null || line.endMs <= line.startMs) {
            showStatus('Fix the start and end times before previewing.', true);
            return;
        }

        stopPreview();
        video.currentTime = Math.max(0, line.startMs - PREVIEW_LEAD_MS) / 1000;
        showStatus('Previewing ' + L.formatTime(line.startMs) + ' to ' + L.formatTime(line.endMs) + '…');
        state.preview = setInterval(function () {
            if (state.video !== video || video.ended || video.currentTime * 1000 >= line.endMs) {
                stopPreview();
                if (state.panel && state.panel.form) {
                    showStatus('Preview done.');
                }
            }
        }, 50);
        const played = video.play();
        if (played && typeof played.catch === 'function') {
            played.catch(function () {
                stopPreview();
                showStatus('The browser did not start playback; press play on the video.', true);
            });
        }
    }

    /** Ends a running Preview and pauses the video. */
    function stopPreview() {
        if (state.preview !== null) {
            clearInterval(state.preview);
            state.preview = null;
            if (state.video && !state.video.paused) {
                state.video.pause();
            }
        }
    }

    function changeRow(row) {
        const item = el('li', 'sqe-change sqe-change-' + row.status);
        item.setAttribute('data-row-id', row.rowId);
        const top = el('div', 'sqe-change-top');
        const where = row.type === 'add'
            ? (row.status === 'saved' && row.position ? 'Line ' + row.position + ' (new)' : 'New') + ' · ' +
              L.formatTime(row.startMs) + ' → ' + L.formatTime(row.endMs)
            : 'Line ' + row.cueIndex + ' · ' + L.formatTime(row.start);
        top.appendChild(el('span', 'sqe-change-where', where));
        top.appendChild(el('span', 'sqe-badge sqe-badge-' + row.status, row.status));
        item.appendChild(top);
        if (row.type === 'add') {
            item.appendChild(el('div', 'sqe-change-new', row.text));
        } else {
            item.appendChild(el('div', 'sqe-change-old', row.oldText));
            item.appendChild(el('div', 'sqe-change-new', row.newText));
        }

        if (row.message) {
            item.appendChild(el('div', 'sqe-change-message', row.message));
        }

        if (row.status !== 'saved') {
            const undo = button('sqe-undo', 'Undo', 'Drop this change', function (e) {
                e.stopPropagation();
                state.panel.model.undo(row.rowId);
                persist(state.panel.target, state.panel.model);
                renderChanges();
                renderCues({ mode: 'active', cues: state.panel.shown }, 'Change dropped.');
            });
            item.appendChild(undo);
        } else if (row.type === 'edit') {
            const undoSaved = button('sqe-undo-saved', 'Undo', 'Stage the old text again (Save writes it)', function (e) {
                e.stopPropagation();
                const panel = state.panel;
                const result = panel.model.undoSaved(row.rowId);
                if (!result.ok) {
                    showStatus(result.error, true);
                    return;
                }

                persist(panel.target, panel.model);
                renderChanges();
                const cue = panel.loaded.cues.filter(function (c) {
                    return c.index === row.cueIndex;
                })[0];
                if (cue) {
                    showCue(cue);
                }

                showStatus(stagedNote('The old text is staged'));
            });
            item.appendChild(undoSaved);
        }

        item.tabIndex = 0;
        item.title = row.type === 'add' && row.status !== 'saved' ? 'Change this new line' : 'Show this line';
        item.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && e.target === item) {
                item.click();
            }
        });
        item.addEventListener('click', function () {
            const panel = state.panel;
            if (row.type === 'add' && row.status !== 'saved') {
                seekTo(insideLine(row.startMs, row.endMs));
                openForm(row);
                return;
            }

            const at = row.type === 'add' ? row.startMs : row.start;
            const cue = row.type === 'edit' ? panel.loaded.cues.filter(function (c) {
                return c.index === row.cueIndex;
            })[0] : null;
            if (cue) {
                showCue(cue);
            } else {
                seekTo(at);
                renderCues(L.cueForTime(panel.loaded.cues, at), '');
            }
        });
        return item;
    }

    function renderChanges() {
        const panel = state.panel;
        const rows = panel.model.sortedRows();
        panel.list.textContent = '';
        rows.forEach(function (row) {
            panel.list.appendChild(changeRow(row));
        });
        panel.changes.hidden = rows.length === 0;
        panel.actions.querySelector('.sqe-primary').disabled = panel.busy || !rows.some(function (r) {
            return r.status === 'staged';
        });
        const revert = panel.actions.querySelector('.sqe-revert');
        if (revert) {
            revert.hidden = !canRevert(panel);
            revert.disabled = panel.busy;
        }
    }

    /** True when there is something to revert: unsaved changes, or a file that differs from the session's original. */
    function canRevert(panel) {
        if (!panel.model || !panel.loaded) {
            return false;
        }

        const baseline = baselineHash(panel.target);
        return panel.model.hasUnsaved() || Boolean(baseline && baseline !== panel.loaded.hash);
    }

    /** Fetches the cues again (after a save or a conflict) and makes them the panel's. */
    function refetchCues(target) {
        delete state.cues[cacheKey(target)];
        return loadCues(target).then(function (loaded) {
            if (state.panel && state.panel.target === target) {
                state.panel.loaded = loaded;
            }

            refreshOverlay();
            return loaded;
        });
    }

    function setBusy(busy) {
        const panel = state.panel;
        panel.busy = busy;
        panel.actions.querySelectorAll('button, textarea').forEach(function (b) {
            b.disabled = busy;
        });
        if (!busy) {
            renderChanges();
        }
    }

    /**
     * Re-shows the lines the panel showed from the panel's current cues. Found by time, not number: a saved new line
     * renumbers every line after it.
     */
    function reshow(note) {
        const panel = state.panel;
        if (panel.shown.length === 0) {
            renderCues(L.cueForTime(panel.loaded.cues, currentMs()), note);
            return;
        }

        const at = panel.shown[0].start;
        const cues = panel.loaded.cues.filter(function (c) {
            return c.start <= at && at < c.end;
        });
        renderCues({ mode: 'active', cues: cues.length > 0 ? cues : panel.shown }, note);
    }

    /** Sends every staged change in one request (the Save button and Ctrl+S). */
    function saveChanges() {
        const panel = state.panel;
        if (!panel || !panel.model || panel.busy) {
            return;
        }

        // A text box or new-line form that was changed but not applied yet is saved too.
        if (!applyCurrent(true)) {
            return;
        }

        const model = panel.model;
        const target = panel.target;
        const prepared = model.prepareSave();
        if (!prepared) {
            showStatus('Nothing is staged.');
            return;
        }

        // The first save of the session keeps a copy of the file as it was, for Revert to original.
        prepared.request.keepRestorePoint = prepared.request.baseHash === baselineHash(target);
        setBusy(true);
        showStatus('Saving\u2026');
        apiPost('SubtitleQuickEdit/Items/' + encodeURIComponent(target.itemId) + '/Subtitles/' + target.streamIndex + '/Edits',
            prepared.request).then(function (response) {
            model.applySaveSuccess(prepared.rowIds, response);
            state.showSavedText = true;
            persist(target, model);
            return refetchCues(target).then(function (loaded) {
                model.setCues(loaded.cues, loaded.hash);
                if (state.panel === panel) {
                    setBusy(false);
                    reshow('Saved ' + prepared.rowIds.length + ' change' + (prepared.rowIds.length === 1 ? '' : 's') + '.');
                }
            });
        }, function (err) {
            return errorDetails(err).then(function (details) {
                const outcome = model.applySaveError(prepared.rowIds, details.status, details.body);
                if (outcome.action === 'reconcile') {
                    return refetchCues(target).then(function (loaded) {
                        const summary = model.reconcile(loaded.cues, loaded.hash);
                        persist(target, model);
                        if (state.panel === panel) {
                            setBusy(false);
                            reshow('The file changed on disk since it was loaded, so nothing was saved. ' +
                                (summary.failed.length > 0
                                    ? summary.failed.length + ' change' + (summary.failed.length === 1 ? '' : 's') +
                                      ' no longer match the file (marked failed); the rest are still staged.'
                                    : 'Your changes still match the file: press Save again.'));
                            showStatus(panel.status.textContent, true);
                        }
                    });
                }

                persist(target, model);
                if (state.panel === panel) {
                    setBusy(false);
                    reshow();
                    showStatus(outcome.action === 'marked' ? 'Nothing was saved: a change was refused (see the list).'
                        : 'Nothing was saved: ' + outcome.message, true);
                }

                return null;
            });
        }).catch(function (err) {
            if (state.panel === panel) {
                setBusy(false);
                return describeError(err).then(function (message) {
                    showStatus('Something went wrong after saving: ' + message, true);
                });
            }

            return null;
        });
    }

    /**
     * Sends every staged change to an admin for review, with the note (the Submit for review button and Ctrl+S, for
     * users who may not save directly). The file is not changed; the sent changes leave the list.
     */
    function submitChanges() {
        const panel = state.panel;
        if (!panel || !panel.model || panel.busy) {
            return;
        }

        if (!applyCurrent(true)) {
            return;
        }

        const model = panel.model;
        const target = panel.target;
        const prepared = model.prepareSave();
        if (!prepared) {
            showStatus('Nothing is staged.');
            return;
        }

        prepared.request.note = panel.note.value.trim() || null;
        setBusy(true);
        showStatus('Submitting\u2026');
        apiPost('SubtitleQuickEdit/Items/' + encodeURIComponent(target.itemId) + '/Subtitles/' + target.streamIndex + '/Submissions',
            prepared.request).then(function () {
            const sent = model.applySubmitSuccess(prepared.rowIds);
            persist(target, model);
            refreshOverlay();
            if (state.panel === panel) {
                panel.note.value = '';
                setBusy(false);
                reshow('Submitted ' + sent + ' change' + (sent === 1 ? '' : 's') + ' for review. An admin decides whether ' +
                    (sent === 1 ? 'it goes' : 'they go') + ' into the file.');
                loadMine();
            }
        }, function (err) {
            return errorDetails(err).then(function (details) {
                const outcome = model.applySaveError(prepared.rowIds, details.status, details.body);
                if (outcome.action === 'reconcile') {
                    return refetchCues(target).then(function (loaded) {
                        const summary = model.reconcile(loaded.cues, loaded.hash);
                        persist(target, model);
                        if (state.panel === panel) {
                            setBusy(false);
                            reshow('The file changed on disk since it was loaded, so nothing was submitted. ' +
                                (summary.failed.length > 0
                                    ? summary.failed.length + ' change' + (summary.failed.length === 1 ? '' : 's') +
                                      ' no longer match the file (marked failed); the rest are still staged.'
                                    : 'Your changes still match the file: press Submit again.'));
                            showStatus(panel.status.textContent, true);
                        }
                    });
                }

                persist(target, model);
                if (state.panel === panel) {
                    setBusy(false);
                    reshow();
                    showStatus(outcome.action === 'marked' ? 'Nothing was submitted: a change was refused (see the list).'
                        : 'Nothing was submitted: ' + outcome.message, true);
                }

                return null;
            });
        }).catch(function (err) {
            if (state.panel === panel) {
                setBusy(false);
                return describeError(err).then(function (message) {
                    showStatus('Something went wrong after submitting: ' + message, true);
                });
            }

            return null;
        });
    }

    /** Loads the user's own submissions for the panel's subtitle and lists them (shown only when there are some). */
    function loadMine() {
        const panel = state.panel;
        if (!panel || !panel.target) {
            return Promise.resolve();
        }

        return apiGet('SubtitleQuickEdit/Submissions/Mine').then(function (list) {
            if (state.panel === panel) {
                renderMine(L.submissionsFor(list, panel.target.itemId, panel.target.streamIndex));
            }
        }, function () {
            // The list is a courtesy; the editor works without it.
        });
    }

    function submissionChange(edit) {
        const item = el('li', 'sqe-mine-change');
        if (edit.type === 'add') {
            item.appendChild(el('span', 'sqe-change-where', 'New · ' + L.formatTime(edit.startMs) + ' → ' + L.formatTime(edit.endMs)));
            item.appendChild(el('div', 'sqe-change-new', edit.text));
        } else {
            item.appendChild(el('span', 'sqe-change-where', 'Line ' + edit.cueIndex + ' · ' + L.formatTime(edit.cueStart)));
            item.appendChild(el('div', 'sqe-change-old', edit.oldText));
            item.appendChild(el('div', 'sqe-change-new', edit.newText));
        }

        if (edit.outcome && edit.outcome !== 'Pending') {
            const words = { Applied: 'applied', Conflict: 'conflict', NotChosen: 'not chosen' };
            item.appendChild(el('span', 'sqe-badge sqe-outcome-' + edit.outcome, words[edit.outcome] || edit.outcome));
        }

        return item;
    }

    function renderMine(submissions) {
        const panel = state.panel;
        panel.mineList.textContent = '';
        panel.mine.hidden = submissions.length === 0;
        submissions.forEach(function (sub) {
            const item = el('li', 'sqe-mine-item sqe-mine-' + sub.state);
            item.setAttribute('data-submission-id', sub.id);
            const top = el('div', 'sqe-change-top');
            const when = sub.created ? new Date(sub.created).toLocaleString() : '';
            const count = (sub.edits || []).length;
            top.appendChild(el('span', 'sqe-change-where', when + ' · ' + count + ' change' + (count === 1 ? '' : 's')));
            top.appendChild(el('span', 'sqe-badge sqe-state-' + sub.state, L.submissionStateLabel(sub.state)));
            item.appendChild(top);
            if (sub.note) {
                item.appendChild(el('div', 'sqe-mine-note', 'Your note: ' + sub.note));
            }

            if (sub.reason) {
                item.appendChild(el('div', 'sqe-change-message', 'Admin: ' + sub.reason));
            }

            const changes = el('ul', 'sqe-mine-changes');
            (sub.edits || []).forEach(function (edit) {
                changes.appendChild(submissionChange(edit));
            });
            item.appendChild(changes);
            if (sub.state === 'Pending') {
                item.appendChild(button('sqe-withdraw', 'Withdraw', 'Take this submission back', function () {
                    withdraw(sub.id);
                }));
            }

            panel.mineList.appendChild(item);
        });
    }

    function withdraw(id) {
        const panel = state.panel;
        if (!panel || !window.confirm('Take this submission back? The admin will not see it any more.')) {
            return;
        }

        apiPost('SubtitleQuickEdit/Submissions/' + encodeURIComponent(id) + '/Withdraw').then(function () {
            if (state.panel === panel) {
                showStatus('Submission withdrawn.');
                loadMine();
            }
        }, function (err) {
            return describeError(err).then(function (message) {
                if (state.panel === panel) {
                    showStatus('Could not withdraw: ' + message, true);
                    loadMine();
                }
            });
        });
    }

    function manualTrack(video) {
        const tracks = video && video.textTracks;
        for (let i = 0; tracks && i < tracks.length; i++) {
            if (tracks[i].label === TEXT_TRACK_LABEL) {
                return tracks[i];
            }
        }

        return null;
    }

    /** True when the player's text track holds each of these cues' text as the file has it now (direction marks aside). */
    function trackMatches(track, cues, format) {
        if (!track || !track.cues) {
            return false;
        }

        const wanted = {};
        cues.forEach(function (c) {
            wanted[Math.round(c.start) + ':' + L.screenText(c.text, format)] = true;
        });
        const strip = /[\u200e\u200f\u202a-\u202e]/g;
        let found = 0;
        for (let i = 0; i < track.cues.length; i++) {
            const c = track.cues[i];
            if (wanted[Math.round(c.startTime * 1000) + ':' + String(c.text).replace(strip, '')]) {
                found++;
            }
        }

        return found >= cues.length;
    }

    /** Fallback: puts the file's cues straight into the player's text track. */
    function replaceTrackCues(track, cues, format) {
        Array.prototype.slice.call(track.cues || []).forEach(function (c) {
            track.removeCue(c);
        });
        cues.forEach(function (c) {
            track.addCue(new window.VTTCue(c.start / 1000, c.end / 1000, L.screenText(c.text, format)));
        });
    }

    function delay(ms) {
        return new Promise(function (resolve) {
            setTimeout(resolve, ms);
        });
    }

    /**
     * Makes the player show the file as saved. The server tells this page's player to switch the subtitle off and on
     * again (it then fetches the track again); if the player's track still differs after a few seconds, the cues are
     * put into the track directly. Playback position and pause state are kept.
     */
    function reloadSubtitles(doneNote) {
        const panel = state.panel;
        const session = state.session;
        if (!panel || !panel.target || panel.busy) {
            return;
        }

        const target = panel.target;
        setBusy(true);
        showStatus('Reloading subtitles\u2026');
        function command(index) {
            return apiPost('Sessions/' + encodeURIComponent(session.id) + '/Command',
                { Name: 'SetSubtitleStreamIndex', Arguments: { Index: String(index) } }, undefined, true);
        }

        refetchCues(target).then(function (loaded) {
            const viaServer = session && session.id
                ? command(-1).then(function () {
                    return delay(RELOAD_OFF_MS);
                }).then(function () {
                    return command(target.streamIndex);
                }).then(function () {
                    const until = now() + RELOAD_CHECK_MS;
                    const settled = now() + 1000; // the custom renderer's element is rebuilt only once the file is fetched
                    // Check the lines the panel shows (jellyfin-web may reformat others, for example styled text).
                    const numbers = panel.shown.map(function (c) {
                        return c.index;
                    });
                    const check = loaded.cues.filter(function (c) {
                        return numbers.indexOf(c.index) >= 0;
                    });
                    function wait() {
                        const renderer = subtitleRenderer();
                        if (renderer === 'native' && trackMatches(manualTrack(state.video), check.length > 0 ? check : loaded.cues, loaded.format)) {
                            return true;
                        }

                        if (renderer === 'none' && loaded.format === 'ass' && now() >= settled) {
                            // The player draws ASS itself (JASSUB, from the whole file): it has fetched the file again
                            // once the subtitle is back on, and there is no text track to compare.
                            return true;
                        }

                        if (renderer === 'custom' && now() >= settled) {
                            // jellyfin-web's own element is back, filled from the file it just fetched; it redraws on
                            // the next timeupdate, so seek to where the video already is.
                            state.showSavedText = false;
                            const video = state.video;
                            if (video) {
                                video.currentTime = video.currentTime;
                            }

                            return true;
                        }

                        return now() >= until ? false : delay(250).then(wait);
                    }

                    return wait();
                }, function () {
                    // A command failed; the subtitle may have been switched off already, so ask for it once more.
                    return command(target.streamIndex).then(function () {
                        return delay(RELOAD_OFF_MS);
                    }, function () {
                        return null;
                    }).then(function () {
                        return false;
                    });
                })
                : Promise.resolve(false);
            return viaServer.then(function (shown) {
                if (!shown) {
                    const track = manualTrack(state.video);
                    if (!track || track.mode === 'disabled') {
                        throw new Error('the player did not switch the subtitle back on. Choose it again in the subtitle menu.');
                    }

                    replaceTrackCues(track, loaded.cues, loaded.format);
                }

                refreshOverlay(); // staged changes that are not saved yet stay on screen
                if (state.panel === panel) {
                    setBusy(false);
                    reshow(doneNote || 'Subtitles reloaded.');
                }
            });
        }).catch(function (err) {
            if (state.panel === panel) {
                setBusy(false);
                return describeError(err).then(function (message) {
                    showStatus('Could not reload the subtitles: ' + message, true);
                });
            }

            return null;
        });
    }

    /**
     * Puts the file back as it was the first time this tab loaded it (the server keeps that copy at the session's first
     * save), drops every change in the list, and reloads the player's subtitles. With nothing saved yet, only the
     * unsaved changes are dropped.
     */
    function revertToOriginal() {
        const panel = state.panel;
        if (!panel || !panel.model || panel.busy) {
            return;
        }

        const target = panel.target;
        const model = panel.model;
        const baseline = baselineHash(target);
        if (!baseline) {
            showStatus('The original of this subtitle is not known in this tab, so it cannot be reverted.', true);
            return;
        }

        if (!window.confirm('Revert this subtitle to how it was before your changes this session? ' +
            'Unsaved changes are dropped too.')) {
            return;
        }

        stopPreview();
        setBusy(true);
        showStatus('Reverting\u2026');
        function dropChanges(loaded) {
            const dropped = model.clear();
            model.setCues(loaded.cues, loaded.hash);
            persist(target, model);
            return dropped;
        }

        refetchCues(target).then(function (current) {
            if (current.hash === baseline) {
                const dropped = dropChanges(current);
                if (state.panel === panel) {
                    setBusy(false);
                    reshow(dropped > 0 ? 'Dropped the unsaved changes. The file was not changed.' : 'The file is already the original.');
                }

                return null;
            }

            return apiPost('SubtitleQuickEdit/Items/' + encodeURIComponent(target.itemId) + '/Subtitles/' + target.streamIndex + '/Restore',
                { baseHash: current.hash, targetHash: baseline }).then(function () {
                return refetchCues(target);
            }).then(function (loaded) {
                dropChanges(loaded);
                state.showSavedText = true;
                if (state.panel === panel) {
                    setBusy(false);
                    reloadSubtitles('Reverted to the original and reloaded the subtitles.');
                }

                return null;
            }, function (err) {
                return errorDetails(err).then(function (details) {
                    if (state.panel !== panel) {
                        return null;
                    }

                    setBusy(false);
                    reshow();
                    if (details.status === 404 && details.body.code === 'RestorePointNotFound') {
                        showStatus('No copy of the original was kept for this subtitle, so nothing was changed.', true);
                    } else if (details.status === 409) {
                        return refetchCues(target).then(function () {
                            showStatus('The file changed on disk just now, so nothing was reverted. Try again.', true);
                        });
                    } else {
                        showStatus('Nothing was reverted: ' + ((details.body && details.body.message) || 'the request failed.'), true);
                    }

                    return null;
                });
            });
        }).catch(function (err) {
            if (state.panel === panel) {
                setBusy(false);
                return describeError(err).then(function (message) {
                    showStatus('Could not revert: ' + message, true);
                });
            }

            return null;
        });
    }

    function openPanel() {
        if (state.panel) {
            return;
        }

        const video = state.video;
        state.resumeOnClose = Boolean(video && !video.paused);
        if (video) {
            video.pause();
        }

        state.panel = buildPanel();
        document.body.appendChild(state.panel.root);
        document.body.classList.add('sqe-panel-open');
        showStatus('Loading…');
        const seq = state.playbackSeq;
        if (state.isAdmin) {
            loadMe().then(showPendingReviews);
        }

        // The authoritative check: ask Sessions again before trusting the button's state.
        loadSession(seq).then(function () {
            if (!state.panel || seq !== state.playbackSeq) {
                return null;
            }

            if (!state.target) {
                showStatus('The subtitle on screen cannot be edited here (only external .srt, .vtt, .ass and .ssa files can).', true);
                return null;
            }

            const target = state.target;
            return loadCues(target).then(function (loaded) {
                if (!state.panel || seq !== state.playbackSeq) {
                    return;
                }

                const t = L.secondsToMs(state.video ? state.video.currentTime : 0);
                state.panel.target = target;
                state.panel.loaded = loaded;
                state.panel.tags.hidden = !L.hasStyleTags(loaded.format);
                const made = modelFor(target, loaded);
                state.panel.model = made.model;
                renderChanges();
                const shown = L.cueForTime(loaded.cues, t === null ? 0 : t);
                renderCues(shown, made.restored === null ? undefined : made.restored);
                loadMine();
            });
        }).catch(function (err) {
            return describeError(err).then(function (message) {
                showStatus('Could not load the subtitle: ' + message, true);
            });
        });
    }

    function closePanel() {
        if (!state.panel) {
            return;
        }

        if (state.panel.model) {
            state.panel.model.dropSaved(); // saved rows stay in the list only while the panel is open
        }

        stopPreview();
        state.panel.root.remove();
        state.panel = null;
        document.body.classList.remove('sqe-panel-open');
        const video = state.video;
        if (state.resumeOnClose && video && video.paused) {
            const played = video.play();
            if (played && typeof played.catch === 'function') {
                played.catch(function () {
                    // The browser refused to resume (autoplay policy); the user can press play.
                });
            }
        }

        state.resumeOnClose = false;
    }

    // ---- keys: while the panel has focus, no key reaches the player -------------------------------------------------
    // Registered at load, before jellyfin-web's bundles run (they are deferred), so this listener runs first.

    function onKey(e) {
        if (!state.panel || !(e.target instanceof Node) || !state.panel.root.contains(e.target)) {
            return;
        }

        if (e.type === 'keydown') {
            if (e.key === 'Escape') {
                e.preventDefault();
                closePanel();
            } else if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
                e.preventDefault();
                applyCurrent();
            } else if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
                e.preventDefault(); // never the browser's save dialog
                primaryAction();
            }
        }

        // Stops jellyfin-web's own listeners (space, f, arrows...). Typing still works: the default action is kept.
        e.stopImmediatePropagation();
    }

    ['keydown', 'keyup', 'keypress'].forEach(function (type) {
        window.addEventListener(type, onKey, true);
    });

    // ---- watching the page -----------------------------------------------------------------------------------------

    let refreshTimer = null;

    function scheduleRefresh() {
        if (refreshTimer === null) {
            refreshTimer = setTimeout(function () {
                refreshTimer = null;
                refresh();
            }, REFRESH_DELAY_MS);
        }
    }

    function refresh() {
        checkUser();
        const video = document.querySelector(SEL.video);
        if (!video) {
            if (state.video) {
                // Left the player.
                state.video = null;
                state.playbackSeq++;
                state.sessionTried = false;
                state.session = null;
                state.menu = null;
                state.target = null;
                closePanel();
            }

            removeButtons(null);
            return;
        }

        if (video !== state.video) {
            // A new playback (jellyfin-web creates a new <video> each time).
            closePanel();
            state.video = video;
            state.showSavedText = false;
            video.addEventListener('timeupdate', onVideoTime);
            video.addEventListener('seeked', onVideoTime);
            state.playbackSeq++;
            state.playbackStartedAt = now();
            state.session = null;
            state.menu = null;
            state.target = null;
            state.tracks = {};
            state.cues = {};
            state.sessionTried = false;
        }

        if (state.mode && !state.sessionTried) {
            // At the start of a playback, or when the rights check finished after playback started.
            state.sessionTried = true;
            loadSession(state.playbackSeq, 0);
            return;
        }

        updateButton();
    }

    function start() {
        L.waitForApiClient({ timeoutMs: 24 * 3600 * 1000, intervalMs: 250 }).then(function () {
            checkUser();
            new MutationObserver(scheduleRefresh).observe(document.documentElement, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['class'],
            });
            scheduleRefresh();
        }, function () {
            console.warn(LOG, 'gave up waiting for the Jellyfin web client; the editor is off.');
        });
    }

    // A small read-only view of the state for troubleshooting and browser tests. Holds no tokens or URLs.
    window.SubtitleQuickEdit = {
        debugState: function () {
            return {
                userId: state.userId,
                isAdmin: state.isAdmin,
                mode: state.mode,
                hasVideo: Boolean(state.video),
                session: state.session ? Object.assign({}, state.session) : null,
                menu: state.menu ? Object.assign({}, state.menu) : null,
                target: state.target ? { itemId: state.target.itemId, streamIndex: state.target.streamIndex } : null,
                panelOpen: Boolean(state.panel),
                renderer: state.video ? subtitleRenderer() : null,
                buttons: document.querySelectorAll('.sqe-edit-button').length,
            };
        },
    };

    start();
}());
