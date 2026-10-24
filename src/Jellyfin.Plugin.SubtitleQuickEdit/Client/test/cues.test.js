'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

const cue = (index, start, end, text) => ({ index: index, start: start, end: end, text: text || 'cue ' + index });
const nums = (cues) => cues.map((c) => c.index);

// 1: 0-2000, 2: 2000-4000 (touches 1), 3: 3000-6000 (overlaps 2), 4: 10000-12000
const CUES = [cue(1, 0, 2000), cue(2, 2000, 4000), cue(3, 3000, 6000), cue(4, 10000, 12000)];

test('cuesAt includes the start and excludes the end', () => {
    assert.deepEqual(nums(L.cuesAt(CUES, 0)), [1]);
    assert.deepEqual(nums(L.cuesAt(CUES, 1999)), [1]);
    assert.deepEqual(nums(L.cuesAt(CUES, 2000)), [2]);
    assert.deepEqual(nums(L.cuesAt(CUES, 3999)), [2, 3]);
    assert.deepEqual(nums(L.cuesAt(CUES, 4000)), [3]);
    assert.deepEqual(nums(L.cuesAt(CUES, 6000)), []);
    assert.deepEqual(nums(L.cuesAt(CUES, -1)), []);
});

test('cuesAt returns every overlapping cue, in time order, whatever the input order', () => {
    assert.deepEqual(nums(L.cuesAt(CUES, 3500)), [2, 3]);
    assert.deepEqual(nums(L.cuesAt(CUES.slice().reverse(), 3500)), [2, 3]);
    const same = [cue(7, 1000, 3000), cue(5, 1000, 3000), cue(6, 500, 3000)];
    assert.deepEqual(nums(L.cuesAt(same, 2000)), [6, 5, 7]);
});

test('cuesAt ignores cues whose end is not after their start', () => {
    const odd = [cue(1, 5000, 5000), cue(2, 5000, 4000)];
    assert.deepEqual(L.cuesAt(odd, 5000), []);
    assert.deepEqual(L.cuesAt(odd, 4500), []);
});

test('cueForTime shows the active cues, several when they overlap', () => {
    const one = L.cueForTime(CUES, 1000);
    assert.equal(one.mode, 'active');
    assert.deepEqual(nums(one.cues), [1]);
    const two = L.cueForTime(CUES, 3500);
    assert.equal(two.mode, 'active');
    assert.deepEqual(nums(two.cues), [2, 3]);
});

test('cueForTime shows the nearest previous cue when nothing is on screen', () => {
    const gap = L.cueForTime(CUES, 8000);
    assert.equal(gap.mode, 'previous');
    assert.deepEqual(nums(gap.cues), [3]);
    const after = L.cueForTime(CUES, 99999);
    assert.equal(after.mode, 'previous');
    assert.deepEqual(nums(after.cues), [4]);
    assert.equal(L.cueForTime(CUES, 6000).mode, 'previous');
    assert.deepEqual(nums(L.cueForTime(CUES, 6000).cues), [3]);
});

test('the nearest previous cue is the one that ended last, not the one that started last', () => {
    const cues = [cue(1, 0, 9000), cue(2, 1000, 2000)];
    assert.deepEqual(nums(L.cueForTime(cues, 9500).cues), [1]);
});

test('previous-cue ties: later start wins, then later position', () => {
    const byStart = [cue(5, 0, 5000), cue(6, 2000, 5000)];
    assert.deepEqual(nums(L.cueForTime(byStart, 7000).cues), [6]);
    const byIndex = [cue(8, 2000, 5000), cue(9, 2000, 5000)];
    assert.deepEqual(nums(L.cueForTime(byIndex, 7000).cues), [9]);
});

test('before the first cue the first upcoming cue is shown, mode next', () => {
    const early = L.cueForTime(CUES.map((c) => cue(c.index, c.start + 1000, c.end + 1000)), 500);
    assert.equal(early.mode, 'next');
    assert.deepEqual(nums(early.cues), [1]);
});

test('no cues at all is mode empty', () => {
    assert.deepEqual(L.cueForTime([], 1000), { mode: 'empty', cues: [] });
    assert.deepEqual(L.cueForTime(undefined, 1000), { mode: 'empty', cues: [] });
});

test('a cue that starts after the time is never "previous", even if its end is before the time', () => {
    const odd = [cue(1, 9000, 8000), cue(2, 1000, 2000)];
    assert.equal(L.nearestPrevious(odd, 8500).index, 2);
    assert.equal(L.nearestPrevious([cue(1, 9000, 8000)], 8500), null);
});

test('nearestPrevious and nextAfter', () => {
    assert.equal(L.nearestPrevious(CUES, 500), null);
    assert.equal(L.nearestPrevious(CUES, 6500).index, 3);
    assert.equal(L.nextAfter(CUES, 6500).index, 4);
    assert.equal(L.nextAfter(CUES, 10000), null);
    assert.equal(L.nextAfter(CUES, 9999).index, 4);
});

test('stepCue goes to the neighbouring cue in time order', () => {
    assert.equal(L.stepCue(CUES, [CUES[1]], -1).index, 1);
    assert.equal(L.stepCue(CUES, [CUES[1]], 1).index, 3);
    assert.equal(L.stepCue(CUES, [CUES[2]], 1).index, 4);
    assert.equal(L.stepCue(CUES, [CUES[3]], -1).index, 3);
});

test('stepCue returns null at either end', () => {
    assert.equal(L.stepCue(CUES, [CUES[0]], -1), null);
    assert.equal(L.stepCue(CUES, [CUES[3]], 1), null);
});

test('with several overlapping cues shown, Previous goes before the first and Next after the last', () => {
    const shown = [CUES[1], CUES[2]];
    assert.equal(L.stepCue(CUES, shown, -1).index, 1);
    assert.equal(L.stepCue(CUES, shown, 1).index, 4);
    assert.equal(L.stepCue(CUES, shown.slice().reverse(), -1).index, 1);
});

test('stepCue follows time order even when the file order differs', () => {
    const odd = [cue(1, 5000, 6000), cue(2, 1000, 2000), cue(3, 3000, 4000)];
    assert.equal(L.stepCue(odd, [odd[1]], 1).index, 3);
    assert.equal(L.stepCue(odd, [odd[2]], 1).index, 1);
    assert.equal(L.stepCue(odd, [odd[0]], -1).index, 3);
});

test('stepCue with nothing shown, no cues, or an unknown cue is null', () => {
    assert.equal(L.stepCue(CUES, [], 1), null);
    assert.equal(L.stepCue(CUES, null, 1), null);
    assert.equal(L.stepCue([], [CUES[0]], 1), null);
    assert.equal(L.stepCue(CUES, [cue(99, 0, 1)], 1), null);
});

test('sortedCues does not change its input', () => {
    const input = [cue(2, 5000, 6000), cue(1, 1000, 2000)];
    assert.deepEqual(nums(L.sortedCues(input)), [1, 2]);
    assert.deepEqual(nums(input), [2, 1]);
});
