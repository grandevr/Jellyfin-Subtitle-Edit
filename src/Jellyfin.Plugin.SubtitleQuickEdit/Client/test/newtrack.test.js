'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

// ---- who gets the edit button -------------------------------------------------------------------------------------

test('users who save directly always get the button, with or without a subtitle on', () => {
    assert.equal(L.showsEditButton('edit', false), true);
    assert.equal(L.showsEditButton('edit', true), true);
});

test('users who submit for review get it only on a subtitle they can work on, and no editor means no button', () => {
    assert.equal(L.showsEditButton('submit', false), false);
    assert.equal(L.showsEditButton('submit', true), true);
    assert.equal(L.showsEditButton(null, true), false);
    assert.equal(L.showsEditButton(null, false), false);
});

// ---- a new track's first save ----------------------------------------------------------------------------------

function emptyModel() {
    const m = new L.StagingModel({ runtimeMs: 60000 });
    m.setCues([], null);
    return m;
}

test('nothing staged means nothing to send', () => {
    assert.equal(emptyModel().prepareNewTrack('en'), null);
});

test('the request carries the language and every staged new line, without a hash', () => {
    const m = emptyModel();
    const a = m.stageAdd({ startMs: 5000, endMs: 6000, text: 'Later' });
    const b = m.stageAdd({ startMs: 1000, endMs: 2000, text: 'Sooner' });
    assert.equal(a.ok && b.ok, true);
    const prepared = m.prepareNewTrack('en');
    assert.deepEqual(prepared.request, {
        language: 'en',
        edits: [
            { type: 'add', clientId: a.row.clientId, startMs: 5000, endMs: 6000, text: 'Later' },
            { type: 'add', clientId: b.row.clientId, startMs: 1000, endMs: 2000, text: 'Sooner' },
        ],
    });
    assert.deepEqual(prepared.rowIds, [a.row.rowId, b.row.rowId]);
    assert.equal(prepared.request.baseHash, undefined);
});

test('no language is sent as null; failed lines stay behind', () => {
    const m = emptyModel();
    const a = m.stageAdd({ startMs: 1000, endMs: 2000, text: 'Kept' });
    const b = m.stageAdd({ startMs: 3000, endMs: 4000, text: 'Refused' });
    m.applySaveError([a.row.rowId, b.row.rowId], 400, { code: 'InvalidEdit', clientId: b.row.clientId, message: 'no' });
    const prepared = m.prepareNewTrack('');
    assert.equal(prepared.request.language, null);
    assert.deepEqual(prepared.request.edits.map((e) => e.text), ['Kept']);
});

test('a made track numbers its lines from the answer', () => {
    const m = emptyModel();
    const a = m.stageAdd({ startMs: 5000, endMs: 6000, text: 'Later' });
    const b = m.stageAdd({ startMs: 1000, endMs: 2000, text: 'Sooner' });
    const prepared = m.prepareNewTrack('en');
    const mapping = {};
    mapping[a.row.clientId] = 2;
    mapping[b.row.clientId] = 1;
    m.applySaveSuccess(prepared.rowIds, { hash: 'h', addedCues: mapping });
    assert.deepEqual(m.snapshot().map((r) => [r.status, r.position]), [['saved', 2], ['saved', 1]]);
    assert.equal(m.hasUnsaved(), false);
    assert.equal(m.prepareNewTrack('en'), null);
});
