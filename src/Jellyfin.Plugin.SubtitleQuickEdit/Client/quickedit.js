/*
 * Subtitle Quick Edit page code: the edit button in the player and the side panel.
 * Served after logic.js in the same file (see ClientAssetsController), so window.SqeLogic is already defined.
 * What jellyfin-web 12.2 looks like (selectors, load order, Sessions lag) is recorded in docs/client-notes.md.
 * Stage 3c: the button, the session state and the panel shell (it shows the cue on screen; editing comes in 3d).
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
    };
    const BODY_IDLE_CLASS = 'mouseIdle'; // on <body> while the OSD is hidden
    const TEXT_TRACK_LABEL = 'manualTrack'; // the one text track jellyfin-web fills with the chosen subtitle's cues
    const FLOATING_AFTER_MS = 3000; // no OSD button row this long after playback started: use the floating button
    const MENU_WINDOW_MS = 60000; // a menu choice counts as a subtitle choice this long after the subtitle button was clicked
    const SESSION_RETRY_MS = [500, 1000, 2000, 4000]; // Sessions may not show the item at the very start of playback
    const REFRESH_DELAY_MS = 100; // coalesces bursts of DOM changes into one refresh
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
        video: null, // the current <video>; a new one is created for every playback
        playbackSeq: 0, // bumped on every new playback, so late answers for an old one are ignored
        playbackStartedAt: 0,
        session: null, // { itemId, mediaSourceId, subtitleIndex, runtimeMs, requestedAt }
        sessionTried: false, // whether this playback's Sessions lookup has been started
        menu: null, // { index, at }: the last subtitle choice made in the subtitle menu
        lastOsdButton: null, // { kind: 'subtitles' | 'other', at }
        tracks: {}, // itemId -> promise of the plugin's subtitle list
        cues: {}, // 'itemId:streamIndex' -> { hash, encoding, cues }
        target: null, // { itemId, streamIndex, track } when the active track is editable, else null
        panel: null, // the open panel's elements, or null
        resumeOnClose: false,
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
        state.tracks = {};
        state.cues = {};
        if (!userId) {
            scheduleRefresh();
            return;
        }

        client.getCurrentUser().then(function (user) {
            if (state.userId === userId) {
                // Stage 3 is for administrators only; the non-admin panel (Submit for review) is stage 4.
                state.isAdmin = Boolean(user && user.Policy && user.Policy.IsAdministrator);
                scheduleRefresh();
            }
        }, function () {
            // Not known: stay hidden.
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
        if (!state.isAdmin || !s || index === null || !sameId(s.mediaSourceId, s.itemId)) {
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
        button.title = 'Edit subtitle line';
        button.setAttribute('aria-label', 'Edit subtitle line');
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
                const loaded = { hash: body.hash, encoding: body.encoding, cues: body.cues || [] };
                state.cues[key] = loaded;
                return loaded;
            });
    }

    function buildPanel() {
        const root = el('aside', 'sqe-panel');
        root.setAttribute('role', 'dialog');
        root.setAttribute('aria-label', 'Subtitle Quick Edit');
        const header = el('header', 'sqe-header');
        const title = el('h2', 'sqe-title', 'Edit subtitle line');
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
        const cues = el('div', 'sqe-cues');
        root.appendChild(header);
        root.appendChild(status);
        root.appendChild(cues);
        return { root: root, status: status, cues: cues };
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

    function renderCues(shown) {
        const box = state.panel.cues;
        box.textContent = '';
        if (shown.cues.length === 0) {
            showStatus('This subtitle has no lines.');
            return;
        }

        showStatus(MODE_NOTES[shown.mode] || '');
        shown.cues.forEach(function (cue) {
            const item = el('section', 'sqe-cue');
            item.setAttribute('data-cue-index', String(cue.index));
            const heading = el('div', 'sqe-cue-heading');
            heading.appendChild(el('span', 'sqe-cue-number', 'Line ' + cue.index));
            heading.appendChild(el('span', 'sqe-cue-time', L.formatTime(cue.start) + ' → ' + L.formatTime(cue.end)));
            const text = el('textarea', 'sqe-cue-text');
            text.value = cue.text;
            text.rows = Math.max(2, cue.text.split('\n').length);
            text.setAttribute('aria-label', 'Text of line ' + cue.index);
            item.appendChild(heading);
            item.appendChild(text);
            box.appendChild(item);
        });
        const first = box.querySelector('textarea');
        if (first) {
            first.focus();
        }
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

        // The authoritative check: ask Sessions again before trusting the button's state.
        loadSession(seq).then(function () {
            if (!state.panel || seq !== state.playbackSeq) {
                return null;
            }

            if (!state.target) {
                showStatus('The subtitle on screen cannot be edited here (only external .srt files can).', true);
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
                renderCues(L.cueForTime(loaded.cues, t === null ? 0 : t));
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
            } else if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
                e.preventDefault(); // Save comes in stage 3e; never the browser's save dialog
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
            state.playbackSeq++;
            state.playbackStartedAt = now();
            state.session = null;
            state.menu = null;
            state.target = null;
            state.tracks = {};
            state.cues = {};
            state.sessionTried = false;
        }

        if (state.isAdmin && !state.sessionTried) {
            // At the start of a playback, or when the admin check finished after playback started.
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
                hasVideo: Boolean(state.video),
                session: state.session ? Object.assign({}, state.session) : null,
                menu: state.menu ? Object.assign({}, state.menu) : null,
                target: state.target ? { itemId: state.target.itemId, streamIndex: state.target.streamIndex } : null,
                panelOpen: Boolean(state.panel),
                buttons: document.querySelectorAll('.sqe-edit-button').length,
            };
        },
    };

    start();
}());
