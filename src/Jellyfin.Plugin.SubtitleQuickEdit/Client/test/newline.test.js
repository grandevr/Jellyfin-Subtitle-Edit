'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

const cue = (index, start, end, text) => ({ index: index, start: start, end: end, text: text || 'cue ' + index });

// ---- default times of a new line ------------------------------------------------------------------------------

test('default new line: start = now, end = start + 2 s when nothing is in the way', () => {
    assert.deepEqual(L.defaultNewLine([], 1000), { startMs: 1000, endMs: 3000 });
    assert.deepEqual(L.defaultNewLine([cue(1, 0, 500)], 1000), { startMs: 1000, endMs: 3000 });
    assert.deepEqual(L.defaultNewLine([cue(1, 9000, 9500)], 1000), { startMs: 1000, endMs: 3000 });
});

test('default new line: end is the next cue\'s start when that comes sooner', () => {
    assert.deepEqual(L.defaultNewLine([cue(1, 2200, 4000)], 1000), { startMs: 1000, endMs: 2200 });
    assert.deepEqual(L.defaultNewLine([cue(1, 9000, 9500), cue(2, 1800, 2500)], 1000), { startMs: 1000, endMs: 1800 });
});

test('default new line: a next cue exactly 2 s away leaves the end at start + 2 s', () => {
    assert.deepEqual(L.defaultNewLine([cue(1, 3000, 4000)], 1000), { startMs: 1000, endMs: 3000 });
});

test('default new line: the length is never below 0.5 s', () => {
    assert.deepEqual(L.defaultNewLine([cue(1, 1200, 4000)], 1000), { startMs: 1000, endMs: 1500 });
    assert.deepEqual(L.defaultNewLine([cue(1, 1499, 4000)], 1000), { startMs: 1000, endMs: 1500 });
    assert.deepEqual(L.defaultNewLine([cue(1, 1500, 4000)], 1000), { startMs: 1000, endMs: 1500 });
    assert.deepEqual(L.defaultNewLine([cue(1, 1501, 4000)], 1000), { startMs: 1000, endMs: 1501 });
});

test('default new line: a cue starting at the same moment is not "the next cue"', () => {
    assert.deepEqual(L.defaultNewLine([cue(1, 1000, 4000), cue(2, 2500, 3000)], 1000), { startMs: 1000, endMs: 2500 });
    assert.deepEqual(L.defaultNewLine([cue(1, 1000, 4000)], 1000), { startMs: 1000, endMs: 3000 });
});

test('default new line: the start is whole milliseconds and not negative', () => {
    assert.deepEqual(L.defaultNewLine([], 1234.6), { startMs: 1235, endMs: 3235 });
    assert.deepEqual(L.defaultNewLine([], -50), { startMs: 0, endMs: 2000 });
});

test('default new line: the end stops at the runtime', () => {
    assert.deepEqual(L.defaultNewLine([], 59000, 60000), { startMs: 59000, endMs: 60000 });
    assert.deepEqual(L.defaultNewLine([], 10000, 60000), { startMs: 10000, endMs: 12000 });
});

test('default new line: near the runtime the start moves back so the line stays 0.5 s long and valid', () => {
    assert.deepEqual(L.defaultNewLine([], 59800, 60000), { startMs: 59500, endMs: 60000 });
    assert.deepEqual(L.defaultNewLine([], 60000, 60000), { startMs: 59500, endMs: 60000 });
    assert.deepEqual(L.defaultNewLine([], 200, 300), { startMs: 0, endMs: 300 });
    const line = L.defaultNewLine([], 59950, 60000);
    assert.equal(L.validateNewLine({ startMs: line.startMs, endMs: line.endMs, text: 'x' }, { runtimeMs: 60000 }).ok, true);
});

// ---- overlaps -------------------------------------------------------------------------------------------------

const SPAN = [cue(1, 0, 1000), cue(2, 1000, 2000), cue(3, 1500, 3000), cue(4, 5000, 6000)];

