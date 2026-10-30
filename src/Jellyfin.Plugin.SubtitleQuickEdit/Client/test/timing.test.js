'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

const cue = (index, start, end, text) => ({ index: index, start: start, end: end, text: text });
const FILE = [cue(1, 0, 2000, 'One'), cue(2, 2000, 4000, 'Two'), cue(3, 4000, 1_500_000, 'Three')];

function model() {
    const m = new L.StagingModel();
    m.setCues(FILE, 'hash-1', 60000);
    return m;
}

test('validateTimes names the line in its messages and checks the runtime', () => {
    assert.deepEqual(L.validateTimes(5000, 4000, null, 'A line'), [{ field: 'end', message: 'A line must end after it starts.' }]);
    assert.deepEqual(L.validateTimes(5000, 70000, 60000, 'A line'), [{ field: 'end', message: 'A line must not end after the end of the video.' }]);
    assert.deepEqual(L.validateTimes(null, 1000, null, 'A line').map((e) => e.field), ['start']);
    assert.deepEqual(L.validateTimes(1000, 1050, null, 'A line'), [{ field: 'end', message: 'A line must be at least 100 ms long.' }]);
    assert.deepEqual(L.validateTimes(1000, 2000, 60000, 'A line'), []);
});

test('new times stage an edit row with the text unchanged, and the save sends them', () => {
    const m = model();
    const r = m.stageEdit(FILE[2], 'Three', { startMs: 4000.4, endMs: 5200 });
    assert.equal(r.ok, true);
    const row = m.snapshot()[0];
    assert.equal(L.timesChanged(row), true);
    assert.deepEqual([row.start, row.end, row.newStart, row.newEnd, row.newText], [4000, 1_500_000, 4000, 5200, 'Three']);
    assert.deepEqual(m.prepareSave().request.edits, [{ type: 'edit', cueIndex: 3, oldText: 'Three', newText: 'Three', startMs: 4000, endMs: 5200 }]);
});

test('a text-only edit sends no times, and keeps times staged earlier', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two fixed');
    assert.deepEqual(m.prepareSave().request.edits, [{ type: 'edit', cueIndex: 2, oldText: 'Two', newText: 'Two fixed' }]);
    m.stageEdit(FILE[1], 'Two fixed', { startMs: 2100, endMs: 3900 });
    m.stageEdit(FILE[1], 'Two fixed again');
    const row = m.snapshot()[0];
    assert.deepEqual([row.newText, row.newStart, row.newEnd], ['Two fixed again', 2100, 3900]);
    assert.equal(m.snapshot().length, 1);
});

test('times set back to the file times and the original text drop the row', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two', { startMs: 2100, endMs: 3900 });
    assert.equal(m.snapshot().length, 1);
    const back = m.stageEdit(FILE[1], 'Two', { startMs: 2000, endMs: 4000 });
    assert.equal(back.removed, true);
    assert.equal(m.snapshot().length, 0);
});

test('bad new times are refused with the field to mark, and nothing is staged', () => {
    const m = model();
    const r = m.stageEdit(FILE[2], 'Three', { startMs: 4000, endMs: 3000 });
    assert.equal(r.ok, false);
    assert.deepEqual(r.errors, [{ field: 'end', message: 'A line must end after it starts.' }]);
    assert.equal(r.error.message, 'A line must end after it starts.');
    const late = m.stageEdit(FILE[2], 'Three', { startMs: 4000, endMs: 61000 });
    assert.equal(late.errors[0].message, 'A line must not end after the end of the video.');
    assert.equal(m.snapshot().length, 0);
});

test('undoSaved puts the old times back, and refuses when the times changed since', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two', { startMs: 2000, endMs: 3200 });
    const prepared = m.prepareSave();
    m.applySaveSuccess(prepared.rowIds, { hash: 'hash-2', addedCues: {} });
    m.setCues([FILE[0], cue(2, 2000, 3200, 'Two'), FILE[2]], 'hash-2');
    const undo = m.undoSaved(prepared.rowIds[0]);
    assert.equal(undo.ok, true);
    const staged = m.snapshot().filter((r) => r.status === 'staged')[0];
    assert.deepEqual([staged.start, staged.end, staged.newStart, staged.newEnd], [2000, 3200, 2000, 4000]);

    const other = model();
    other.stageEdit(FILE[1], 'Two', { startMs: 2000, endMs: 3200 });
    const sent = other.prepareSave();
    other.applySaveSuccess(sent.rowIds, { hash: 'hash-2', addedCues: {} });
    other.setCues([FILE[0], cue(2, 2000, 3900, 'Two'), FILE[2]], 'hash-3');
    assert.equal(other.undoSaved(sent.rowIds[0]).ok, false);
});

test('a staged retime fails on reconcile when the line got other times on disk, a text edit does not', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two fixed');
    m.stageEdit(FILE[2], 'Three', { startMs: 4000, endMs: 5200 });
    const summary = m.reconcile([FILE[0], cue(2, 2000, 4100, 'Two'), cue(3, 4000, 9000, 'Three')], 'hash-2');
    assert.deepEqual(summary.kept, ['e1']);
    assert.deepEqual(summary.failed, ['e2']);
});

test('a draft with new times comes back staged when the line still has its times', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three', { startMs: 4000, endMs: 5200 });
    const draft = m.serialize();
    const back = new L.StagingModel();
    const summary = back.restore(draft, FILE, 'hash-1');
    assert.deepEqual(summary.restored, ['e1']);
    assert.deepEqual(back.prepareSave().request.edits[0], { type: 'edit', cueIndex: 3, oldText: 'Three', newText: 'Three', startMs: 4000, endMs: 5200 });
    const moved = new L.StagingModel();
    assert.deepEqual(moved.restore(draft, [FILE[0], FILE[1], cue(3, 4000, 6000, 'Three')], 'hash-2').failed, ['e1']);
});
