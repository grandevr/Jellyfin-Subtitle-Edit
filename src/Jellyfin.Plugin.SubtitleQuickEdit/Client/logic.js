/*
 * Subtitle Editor client logic: pure functions and a staging model, no DOM and no network.
 * Works as a CommonJS module (Node unit tests) and as a plain script (window.SqeLogic).
 *
 * Conventions: times are integer milliseconds. A cue is { index, start, end, text } where index is the cue's 1-based
 * position in the file, exactly as the server's Cues call returns it. Line breaks inside text are "\n".
 */
(function (root, factory) {
    'use strict';
    if (typeof module === 'object' && module.exports) {
        module.exports = factory();
    } else {
        root.SqeLogic = factory();
    }
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    // ---- constants (mirror the server's rules) ----------------------------------------------------------------

    const MAX_TIME_MS = 359999999; // 99:59:59,999, the most an .srt time can hold
    const MIN_CUE_MS = 100; // an added line must be at least this long
    const DEFAULT_LENGTH_MS = 2000; // default length of a new line
    const MIN_DEFAULT_LENGTH_MS = 500; // the default end is never closer than this to the start
    const SESSIONS_LAG_MS = 10000; // how long GET /Sessions can take to show a subtitle change (6 s measured, with margin)
    const DRAFT_VERSION = 1;

    // ---- time -------------------------------------------------------------------------------------------------

    const TIME_PATTERN = /^(\d{1,2}):([0-5]\d):([0-5]\d)[,.](\d{3})$/;

    /** Parses hh:mm:ss,mmm (a dot is accepted for the comma) into milliseconds; null for anything else. */
    function parseTime(text) {
        if (typeof text !== 'string') {
            return null;
        }

        const m = TIME_PATTERN.exec(text.trim());
        if (!m) {
            return null;
        }

        return ((Number(m[1]) * 60 + Number(m[2])) * 60 + Number(m[3])) * 1000 + Number(m[4]);
    }

    function pad(value, width) {
        let s = String(value);
        while (s.length < width) {
            s = '0' + s;
        }

        return s;
    }

    /** Formats milliseconds as hh:mm:ss,mmm; null when the value is not a number from 0 to 99:59:59,999. */
    function formatTime(ms) {
        if (typeof ms !== 'number' || !isFinite(ms)) {
            return null;
        }

        const total = Math.round(ms);
        if (total < 0 || total > MAX_TIME_MS) {
            return null;
        }

        const hours = Math.floor(total / 3600000);
        const minutes = Math.floor(total / 60000) % 60;
        const seconds = Math.floor(total / 1000) % 60;
        return pad(hours, 2) + ':' + pad(minutes, 2) + ':' + pad(seconds, 2) + ',' + pad(total % 1000, 3);
    }

    /** Converts a video element's currentTime (seconds) to whole milliseconds; null for bad input. */
    function secondsToMs(seconds) {
        if (typeof seconds !== 'number' || !isFinite(seconds) || seconds < 0) {
            return null;
        }

        return Math.round(seconds * 1000);
    }

    /** Moves a time by delta milliseconds (the -100 / +100 buttons), never below 0 or above the limit. */
    function nudgeTime(ms, delta, limitMs) {
        const limit = typeof limitMs === 'number' && isFinite(limitMs) ? Math.min(limitMs, MAX_TIME_MS) : MAX_TIME_MS;
        return Math.min(limit, Math.max(0, Math.round(ms + delta)));
    }

    // ---- cue lookup -------------------------------------------------------------------------------------------

    function compareCues(a, b) {
        return (a.start - b.start) || (a.index - b.index);
    }

    /** Cues in time order (start, then file position). The input is not changed. */
    function sortedCues(cues) {
        return (cues || []).slice().sort(compareCues);
    }

    /** Every cue on screen at time t: start is included, end is not. Time order. */
    function cuesAt(cues, t) {
        return sortedCues((cues || []).filter(function (c) {
            return c.start <= t && t < c.end;
        }));
    }

    /** The cue that went off screen most recently before t (largest end; ties go to the later start, then position). */
    function nearestPrevious(cues, t) {
        let best = null;
        (cues || []).forEach(function (c) {
            if (c.start > t || c.end > t) {
                return;
            }

            if (best === null || c.end > best.end ||
                (c.end === best.end && (c.start > best.start || (c.start === best.start && c.index > best.index)))) {
                best = c;
            }
        });
        return best;
    }

    /** The first cue that starts after t, or null. */
    function nextAfter(cues, t) {
        const later = sortedCues((cues || []).filter(function (c) {
            return c.start > t;
        }));
        return later.length > 0 ? later[0] : null;
    }

    /**
     * What the panel shows at time t: the cues on screen (several when they overlap), else the nearest previous cue,
     * else (before the first cue) the first upcoming cue. mode is 'active', 'previous', 'next' or 'empty'.
     */
    function cueForTime(cues, t) {
        const active = cuesAt(cues, t);
        if (active.length > 0) {
            return { mode: 'active', cues: active };
        }

        const previous = nearestPrevious(cues, t);
        if (previous) {
            return { mode: 'previous', cues: [previous] };
        }

        const next = nextAfter(cues, t);
        if (next) {
            return { mode: 'next', cues: [next] };
        }

        return { mode: 'empty', cues: [] };
    }

    /**
     * The neighbour a Previous (direction < 0) or Next (direction > 0) click moves to, in time order. `shown` is what the
     * panel shows now (one cue, or several overlapping ones: Previous goes before the first of them, Next after the last).
     * Returns null at either end of the file.
     */
    function stepCue(cues, shown, direction) {
        const sorted = sortedCues(cues);
        if (!shown || shown.length === 0 || sorted.length === 0) {
            return null;
        }

        const positions = [];
        shown.forEach(function (s) {
            for (let i = 0; i < sorted.length; i++) {
                if (sorted[i].index === s.index) {
                    positions.push(i);
                    break;
                }
            }
        });
        if (positions.length === 0) {
            return null;
        }

        const target = direction < 0 ? Math.min.apply(null, positions) - 1 : Math.max.apply(null, positions) + 1;
        return target >= 0 && target < sorted.length ? sorted[target] : null;
    }

    // ---- adding a line: defaults and overlaps ------------------------------------------------------------------

    /**
     * The prefilled times of a new line: start = now, end = start + 2 s, or the next cue's start if that comes sooner,
     * but never less than 0.5 s after the start, and not past the runtime when that is known (near the end the start
     * moves back so the line still lasts 0.5 s).
     */
    function defaultNewLine(cues, nowMs, runtimeMs) {
        const startMs = Math.max(0, Math.round(nowMs));
        const next = nextAfter(cues, startMs);
        let endMs = startMs + DEFAULT_LENGTH_MS;
        if (next && next.start < endMs) {
            endMs = next.start;
        }

        if (endMs < startMs + MIN_DEFAULT_LENGTH_MS) {
            endMs = startMs + MIN_DEFAULT_LENGTH_MS;
        }

        if (typeof runtimeMs === 'number' && isFinite(runtimeMs) && endMs > runtimeMs) {
            // Near the end of the video, keep the line valid by moving the start back rather than shrinking the line.
            endMs = Math.max(0, Math.round(runtimeMs));
            return { startMs: Math.max(0, Math.min(startMs, endMs - MIN_DEFAULT_LENGTH_MS)), endMs: endMs };
        }

        return { startMs: startMs, endMs: endMs };
    }

    /** Cues whose time span overlaps [startMs, endMs). Touching ends do not count. */
    function findOverlaps(cues, startMs, endMs) {
        return sortedCues((cues || []).filter(function (c) {
            return c.start < endMs && startMs < c.end;
        }));
    }

    /**
     * The warning text for a list of overlapped cues and a count of overlapped staged new lines, or null when there are
     * none. It warns; it never blocks.
     */
    function overlapWarning(overlaps, stagedCount) {
        const parts = [];
        if (overlaps && overlaps.length === 1) {
            const c = overlaps[0];
            parts.push('Overlaps cue ' + c.index + ' (' + formatTime(c.start) + ' to ' + formatTime(c.end) + ').');
        } else if (overlaps && overlaps.length > 1) {
            parts.push('Overlaps ' + overlaps.length + ' cues: ' + overlaps.map(function (c) {
                return c.index;
            }).join(', ') + '.');
        }

        if (stagedCount > 0) {
            parts.push((parts.length > 0 ? 'Also overlaps ' : 'Overlaps ') + stagedCount + ' staged new line' +
                (stagedCount === 1 ? '' : 's') + '.');
        }

        return parts.length > 0 ? parts.join(' ') : null;
    }

    // ---- text and new-line checks (the same rules the server applies) ----------------------------------------------

    // The server's time-line pattern (SrtPatterns.TimeLine): a line that starts with a cue time line.
    const TIME_LINE_PATTERN = /^\s*\d{1,3}:\d{1,2}:\d{1,2}[,.]\d{1,3}\s*-->\s*\d{1,3}:\d{1,2}:\d{1,2}[,.]\d{1,3}/;

    /** CRLF and CR become LF, like the server does before it checks text. */
    function normalizeText(value) {
        return String(value).replace(/\r\n?/g, '\n');
    }

    /**
     * Checks cue text for a format ('srt' when not given; 'vtt'; 'ass'). Returns { ok: true, text } (text with LF breaks)
     * or { ok: false, error: { code, message } }. The rules are the server's: every format needs at least one character
     * and no control characters; .srt and .vtt text may not hold a blank line, .srt text no line that looks like a time
     * line and .vtt text no "-->". ASS text may hold blank lines (\N\N), since its line breaks are not real ones.
     */
    function validateCueText(value, format) {
        if (typeof value !== 'string') {
            return { ok: false, error: { code: 'TextNotText', message: 'The text must be a string.' } };
        }

        const text = normalizeText(value);
        if (text.length === 0) {
            return { ok: false, error: { code: 'TextEmpty', message: 'A line must keep at least one character.' } };
        }

        const lines = text.split('\n');
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (format !== 'ass' && line.trim().length === 0) {
                return { ok: false, error: { code: 'TextBlankLine', message: 'The text must not contain empty lines: a blank line would end the cue.' } };
            }

            if (format === 'vtt' && line.indexOf('-->') >= 0) {
                return { ok: false, error: { code: 'TextArrow', message: 'A line of text must not contain "-->": in a WebVTT file it would start a new cue.' } };
            }

            if (format !== 'vtt' && format !== 'ass' && TIME_LINE_PATTERN.test(line)) {
                return { ok: false, error: { code: 'TextLooksLikeTime', message: 'A line of text must not look like a cue time line (hh:mm:ss,mmm --> hh:mm:ss,mmm).' } };
            }

            for (let k = 0; k < line.length; k++) {
                const code = line.charCodeAt(k);
                if (code < 32 && code !== 9) {
                    return { ok: false, error: { code: 'TextControlChar', message: 'The text contains a control character.' } };
                }
            }
        }

        return { ok: true, text: text };
    }

    // ---- style tags (ASS override blocks, WebVTT tags) ------------------------------------------------------------

    const ASS_LEADING_TAGS = /^(?:\{[^}]*\})+/;
    const VTT_LEADING_TAGS = /^(?:<[a-z][^>\n]*>)+/i;
    const VTT_TRAILING_TAGS = /(?:<\/[a-z]+>)+$/i;

    /**
     * Splits cue text into the style tags the editor hides when "Show style tags" is off and the text it shows: for ASS
     * the override blocks at the very start ({\an8}); for WebVTT the tags opening the line (<v Anna>, <i>) and those
     * closing it. Tags inside the line stay in body. prefix + body + suffix is always the text it was given.
     */
    function splitStyleTags(text, format) {
        const value = String(text);
        let prefix = '';
        let suffix = '';
        if (format === 'ass') {
            const m = ASS_LEADING_TAGS.exec(value);
            prefix = m ? m[0] : '';
        } else if (format === 'vtt') {
            const m = VTT_LEADING_TAGS.exec(value);
            prefix = m ? m[0] : '';
            const rest = value.slice(prefix.length);
            const t = prefix ? VTT_TRAILING_TAGS.exec(rest) : null;
            suffix = t ? t[0] : '';
        }

        return { prefix: prefix, body: value.slice(prefix.length, value.length - suffix.length), suffix: suffix };
    }

    /** Whether a format has style tags the editor can hide (and so shows the "Show style tags" toggle). */
    function hasStyleTags(format) {
        return format === 'ass' || format === 'vtt';
    }

    /**
     * Cue text as the player shows it from Jellyfin's own conversion: ASS override blocks removed and \h as a space.
     * Used to compare with, or write into, the player's text track. Other formats are unchanged.
     */
    function screenText(text, format) {
        const value = String(text);
        return format === 'ass' ? value.replace(/\{[^}]*\}/g, '').replace(/\\h/g, ' ') : value;
    }

    /**
     * Checks an added line the way the server will: start at least 0, end after start, at least 100 ms long, end within
     * 99:59:59,999 and within the runtime when it is known, and valid text for options.format. Returns { ok, errors:
     * [{ field, message }] }.
     */
    function validateNewLine(line, options) {
        const errors = [];
        const runtimeMs = options && typeof options.runtimeMs === 'number' && isFinite(options.runtimeMs) ? options.runtimeMs : null;
        const startMs = line ? line.startMs : null;
        const endMs = line ? line.endMs : null;
        const startOk = typeof startMs === 'number' && isFinite(startMs);
        const endOk = typeof endMs === 'number' && isFinite(endMs);
        if (!startOk) {
            errors.push({ field: 'start', message: 'Enter a start time as hh:mm:ss,mmm.' });
        } else if (startMs < 0) {
            errors.push({ field: 'start', message: 'A new line cannot start before 00:00:00,000.' });
        }

        if (!endOk) {
            errors.push({ field: 'end', message: 'Enter an end time as hh:mm:ss,mmm.' });
        } else if (endMs > MAX_TIME_MS) {
            errors.push({ field: 'end', message: 'A new line cannot end after 99:59:59,999.' });
        } else if (runtimeMs !== null && endMs > runtimeMs) {
            errors.push({ field: 'end', message: 'A new line must not end after the end of the video.' });
        }

        if (startOk && endOk) {
            if (endMs <= startMs) {
                errors.push({ field: 'end', message: 'A new line must end after it starts.' });
            } else if (endMs - startMs < MIN_CUE_MS) {
                errors.push({ field: 'end', message: 'A new line must be at least 100 ms long.' });
            }
        }

        const text = validateCueText(line ? line.text : null, options && options.format);
        if (!text.ok) {
            errors.push({ field: 'text', message: text.error.message });
        }

        return { ok: errors.length === 0, errors: errors, text: text.ok ? text.text : null };
    }

    // ---- renumbering after added lines ------------------------------------------------------------------------

    /**
     * Where a cue that was at oldPosition is after the server inserted new cues at addedPositions (their new 1-based
     * numbers, as the Edits response's addedCues mapping gives them).
     */
    function shiftPosition(oldPosition, addedPositions) {
        const added = (addedPositions || []).slice().sort(function (a, b) {
            return a - b;
        });
        let position = oldPosition;
        for (let i = 0; i < added.length; i++) {
            if (added[i] <= position) {
                position++;
            }
        }

        return position;
    }

    // ---- the staging model ------------------------------------------------------------------------------------

    function sameText(a, b) {
        return normalizeText(a) === normalizeText(b);
    }

    function findCueByIndex(cues, index) {
        for (let i = 0; i < cues.length; i++) {
            if (cues[i].index === index) {
                return cues[i];
            }
        }

        return null;
    }

    /**
     * Where a staged edit's cue is in a fresh set of cues: at the same position if its text there is still the row's
     * oldText; otherwise at the one cue with the same times and the same oldText (the file shifted); otherwise nowhere.
     */
    function locateCue(row, freshCues) {
        const atSame = findCueByIndex(freshCues, row.cueIndex);
        if (atSame && sameText(atSame.text, row.oldText)) {
            return atSame;
        }

        const candidates = freshCues.filter(function (c) {
            return c.start === row.start && c.end === row.end && sameText(c.text, row.oldText);
        });
        return candidates.length === 1 ? candidates[0] : null;
    }

    function copyRow(row) {
        return Object.assign({}, row);
    }

    /**
     * The changes of one editing session: one row per edited cue (editing a cue again updates its row), plus new lines.
     * Row status is 'staged', 'saved' or 'failed'. Edit rows: { rowId, type: 'edit', cueIndex, start, end, oldText,
     * newText, status, message }. New-line rows: { rowId, type: 'add', clientId, position, startMs, endMs, text, status,
     * message }. cueIndex and position are 1-based cue numbers as of the last known state of the file.
     */
    class StagingModel {
        constructor(options) {
            const o = options || {};
            this.rows = [];
            this.cues = [];
            this.baseHash = null;
            this.runtimeMs = typeof o.runtimeMs === 'number' ? o.runtimeMs : null;
            this.format = typeof o.format === 'string' ? o.format : 'srt';
            this.editSeq = 0;
            this.addSeq = 0;
            this.newClientId = typeof o.newClientId === 'function' ? o.newClientId : null;
        }

        /** Sets the cues and hash the next save is based on (call after every fetch of the Cues). */
        setCues(cues, hash, runtimeMs) {
            this.cues = (cues || []).slice();
            this.baseHash = hash === undefined ? this.baseHash : hash;
            if (typeof runtimeMs === 'number') {
                this.runtimeMs = runtimeMs;
            }
        }

        /** A copy of the rows in insertion order. */
        snapshot() {
            return this.rows.map(copyRow);
        }

        /** A copy of the rows in time order, for the "Changes this session" list. */
        sortedRows() {
            return this.snapshot().sort(function (a, b) {
                const sa = a.type === 'add' ? a.startMs : a.start;
                const sb = b.type === 'add' ? b.startMs : b.start;
                return (sa - sb) || (a.type === b.type ? 0 : (a.type === 'edit' ? -1 : 1));
            });
        }

        /** True while anything is staged or failed, so leaving the page needs a confirmation. */
        hasUnsaved() {
            return this.rows.some(function (r) {
                return r.status !== 'saved';
            });
        }

        findRow(rowId) {
            for (let i = 0; i < this.rows.length; i++) {
                if (this.rows[i].rowId === rowId) {
                    return this.rows[i];
                }
            }

            return null;
        }

        /**
         * Stages new text for a cue. Editing a cue that already has an unsaved row updates that row (oldText stays the
         * file's text); setting the text back to the original removes the row. Returns { ok, row } or { ok: false, error }.
         */
        stageEdit(cue, newText) {
            const checked = validateCueText(newText, this.format);
            if (!checked.ok) {
                return { ok: false, error: checked.error };
            }

            const existing = this.rows.find(function (r) {
                return r.type === 'edit' && r.cueIndex === cue.index && r.status !== 'saved';
            });
            const original = existing ? existing.oldText : normalizeText(cue.text);
            if (checked.text === original) {
                if (existing) {
                    this.rows.splice(this.rows.indexOf(existing), 1);
                }

                return { ok: true, row: null, removed: Boolean(existing) };
            }

            if (existing) {
                existing.newText = checked.text;
                existing.start = cue.start;
                existing.end = cue.end;
                existing.status = 'staged';
                existing.message = null;
                return { ok: true, row: copyRow(existing) };
            }

            this.editSeq++;
            const row = {
                rowId: 'e' + this.editSeq,
                type: 'edit',
                cueIndex: cue.index,
                start: cue.start,
                end: cue.end,
                oldText: original,
                newText: checked.text,
                status: 'staged',
                message: null,
            };
            this.rows.push(row);
            return { ok: true, row: copyRow(row) };
        }

        makeClientId() {
            this.addSeq++;
            return this.newClientId ? this.newClientId(this.addSeq) : 'n' + this.addSeq;
        }

        /** Stages a new line { startMs, endMs, text }. Returns { ok, row } or { ok: false, errors }. */
        stageAdd(line) {
            const checked = validateNewLine(line, { runtimeMs: this.runtimeMs, format: this.format });
            if (!checked.ok) {
                return { ok: false, errors: checked.errors };
            }

            const clientId = this.makeClientId();
            const row = {
                rowId: clientId,
                type: 'add',
                clientId: clientId,
                position: null,
                startMs: Math.round(line.startMs),
                endMs: Math.round(line.endMs),
                text: checked.text,
                status: 'staged',
                message: null,
            };
            this.rows.push(row);
            return { ok: true, row: copyRow(row) };
        }

        /** Changes the text or timing of a staged (or failed) new line. Returns { ok, row } or { ok: false, errors }. */
        updateAdd(clientId, line) {
            const row = this.findRow(clientId);
            if (!row || row.type !== 'add' || row.status === 'saved') {
                return { ok: false, errors: [{ field: 'row', message: 'That new line is not staged any more.' }] };
            }

            const checked = validateNewLine(line, { runtimeMs: this.runtimeMs, format: this.format });
            if (!checked.ok) {
                return { ok: false, errors: checked.errors };
            }

            row.startMs = Math.round(line.startMs);
            row.endMs = Math.round(line.endMs);
            row.text = checked.text;
            row.status = 'staged';
            row.message = null;
            return { ok: true, row: copyRow(row) };
        }

        /** Drops a staged or failed row (the undo button). Saved rows cannot be undone. */
        undo(rowId) {
            const row = this.findRow(rowId);
            if (!row || row.status === 'saved') {
                return false;
            }

            this.rows.splice(this.rows.indexOf(row), 1);
            return true;
        }

        /**
         * Stages the reverse of a saved edit: the line's text from before that save, to be written by the next Save.
         * Refused when the line has changed in the file since. Returns stageEdit's answer, or { ok: false, error }.
         */
        undoSaved(rowId) {
            const row = this.findRow(rowId);
            if (!row || row.type !== 'edit' || row.status !== 'saved') {
                return { ok: false, error: 'Only a saved change to a line can be undone this way.' };
            }

            const cue = this.cues.find(function (c) {
                return c.index === row.cueIndex;
            });
            if (!cue || normalizeText(cue.text) !== row.newText) {
                return { ok: false, error: 'This line was changed again after that save, so it cannot be put back this way. Edit it by hand.' };
            }

            return this.stageEdit(cue, row.oldText);
        }

        /** Staged new lines that overlap the span, for the overlap warning (existing cues are checked with findOverlaps). */
        stagedOverlaps(startMs, endMs, exceptClientId) {
            return this.rows.filter(function (r) {
                return r.type === 'add' && r.status !== 'saved' && r.clientId !== exceptClientId &&
                    r.startMs < endMs && startMs < r.endMs;
            }).map(copyRow);
        }

        /**
         * The Save request for everything staged, or null when nothing is staged: { request, rowIds } where request is
         * the Edits body and rowIds are the rows it carries. Edits come first (by cue number), then new lines.
         */
        prepareSave() {
            const staged = this.rows.filter(function (r) {
                return r.status === 'staged';
            });
            if (staged.length === 0) {
                return null;
            }

            if (!this.baseHash) {
                throw new Error('The cues have not been loaded: there is no hash to save against.');
            }

            const edits = staged.filter(function (r) {
                return r.type === 'edit';
            }).sort(function (a, b) {
                return a.cueIndex - b.cueIndex;
            }).map(function (r) {
                return { type: 'edit', cueIndex: r.cueIndex, oldText: r.oldText, newText: r.newText };
            });
            const adds = staged.filter(function (r) {
                return r.type === 'add';
            }).map(function (r) {
                return { type: 'add', clientId: r.clientId, startMs: r.startMs, endMs: r.endMs, text: r.text };
            });
            return {
                request: { baseHash: this.baseHash, edits: edits.concat(adds) },
                rowIds: staged.map(function (r) {
                    return r.rowId;
                }),
            };
        }

        /**
         * Applies a successful save. The rows that were sent become 'saved'; each new line takes its cue number from
         * the response's addedCues mapping (clientId to number), and every edit row's cue number shifts for the lines
         * that were inserted before it. The response's hash becomes the new base hash.
         */
        applySaveSuccess(rowIds, response) {
            const sent = new Set(rowIds);
            const mapping = (response && response.addedCues) || {};
            const addedPositions = Object.keys(mapping).map(function (k) {
                return mapping[k];
            });
            this.rows.forEach(function (row) {
                if (row.type === 'edit') {
                    row.cueIndex = shiftPosition(row.cueIndex, addedPositions);
                } else if (Object.prototype.hasOwnProperty.call(mapping, row.clientId)) {
                    row.position = mapping[row.clientId];
                }

                if (sent.has(row.rowId)) {
                    row.status = 'saved';
                    row.message = null;
                }
            });
            if (response && response.hash) {
                this.baseHash = response.hash;
            }
        }

        /**
         * Interprets a refused save. For 409 FileChanged / TextMismatch it answers { action: 'reconcile' }: re-fetch the
         * cues and call reconcile. For an error that names a row (clientId of a new line, or cueIndex of an edit) the
         * row is marked failed and the answer is { action: 'marked', rowId }. Anything else is { action: 'show-error' }
         * and the rows stay staged (a refused save writes nothing).
         */
        applySaveError(rowIds, status, body) {
            const b = body || {};
            if (status === 409 && (b.code === 'FileChanged' || b.code === 'TextMismatch')) {
                return { action: 'reconcile', code: b.code, currentHash: b.currentHash || null, cueIndex: b.cueIndex || null };
            }

            const sent = new Set(rowIds);
            let row = null;
            if (typeof b.clientId === 'string' && b.clientId.length > 0) {
                row = this.rows.find(function (r) {
                    return r.type === 'add' && r.clientId === b.clientId && sent.has(r.rowId);
                }) || null;
            } else if (typeof b.cueIndex === 'number') {
                row = this.rows.find(function (r) {
                    return r.type === 'edit' && r.cueIndex === b.cueIndex && sent.has(r.rowId);
                }) || null;
            }

            if (row) {
                row.status = 'failed';
                row.message = b.message || 'The server refused this change.';
                return { action: 'marked', rowId: row.rowId };
            }

            return { action: 'show-error', message: b.message || 'The save failed.' };
        }

        /**
         * After the file changed on disk: takes the freshly fetched cues. A staged edit stays staged if its oldText is
         * still the text of its cue (found at the same number, or as the one cue with the same times); every other
         * staged edit is marked failed. New lines are always kept. Saved and already failed rows are left alone.
         * Returns { kept, failed, relocated, newLines } as lists of row ids.
         */
        reconcile(freshCues, freshHash) {
            this.cues = (freshCues || []).slice();
            if (freshHash) {
                this.baseHash = freshHash;
            }

            const summary = { kept: [], failed: [], relocated: [], newLines: [] };
            const cues = this.cues;
            this.rows.forEach(function (row) {
                if (row.status !== 'staged') {
                    return;
                }

                if (row.type === 'add') {
                    summary.newLines.push(row.rowId);
                    return;
                }

                const found = locateCue(row, cues);
                if (found) {
                    if (found.index !== row.cueIndex) {
                        summary.relocated.push(row.rowId);
                    }

                    row.cueIndex = found.index;
                    row.start = found.start;
                    row.end = found.end;
                    summary.kept.push(row.rowId);
                } else {
                    row.status = 'failed';
                    row.message = 'This line changed on disk after you loaded it, so your edit was not applied.';
                    summary.failed.push(row.rowId);
                }
            });
            return summary;
        }

        /** Forgets saved rows (when the panel closes, the list keeps only what is still unsaved). Returns how many. */
        dropSaved() {
            const before = this.rows.length;
            this.rows = this.rows.filter(function (r) {
                return r.status !== 'saved';
            });
            return before - this.rows.length;
        }

        /**
         * Applies a successful submission: the rows that were sent leave the list (they wait for an admin now, and the
         * file is unchanged). Returns how many left.
         */
        applySubmitSuccess(rowIds) {
            const sent = new Set(rowIds);
            const before = this.rows.length;
            this.rows = this.rows.filter(function (r) {
                return !sent.has(r.rowId);
            });
            return before - this.rows.length;
        }

        /** Forgets every row, saved or not (after "Revert to original"). Returns how many. */
        clear() {
            const count = this.rows.length;
            this.rows = [];
            return count;
        }

        /** The rows worth keeping across a page reload: everything not saved, as plain data. */
        serialize() {
            return {
                v: DRAFT_VERSION,
                rows: this.rows.filter(function (r) {
                    return r.status !== 'saved';
                }).map(function (r) {
                    const copy = copyRow(r);
                    copy.status = 'staged';
                    copy.message = null;
                    return copy;
                }),
            };
        }

        /**
         * Puts a stored draft back into an empty model, checking it against freshly fetched cues: an edit whose oldText
         * still matches (see locateCue) is staged again; any other edit comes back as a failed row. New lines come
         * back staged if they still pass the checks, otherwise failed with the reason. Returns { restored, failed }.
         */
        restore(draft, freshCues, freshHash) {
            this.setCues(freshCues, freshHash);
            const summary = { restored: [], failed: [] };
            if (!draft || draft.v !== DRAFT_VERSION || !Array.isArray(draft.rows)) {
                return summary;
            }

            const self = this;
            draft.rows.forEach(function (stored) {
                const row = copyRow(stored);
                if (row.type === 'edit' && typeof row.cueIndex === 'number' && typeof row.oldText === 'string' && typeof row.newText === 'string') {
                    const found = locateCue(row, self.cues);
                    if (found) {
                        row.cueIndex = found.index;
                        row.start = found.start;
                        row.end = found.end;
                        row.status = 'staged';
                        row.message = null;
                        summary.restored.push(row.rowId);
                    } else {
                        row.status = 'failed';
                        row.message = 'This line changed after your draft was saved, so the edit was not restored.';
                        summary.failed.push(row.rowId);
                    }

                    const n = Number(String(row.rowId).slice(1));
                    if (isFinite(n) && n > self.editSeq) {
                        self.editSeq = n;
                    }

                    self.rows.push(row);
                } else if (row.type === 'add' && typeof row.clientId === 'string') {
                    const checked = validateNewLine(row, { runtimeMs: self.runtimeMs, format: self.format });
                    if (checked.ok) {
                        row.text = checked.text;
                        row.status = 'staged';
                        row.message = null;
                        summary.restored.push(row.rowId);
                    } else {
                        row.status = 'failed';
                        row.message = checked.errors[0].message;
                        summary.failed.push(row.rowId);
                    }

                    const n = Number(String(row.clientId).replace(/^\D+/, ''));
                    if (isFinite(n) && n > self.addSeq) {
                        self.addSeq = n;
                    }

                    self.rows.push(row);
                }
            });
            return summary;
        }
    }

    // ---- who may do what, and submissions ----------------------------------------------------------------------

    /**
     * The editor mode for the server's Me answer: 'edit' (save directly: admins and users allowed to edit), 'submit'
     * (send changes for an admin's review) or null (no editor). Jellyfin leaves null fields out of its answers, so
     * only an explicit true counts.
     */
    function editorMode(me) {
        if (!me || typeof me !== 'object') {
            return null;
        }

        if (me.canEdit === true) {
            return 'edit';
        }

        return me.canSubmit === true ? 'submit' : null;
    }

    const SUBMISSION_STATES = {
        Pending: 'waiting for review',
        Approved: 'approved',
        PartlyApproved: 'partly approved',
        Rejected: 'rejected',
        Withdrawn: 'withdrawn',
    };

    /** A submission's state in words, for the list of the user's submissions. */
    function submissionStateLabel(state) {
        return Object.prototype.hasOwnProperty.call(SUBMISSION_STATES, state) ? SUBMISSION_STATES[state] : String(state || '').toLowerCase();
    }

    /**
     * The submissions of one subtitle (item and stream), newest first. Ids are compared without dashes and case, as
     * Jellyfin writes them both ways.
     */
    function submissionsFor(list, itemId, streamIndex) {
        const want = String(itemId || '').replace(/-/g, '').toLowerCase();
        return (Array.isArray(list) ? list : []).filter(function (s) {
            return s && String(s.itemId || '').replace(/-/g, '').toLowerCase() === want && s.streamIndex === streamIndex;
        }).sort(function (a, b) {
            return String(b.created || '').localeCompare(String(a.created || ''));
        });
    }

    // ---- drafts in localStorage -------------------------------------------------------------------------------

    /** The storage key of a draft: user id, item id and stream index, so drafts of different users never mix. */
    function draftKey(userId, itemId, streamIndex) {
        return 'sqe:v' + DRAFT_VERSION + ':' + encodeURIComponent(String(userId)) + ':' +
            encodeURIComponent(String(itemId)) + ':' + encodeURIComponent(String(streamIndex));
    }

    /**
     * A thin wrapper over localStorage in which nothing can throw. `getStorage` returns the storage object; it may
     * itself throw (blocked storage) or return nothing. save/load/remove report failure by returning false / null.
     */
    function createDraftStore(getStorage) {
        function storage() {
            try {
                const s = getStorage();
                return s && typeof s.getItem === 'function' ? s : null;
            } catch (e) {
                return null;
            }
        }

        return {
            /** Stores a draft ({ v, rows }); an empty draft removes the entry. Returns true when storage accepted it. */
            save: function (userId, itemId, streamIndex, draft) {
                const s = storage();
                if (!s) {
                    return false;
                }

                try {
                    const key = draftKey(userId, itemId, streamIndex);
                    if (!draft || !Array.isArray(draft.rows) || draft.rows.length === 0) {
                        s.removeItem(key);
                    } else {
                        s.setItem(key, JSON.stringify(draft));
                    }

                    return true;
                } catch (e) {
                    return false;
                }
            },

            /** The stored draft, or null if there is none, it is damaged, or it is from another version. */
            load: function (userId, itemId, streamIndex) {
                const s = storage();
                if (!s) {
                    return null;
                }

                const key = draftKey(userId, itemId, streamIndex);
                try {
                    const raw = s.getItem(key);
                    if (raw === null || raw === undefined) {
                        return null;
                    }

                    const parsed = JSON.parse(raw);
                    if (parsed && parsed.v === DRAFT_VERSION && Array.isArray(parsed.rows)) {
                        return parsed;
                    }
                } catch (e) {
                    try {
                        s.removeItem(key);
                    } catch (ignored) {
                        // nothing more to do
                    }
                }

                return null;
            },

            remove: function (userId, itemId, streamIndex) {
                const s = storage();
                if (!s) {
                    return false;
                }

                try {
                    s.removeItem(draftKey(userId, itemId, streamIndex));
                    return true;
                } catch (e) {
                    return false;
                }
            },
        };
    }

    // ---- waiting for window.ApiClient ---------------------------------------------------------------------------

    function clientIsReady(client, requireLogin) {
        if (!client || typeof client !== 'object' || typeof client.getCurrentUser !== 'function' || typeof client.getUrl !== 'function') {
            return false;
        }

        if (!requireLogin) {
            return true;
        }

        if (typeof client.isLoggedIn === 'function' && client.isLoggedIn() !== true) {
            return false;
        }

        return typeof client.accessToken !== 'function' || Boolean(client.accessToken());
    }

    /**
     * Resolves with window.ApiClient once it exists and (unless requireLogin is false) has a logged-in user, checking
     * every intervalMs and rejecting after timeoutMs. The client is read fresh on every check, because jellyfin-web can
     * replace the instance. Options: getClient, timeoutMs (30000), intervalMs (100), requireLogin (true), and now /
     * setTimer for tests.
     */
    function waitForApiClient(options) {
        const o = Object.assign({
            getClient: function () {
                return typeof window !== 'undefined' ? window.ApiClient : undefined;
            },
            timeoutMs: 30000,
            intervalMs: 100,
            requireLogin: true,
            now: Date.now,
            setTimer: setTimeout,
        }, options || {});
        // Called as plain functions, never as methods of `o`: browsers refuse setTimeout with another `this`
        // ("Illegal invocation").
        const nowFn = o.now;
        const setTimer = o.setTimer;
        return new Promise(function (resolve, reject) {
            const started = nowFn();
            function check() {
                let client = null;
                let ready = false;
                try {
                    client = o.getClient();
                    ready = clientIsReady(client, o.requireLogin);
                } catch (e) {
                    ready = false;
                }

                if (ready) {
                    resolve(client);
                    return;
                }

                if (nowFn() - started >= o.timeoutMs) {
                    reject(new Error('Timed out waiting for ApiClient.'));
                    return;
                }

                setTimer(check, o.intervalMs);
            }

            check();
        });
    }

    // ---- which subtitle track is on: Sessions lags behind the menu ---------------------------------------------

    /** The stream index in a subtitle menu item's data-id: an integer (-1 is Off), or null for entries such as "secondarysubtitle". */
    function parseMenuId(value) {
        if (typeof value !== 'string' || !/^-?\d+$/.test(value.trim())) {
            return null;
        }

        return Number(value.trim());
    }

    /** True once enough time has passed since a menu click for a Sessions answer to show it. */
    function sessionsCanConfirm(menuClickedAtMs, nowMs, lagMs) {
        const lag = typeof lagMs === 'number' ? lagMs : SESSIONS_LAG_MS;
        return nowMs - menuClickedAtMs >= lag;
    }

    /**
     * Decides which subtitle stream is on when GET /Sessions and the last subtitle-menu click may disagree.
     * GET /Sessions shows a track change only after a delay (about 6 s measured, SESSIONS_LAG_MS allows 10 s), so:
     *  - no menu click known: Sessions is the answer;
     *  - they agree: that index, confirmed;
     *  - they disagree and the Sessions request started less than the lag after the click: Sessions has not caught up,
     *    so the menu click wins (not confirmed yet);
     *  - they disagree and the Sessions request started at least the lag after the click: Sessions wins (the click did
     *    not take, or something else changed the track).
     * Input: sessionIndex, sessionRequestedAtMs (when the Sessions request was sent), menuIndex, menuClickedAtMs, lagMs.
     * Output: { index, source: 'sessions' | 'menu' | 'none', confirmed }.
     */
    function resolveSubtitleTrack(input) {
        const i = input || {};
        const lag = typeof i.lagMs === 'number' ? i.lagMs : SESSIONS_LAG_MS;
        const hasSession = typeof i.sessionIndex === 'number' && isFinite(i.sessionIndex);
        const hasMenu = typeof i.menuIndex === 'number' && isFinite(i.menuIndex) &&
            typeof i.menuClickedAtMs === 'number' && isFinite(i.menuClickedAtMs);
        if (!hasMenu) {
            return hasSession ? { index: i.sessionIndex, source: 'sessions', confirmed: true } : { index: null, source: 'none', confirmed: false };
        }

        if (hasSession && i.sessionIndex === i.menuIndex) {
            return { index: i.menuIndex, source: 'sessions', confirmed: true };
        }

        const caughtUp = hasSession && typeof i.sessionRequestedAtMs === 'number' && i.sessionRequestedAtMs - i.menuClickedAtMs >= lag;
        if (caughtUp) {
            return { index: i.sessionIndex, source: 'sessions', confirmed: true };
        }

        return { index: i.menuIndex, source: 'menu', confirmed: false };
    }

    return {
        MAX_TIME_MS: MAX_TIME_MS,
        MIN_CUE_MS: MIN_CUE_MS,
        SESSIONS_LAG_MS: SESSIONS_LAG_MS,
        parseTime: parseTime,
        formatTime: formatTime,
        secondsToMs: secondsToMs,
        nudgeTime: nudgeTime,
        sortedCues: sortedCues,
        cuesAt: cuesAt,
        nearestPrevious: nearestPrevious,
        nextAfter: nextAfter,
        cueForTime: cueForTime,
        stepCue: stepCue,
        defaultNewLine: defaultNewLine,
        findOverlaps: findOverlaps,
        overlapWarning: overlapWarning,
        normalizeText: normalizeText,
        validateCueText: validateCueText,
        validateNewLine: validateNewLine,
        splitStyleTags: splitStyleTags,
        hasStyleTags: hasStyleTags,
        screenText: screenText,
        shiftPosition: shiftPosition,
        locateCue: locateCue,
        StagingModel: StagingModel,
        editorMode: editorMode,
        submissionStateLabel: submissionStateLabel,
        submissionsFor: submissionsFor,
        draftKey: draftKey,
        createDraftStore: createDraftStore,
        waitForApiClient: waitForApiClient,
        parseMenuId: parseMenuId,
        sessionsCanConfirm: sessionsCanConfirm,
        resolveSubtitleTrack: resolveSubtitleTrack,
    };
}));