test('findOverlaps: touching ends are not overlaps', () => {
    assert.deepEqual(L.findOverlaps(SPAN, 2000, 2500).map((c) => c.index), [3]);
    assert.deepEqual(L.findOverlaps(SPAN, 3000, 4000), []);
    assert.deepEqual(L.findOverlaps(SPAN, 6000, 7000), []);
    assert.deepEqual(L.findOverlaps(SPAN, 4000, 5000), []);
});

test('findOverlaps: partial, containing and contained overlaps, in time order', () => {
    assert.deepEqual(L.findOverlaps(SPAN, 900, 1100).map((c) => c.index), [1, 2]);
    assert.deepEqual(L.findOverlaps(SPAN, 0, 10000).map((c) => c.index), [1, 2, 3, 4]);
    assert.deepEqual(L.findOverlaps(SPAN, 5200, 5300).map((c) => c.index), [4]);
    assert.deepEqual(L.findOverlaps(SPAN.slice().reverse(), 900, 1100).map((c) => c.index), [1, 2]);
});

test('overlapWarning is null without overlaps and names the cues otherwise', () => {
    assert.equal(L.overlapWarning([]), null);
    assert.equal(L.overlapWarning(null), null);
    assert.equal(L.overlapWarning([SPAN[2]]), 'Overlaps cue 3 (00:00:01,500 to 00:00:03,000).');
    assert.equal(L.overlapWarning([SPAN[0], SPAN[1], SPAN[2]]), 'Overlaps 3 cues: 1, 2, 3.');
});

test('overlapWarning also counts overlapped staged new lines', () => {
    assert.equal(L.overlapWarning([], 0), null);
    assert.equal(L.overlapWarning([], 1), 'Overlaps 1 staged new line.');
    assert.equal(L.overlapWarning(null, 2), 'Overlaps 2 staged new lines.');
    assert.equal(L.overlapWarning([SPAN[2]], 1), 'Overlaps cue 3 (00:00:01,500 to 00:00:03,000). Also overlaps 1 staged new line.');
});

// ---- text rules -----------------------------------------------------------------------------------------------

test('validateCueText accepts ordinary text and normalizes line breaks', () => {
    assert.deepEqual(L.validateCueText('Hello'), { ok: true, text: 'Hello' });
    assert.deepEqual(L.validateCueText('one\ntwo'), { ok: true, text: 'one\ntwo' });
    assert.deepEqual(L.validateCueText('one\r\ntwo'), { ok: true, text: 'one\ntwo' });
    assert.deepEqual(L.validateCueText('one\rtwo'), { ok: true, text: 'one\ntwo' });
    assert.deepEqual(L.validateCueText('tab\tseparated'), { ok: true, text: 'tab\tseparated' });
    assert.deepEqual(L.validateCueText('<i>tags</i> & {\\an8}'), { ok: true, text: '<i>tags</i> & {\\an8}' });
    assert.equal(L.validateCueText('--> not a time line').ok, true);
    // Same rule as the server: only a line that starts with a time line is refused.
    assert.equal(L.validateCueText('he said 00:00:01,000 --> 00:00:02,000').ok, true);
    assert.equal(L.validateCueText('  00:00:01,000 --> 00:00:02,000').ok, false);
});

test('validateCueText rejects what the server rejects', () => {
    const bad = {
        '': 'TextEmpty', ' ': 'TextBlankLine', '   ': 'TextBlankLine',
        'a\n\nb': 'TextBlankLine', 'a\n': 'TextBlankLine', '\na': 'TextBlankLine', 'a\n   \nb': 'TextBlankLine', 'a\r\n\r\nb': 'TextBlankLine',
        '00:00:01,000 --> 00:00:02,000': 'TextLooksLikeTime', 'fine\n00:00:01,000 --> 00:00:02,000': 'TextLooksLikeTime',
        '1:2:3,4 --> 5:6:7,8': 'TextLooksLikeTime',
        'bell\u0007': 'TextControlChar', 'nul\0here': 'TextControlChar', 'esc\u001b[0m': 'TextControlChar',
    };
    Object.keys(bad).forEach((text) => {
        const r = L.validateCueText(text);
        assert.equal(r.ok, false, 'should reject ' + JSON.stringify(text));
        assert.equal(r.error.code, bad[text], JSON.stringify(text));
        assert.ok(r.error.message.length > 0);
    });
});

