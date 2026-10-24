'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

function memoryStorage() {
    const map = new Map();
    return {
        map: map,
        getItem: (k) => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => { map.set(k, String(v)); },
        removeItem: (k) => { map.delete(k); },
    };
}

const DRAFT = { v: 1, rows: [{ rowId: 'e1', type: 'edit', cueIndex: 2, oldText: 'a', newText: 'b' }] };

test('the key holds the user id, the item id and the stream index', () => {
    const key = L.draftKey('user1', 'item1', 3);
    assert.ok(key.includes('user1') && key.includes('item1') && key.endsWith(':3'));
    assert.notEqual(L.draftKey('user1', 'item1', 3), L.draftKey('user2', 'item1', 3));
    assert.notEqual(L.draftKey('user1', 'item1', 3), L.draftKey('user1', 'item2', 3));
    assert.notEqual(L.draftKey('user1', 'item1', 3), L.draftKey('user1', 'item1', 4));
});

test('ids with a colon in them cannot make two different keys the same', () => {
    assert.notEqual(L.draftKey('a:b', 'c', 1), L.draftKey('a', 'b:c', 1));
});

test('save then load returns the draft', () => {
    const s = memoryStorage();
    const store = L.createDraftStore(() => s);
    assert.equal(store.save('u', 'i', 1, DRAFT), true);
    assert.deepEqual(store.load('u', 'i', 1), DRAFT);
});

test('drafts of different users, items and streams are kept apart', () => {
    const s = memoryStorage();
    const store = L.createDraftStore(() => s);
    store.save('u1', 'i', 1, DRAFT);
    assert.equal(store.load('u2', 'i', 1), null);
    assert.equal(store.load('u1', 'other', 1), null);
    assert.equal(store.load('u1', 'i', 2), null);
    assert.deepEqual(store.load('u1', 'i', 1), DRAFT);
});

test('saving a draft with no rows removes the entry', () => {
    const s = memoryStorage();
    const store = L.createDraftStore(() => s);
    store.save('u', 'i', 1, DRAFT);
    assert.equal(s.map.size, 1);
    assert.equal(store.save('u', 'i', 1, { v: 1, rows: [] }), true);
    assert.equal(s.map.size, 0);
    assert.equal(store.load('u', 'i', 1), null);
});

test('remove deletes the entry', () => {
    const s = memoryStorage();
    const store = L.createDraftStore(() => s);
    store.save('u', 'i', 1, DRAFT);
    assert.equal(store.remove('u', 'i', 1), true);
    assert.equal(store.load('u', 'i', 1), null);
});

test('load of a missing entry is null', () => {
    assert.equal(L.createDraftStore(() => memoryStorage()).load('u', 'i', 1), null);
});

test('a damaged entry loads as null and is removed', () => {
    const s = memoryStorage();
    s.setItem(L.draftKey('u', 'i', 1), '{not json');
    const store = L.createDraftStore(() => s);
    assert.equal(store.load('u', 'i', 1), null);
    assert.equal(s.map.size, 0);
});

test('an entry of another version or shape loads as null', () => {
    const s = memoryStorage();
    const store = L.createDraftStore(() => s);
    s.setItem(L.draftKey('u', 'i', 1), JSON.stringify({ v: 2, rows: [] }));
    assert.equal(store.load('u', 'i', 1), null);
    s.setItem(L.draftKey('u', 'i', 1), JSON.stringify({ v: 1, rows: 'x' }));
    assert.equal(store.load('u', 'i', 1), null);
    s.setItem(L.draftKey('u', 'i', 1), 'null');
    assert.equal(store.load('u', 'i', 1), null);
});

test('nothing throws when the storage throws', () => {
    const boom = () => { throw new Error('SecurityError'); };
    const store = L.createDraftStore(() => ({ getItem: boom, setItem: boom, removeItem: boom }));
    assert.equal(store.save('u', 'i', 1, DRAFT), false);
    assert.equal(store.save('u', 'i', 1, { v: 1, rows: [] }), false);
    assert.equal(store.load('u', 'i', 1), null);
    assert.equal(store.remove('u', 'i', 1), false);
});

test('a full storage (quota error on setItem) reports false and keeps working', () => {
    const s = memoryStorage();
    s.setItem = () => { throw Object.assign(new Error('full'), { name: 'QuotaExceededError' }); };
    const store = L.createDraftStore(() => s);
    assert.equal(store.save('u', 'i', 1, DRAFT), false);
    assert.equal(store.load('u', 'i', 1), null);
});

test('a damaged entry that also cannot be removed still loads as null', () => {
    const s = memoryStorage();
    s.setItem(L.draftKey('u', 'i', 1), 'oops');
    s.removeItem = () => { throw new Error('read only'); };
    assert.equal(L.createDraftStore(() => s).load('u', 'i', 1), null);
});

test('nothing throws when the storage cannot be reached at all', () => {
    const blocked = L.createDraftStore(() => { throw new Error('access denied'); });
    assert.equal(blocked.save('u', 'i', 1, DRAFT), false);
    assert.equal(blocked.load('u', 'i', 1), null);
    assert.equal(blocked.remove('u', 'i', 1), false);
    const none = L.createDraftStore(() => undefined);
    assert.equal(none.save('u', 'i', 1, DRAFT), false);
    assert.equal(none.load('u', 'i', 1), null);
    const odd = L.createDraftStore(() => ({}));
    assert.equal(odd.save('u', 'i', 1, DRAFT), false);
});

test('a model draft survives the store and a restore', () => {
    const cues = [{ index: 1, start: 0, end: 2000, text: 'One' }, { index: 2, start: 2000, end: 4000, text: 'Two' }];
    const m = new L.StagingModel();
    m.setCues(cues, 'h1', 60000);
    m.stageEdit(cues[1], 'Two fixed');
    m.stageAdd({ startMs: 5000, endMs: 5500, text: 'Added' });
    const s = memoryStorage();
    const store = L.createDraftStore(() => s);
    assert.equal(store.save('u', 'i', 1, m.serialize()), true);
    const back = new L.StagingModel({ runtimeMs: 60000 });
    back.restore(store.load('u', 'i', 1), cues, 'h1');
    assert.deepEqual(back.snapshot().map((r) => [r.rowId, r.status]), [['e1', 'staged'], ['n1', 'staged']]);
});
