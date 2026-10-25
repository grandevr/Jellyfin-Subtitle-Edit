'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

// ---- what the panel offers for a track inside the video --------------------------------------------------------

test('a track that is not extractable offers nothing', () => {
    assert.deepEqual(L.extractChoice(null, null), { kind: 'none' });
    assert.deepEqual(L.extractChoice({ extractable: false }, { exists: false }), { kind: 'none' });
});

test('an existing copy is offered as a switch, before any preview', () => {
    assert.deepEqual(L.extractChoice({ extractable: true, extractedStreamIndex: 0 }, null), { kind: 'switch', index: 0 });
    assert.deepEqual(L.extractChoice({ extractable: true, extractedStreamIndex: 7 }, { exists: true }), { kind: 'switch', index: 7 });
});

test('without a preview the panel waits; a free name offers to make the copy', () => {
    assert.deepEqual(L.extractChoice({ extractable: true, extractedStreamIndex: null }, null), { kind: 'loading' });
    assert.deepEqual(L.extractChoice({ extractable: true }, { exists: false, canReplace: true }), { kind: 'make' });
});

test('a taken name: admins may replace, others may only switch to it when it is a track', () => {
    assert.deepEqual(L.extractChoice({ extractable: true }, { exists: true, canReplace: true, existingStreamIndex: 4 }),
        { kind: 'replace', index: 4 });
    assert.deepEqual(L.extractChoice({ extractable: true }, { exists: true, canReplace: false, existingStreamIndex: 0 }),
        { kind: 'exists', index: 0 });
    assert.deepEqual(L.extractChoice({ extractable: true }, { exists: true, canReplace: false, existingStreamIndex: null }),
        { kind: 'exists', index: null });
});

// ---- the comparison before a Replace ---------------------------------------------------------------------------

test('lines are compared by position, time and text', () => {
    const cue = (start, end, text) => ({ start: start, end: end, text: text });
    const result = L.lineUpCues(
        [cue(1000, 2000, 'Same'), cue(3000, 4000, 'Old text'), cue(5000, 6000, 'Moved')],
        [cue(1000, 2000, 'Same'), cue(3000, 4000, 'New text'), cue(5000, 6500, 'Moved'), cue(7000, 8000, 'Extra')]);
    assert.deepEqual(result.rows.map((r) => r.same), [true, false, false, false]);
    assert.equal(result.differing, 3);
    assert.equal(result.rows[3].left, null);
    assert.equal(result.rows[3].right.text, 'Extra');
});

test('a shorter extracted side leaves the right column empty', () => {
    const result = L.lineUpCues([{ start: 0, end: 1, text: 'a' }, { start: 2, end: 3, text: 'b' }], [{ start: 0, end: 1, text: 'a' }]);
    assert.equal(result.rows.length, 2);
    assert.equal(result.rows[1].right, null);
    assert.equal(result.differing, 1);
    assert.deepEqual(L.lineUpCues(null, undefined), { rows: [], differing: 0 });
});

// ---- switching the player to a track it did not have -----------------------------------------------------------

test('the restart command keeps the place and picks the new track', () => {
    assert.deepEqual(L.playWithSubtitle('s/1', 'abc', 12345.6, 5), {
        path: 'Sessions/s%2F1/Playing',
        params: { playCommand: 'PlayNow', itemIds: 'abc', mediaSourceId: 'abc', startPositionTicks: 123460000, subtitleStreamIndex: 5 },
    });
    assert.equal(L.playWithSubtitle('s', 'abc', -5, 0).params.startPositionTicks, 0);
    assert.equal(L.playWithSubtitle('s', 'abc', null, 0).params.startPositionTicks, 0);
});
