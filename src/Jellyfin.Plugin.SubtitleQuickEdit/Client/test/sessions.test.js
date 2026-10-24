'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

// The rule: GET /Sessions shows a subtitle change only after a delay (about 6 s measured; the module allows 10 s),
// so a recent menu click beats a Sessions answer that disagrees with it.
const LAG = L.SESSIONS_LAG_MS;

test('the lag constant is 10 seconds', () => {
    assert.equal(LAG, 10000);
});

test('without a menu click, Sessions is the answer', () => {
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: 1, sessionRequestedAtMs: 5000 }), { index: 1, source: 'sessions', confirmed: true });
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: -1 }), { index: -1, source: 'sessions', confirmed: true });
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: 0, menuIndex: null, menuClickedAtMs: null }), { index: 0, source: 'sessions', confirmed: true });
});

test('without a menu click and without a session there is no answer', () => {
    assert.deepEqual(L.resolveSubtitleTrack({}), { index: null, source: 'none', confirmed: false });
    assert.deepEqual(L.resolveSubtitleTrack(null), { index: null, source: 'none', confirmed: false });
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: undefined }), { index: null, source: 'none', confirmed: false });
});

test('when Sessions and the menu agree, that is the answer, confirmed', () => {
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: 1, sessionRequestedAtMs: 1000, menuIndex: 1, menuClickedAtMs: 900 }), { index: 1, source: 'sessions', confirmed: true });
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: -1, sessionRequestedAtMs: 1000, menuIndex: -1, menuClickedAtMs: 900 }), { index: -1, source: 'sessions', confirmed: true });
});

test('a disagreeing Sessions answer asked soon after the click has not caught up: the menu wins', () => {
    const r = L.resolveSubtitleTrack({ sessionIndex: 0, sessionRequestedAtMs: 12000, menuIndex: 1, menuClickedAtMs: 10000 });
    assert.deepEqual(r, { index: 1, source: 'menu', confirmed: false });
    const justUnder = L.resolveSubtitleTrack({ sessionIndex: 0, sessionRequestedAtMs: 10000 + LAG - 1, menuIndex: 1, menuClickedAtMs: 10000 });
    assert.equal(justUnder.source, 'menu');
});

test('a disagreeing Sessions answer asked a full lag after the click wins', () => {
    const exactly = L.resolveSubtitleTrack({ sessionIndex: 0, sessionRequestedAtMs: 10000 + LAG, menuIndex: 1, menuClickedAtMs: 10000 });
    assert.deepEqual(exactly, { index: 0, source: 'sessions', confirmed: true });
    const later = L.resolveSubtitleTrack({ sessionIndex: 2, sessionRequestedAtMs: 60000, menuIndex: 1, menuClickedAtMs: 10000 });
    assert.deepEqual(later, { index: 2, source: 'sessions', confirmed: true });
});

test('Off (-1) chosen in the menu beats a Sessions answer that still shows a track', () => {
    const r = L.resolveSubtitleTrack({ sessionIndex: 0, sessionRequestedAtMs: 3000, menuIndex: -1, menuClickedAtMs: 2000 });
    assert.deepEqual(r, { index: -1, source: 'menu', confirmed: false });
});

test('a menu click with no Sessions answer at all uses the menu', () => {
    assert.deepEqual(L.resolveSubtitleTrack({ menuIndex: 1, menuClickedAtMs: 5000 }), { index: 1, source: 'menu', confirmed: false });
    assert.deepEqual(L.resolveSubtitleTrack({ sessionIndex: null, menuIndex: 1, menuClickedAtMs: 5000, sessionRequestedAtMs: 90000 }), { index: 1, source: 'menu', confirmed: false });
});

test('a Sessions answer with no request time is never treated as having caught up', () => {
    const r = L.resolveSubtitleTrack({ sessionIndex: 0, menuIndex: 1, menuClickedAtMs: 10000 });
    assert.equal(r.source, 'menu');
});

test('a Sessions answer asked before the click cannot have caught up', () => {
    const r = L.resolveSubtitleTrack({ sessionIndex: 0, sessionRequestedAtMs: 5000, menuIndex: 1, menuClickedAtMs: 10000 });
    assert.equal(r.source, 'menu');
});

test('a custom lag is honoured', () => {
    const input = { sessionIndex: 0, sessionRequestedAtMs: 13000, menuIndex: 1, menuClickedAtMs: 10000 };
    assert.equal(L.resolveSubtitleTrack(Object.assign({ lagMs: 5000 }, input)).source, 'menu');
    assert.equal(L.resolveSubtitleTrack(Object.assign({ lagMs: 3000 }, input)).source, 'sessions');
});

test('sessionsCanConfirm is true once the lag has passed since the click', () => {
    assert.equal(L.sessionsCanConfirm(10000, 10000), false);
    assert.equal(L.sessionsCanConfirm(10000, 10000 + LAG - 1), false);
    assert.equal(L.sessionsCanConfirm(10000, 10000 + LAG), true);
    assert.equal(L.sessionsCanConfirm(10000, 10000 + 5000, 5000), true);
    assert.equal(L.sessionsCanConfirm(10000, 10000 + 4999, 5000), false);
});

test('parseMenuId reads the stream index from a menu item, and ignores entries that are not tracks', () => {
    assert.equal(L.parseMenuId('-1'), -1);
    assert.equal(L.parseMenuId('0'), 0);
    assert.equal(L.parseMenuId('7'), 7);
    assert.equal(L.parseMenuId(' 3 '), 3);
    ['secondarysubtitle', '', '1.5', 'a1', null, undefined, 3].forEach((v) => assert.equal(L.parseMenuId(v), null));
});