test('validateCueText rejects values that are not strings', () => {
    [null, undefined, 5, {}, []].forEach((v) => assert.equal(L.validateCueText(v).ok, false));
});

// ---- checks of an added line ----------------------------------------------------------------------------------

const line = (startMs, endMs, text) => ({ startMs: startMs, endMs: endMs, text: text === undefined ? 'New line' : text });
const fields = (r) => r.errors.map((e) => e.field);

test('validateNewLine accepts a good line', () => {
    const r = L.validateNewLine(line(1000, 3000), { runtimeMs: 60000 });
    assert.equal(r.ok, true);
    assert.deepEqual(r.errors, []);
    assert.equal(r.text, 'New line');
});

test('validateNewLine: the end must be after the start', () => {
    assert.deepEqual(fields(L.validateNewLine(line(5000, 4000))), ['end']);
    assert.deepEqual(fields(L.validateNewLine(line(5000, 5000))), ['end']);
});

test('validateNewLine: at least 100 ms long', () => {
    assert.equal(L.validateNewLine(line(5000, 5099)).ok, false);
    assert.equal(L.validateNewLine(line(5000, 5100)).ok, true);
    assert.match(L.validateNewLine(line(5000, 5050)).errors[0].message, /100 ms/);
});

test('validateNewLine: not before zero', () => {
    const r = L.validateNewLine(line(-1, 1000));
    assert.deepEqual(fields(r), ['start']);
    assert.equal(L.validateNewLine(line(0, 100)).ok, true);
});

test('validateNewLine: within the runtime when it is known', () => {
    assert.equal(L.validateNewLine(line(59000, 60000), { runtimeMs: 60000 }).ok, true);
    const over = L.validateNewLine(line(59000, 60001), { runtimeMs: 60000 });
    assert.deepEqual(fields(over), ['end']);
    assert.match(over.errors[0].message, /end of the video/);
    assert.equal(L.validateNewLine(line(59000, 61000)).ok, true);
    assert.equal(L.validateNewLine(line(59000, 61000), { runtimeMs: null }).ok, true);
});

test('validateNewLine: within what an .srt file can hold', () => {
    assert.equal(L.validateNewLine(line(359999000, 359999999)).ok, true);
    assert.deepEqual(fields(L.validateNewLine(line(359999000, 360000000))), ['end']);
});

test('validateNewLine: the text rules apply', () => {
    assert.deepEqual(fields(L.validateNewLine(line(1000, 2000, ''))), ['text']);
    assert.deepEqual(fields(L.validateNewLine(line(1000, 2000, 'a\n\nb'))), ['text']);
    assert.deepEqual(fields(L.validateNewLine(line(1000, 2000, '00:00:01,000 --> 00:00:02,000'))), ['text']);
});

test('validateNewLine: missing or non-numeric times are named', () => {
    assert.deepEqual(fields(L.validateNewLine(line(null, 2000))), ['start']);
    assert.deepEqual(fields(L.validateNewLine(line(1000, NaN))), ['end']);
    assert.deepEqual(fields(L.validateNewLine(line('1000', 2000))), ['start']);
    assert.deepEqual(fields(L.validateNewLine(null)).sort(), ['end', 'start', 'text']);
});

test('validateNewLine collects every problem', () => {
    assert.deepEqual(fields(L.validateNewLine(line(5000, 4000, ''))).sort(), ['end', 'text']);
});
