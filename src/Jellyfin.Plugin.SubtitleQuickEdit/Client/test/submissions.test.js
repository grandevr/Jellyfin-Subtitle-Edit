'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

test('editorMode: edit when the user may save directly, even if submissions are on too', () => {
    assert.equal(L.editorMode({ isAdmin: true, canEdit: true, canSubmit: false }), 'edit');
    assert.equal(L.editorMode({ isAdmin: false, canEdit: true, canSubmit: true }), 'edit');
});

test('editorMode: submit for a user who may only submit', () => {
    assert.equal(L.editorMode({ isAdmin: false, canEdit: false, canSubmit: true }), 'submit');
});

test('editorMode: no editor without a right, a missing field or a missing answer', () => {
    assert.equal(L.editorMode({ isAdmin: false, canEdit: false, canSubmit: false }), null);
    assert.equal(L.editorMode({ isAdmin: true }), null);
    assert.equal(L.editorMode({ canEdit: 'true' }), null);
    assert.equal(L.editorMode({ canSubmit: 1 }), null);
    assert.equal(L.editorMode(null), null);
    assert.equal(L.editorMode(undefined), null);
});

test('submissionStateLabel names every state, and passes unknown ones through', () => {
    assert.equal(L.submissionStateLabel('Pending'), 'waiting for review');
    assert.equal(L.submissionStateLabel('Approved'), 'approved');
    assert.equal(L.submissionStateLabel('PartlyApproved'), 'partly approved');
    assert.equal(L.submissionStateLabel('Rejected'), 'rejected');
    assert.equal(L.submissionStateLabel('Withdrawn'), 'withdrawn');
    assert.equal(L.submissionStateLabel('Archived'), 'archived');
    assert.equal(L.submissionStateLabel(undefined), '');
});

test('submissionsFor keeps one subtitle\'s submissions, newest first, whatever the id spelling', () => {
    const list = [
        { id: 'a', itemId: 'abcd1234', streamIndex: 2, created: '2026-10-25T01:00:00Z' },
        { id: 'b', itemId: 'abcd1234', streamIndex: 3, created: '2026-10-25T02:00:00Z' },
        { id: 'c', itemId: 'ABCD1234', streamIndex: 2, created: '2026-10-25T03:00:00Z' },
        { id: 'd', itemId: 'other', streamIndex: 2, created: '2026-10-25T04:00:00Z' },
        null,
    ];
    assert.deepEqual(L.submissionsFor(list, 'abcd-1234', 2).map((s) => s.id), ['c', 'a']);
    assert.deepEqual(L.submissionsFor(null, 'abcd1234', 2), []);
    assert.deepEqual(L.submissionsFor(list, 'abcd1234', '2'), []);
});

test('applySubmitSuccess removes only the rows that were sent', () => {
    const m = new L.StagingModel();
    m.setCues([{ index: 1, start: 0, end: 1000, text: 'One' }, { index: 2, start: 1000, end: 2000, text: 'Two' }], 'h', 60000);
    const a = m.stageEdit({ index: 1, start: 0, end: 1000, text: 'One' }, 'One fixed').row;
    const sent = m.prepareSave();
    m.stageEdit({ index: 2, start: 1000, end: 2000, text: 'Two' }, 'Two fixed');
    assert.equal(m.applySubmitSuccess(sent.rowIds), 1);
    const rows = m.snapshot();
    assert.equal(rows.length, 1);
    assert.equal(rows[0].cueIndex, 2);
    assert.notEqual(rows[0].rowId, a && a.rowId);
    assert.equal(m.baseHash, 'h');
});
