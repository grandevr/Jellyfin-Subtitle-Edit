'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

const cue = (index, start, end, text) => ({ index: index, start: start, end: end, text: text === undefined ? 'cue ' + index : text });
const FILE = [cue(1, 0, 2000, 'One'), cue(2, 2000, 4000, 'Two'), cue(3, 4000, 6000, 'Three'), cue(4, 6000, 8000, 'Four')];

function model() {
    const m = new L.StagingModel();
    m.setCues(FILE, 'hash-1', 60000);
    return m;
}

const statusOf = (m) => Object.fromEntries(m.snapshot().map((r) => [r.rowId, r.status]));

// ---- 409: keep what still matches, fail the rest, always keep new lines ----------------------------------------

test('reconcile keeps a staged edit whose oldText still matches its cue', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two fixed');
    const s = m.reconcile(FILE, 'hash-2');
    assert.deepEqual(s, { kept: ['e1'], failed: [], relocated: [], newLines: [] });
    assert.equal(m.snapshot()[0].status, 'staged');
    assert.equal(m.baseHash, 'hash-2');
});

test('reconcile marks an edit failed when its cue text changed, with a reason', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two fixed');
    const s = m.reconcile([FILE[0], cue(2, 2000, 4000, 'Two, changed on disk'), FILE[2], FILE[3]], 'hash-2');
    assert.deepEqual(s.failed, ['e1']);
    assert.deepEqual(s.kept, []);
    assert.equal(m.snapshot()[0].status, 'failed');
    assert.match(m.snapshot()[0].message, /changed on disk/);
});

test('reconcile sorts edits into kept and failed one by one', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[1], 'b');
    m.stageEdit(FILE[2], 'c');
    const fresh = [FILE[0], cue(2, 2000, 4000, 'Two!'), FILE[2], FILE[3]];
    const s = m.reconcile(fresh, 'h2');
    assert.deepEqual(s.kept, ['e1', 'e3']);
    assert.deepEqual(s.failed, ['e2']);
    assert.deepEqual(statusOf(m), { e1: 'staged', e2: 'failed', e3: 'staged' });
});

test('reconcile always keeps new lines, even when the cues around them changed or are gone', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'new one' });
    m.stageAdd({ startMs: 9000, endMs: 9500, text: 'new two' });
    const s = m.reconcile([], 'h2');
    assert.deepEqual(s.newLines, ['n1', 'n2']);
    assert.deepEqual(statusOf(m), { n1: 'staged', n2: 'staged' });
});

test('after reconcile, a save carries the kept edits and the new lines and not the failed ones', () => {
    const m = model();
    m.stageEdit(FILE[0], 'keep me');
    m.stageEdit(FILE[1], 'lose me');
    m.stageAdd({ startMs: 5000, endMs: 5500, text: 'added' });
    m.reconcile([FILE[0], cue(2, 2000, 4000, 'moved on'), FILE[2], FILE[3]], 'h2');
    const p = m.prepareSave();
    assert.equal(p.request.baseHash, 'h2');
    assert.deepEqual(p.request.edits.map((e) => e.type + ':' + (e.cueIndex || e.clientId)), ['edit:1', 'add:n1']);
});

test('reconcile leaves saved rows and already failed rows alone', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[1], 'b');
    m.stageEdit(FILE[2], 'c');
    m.rows[0].status = 'saved';
    m.rows[1].status = 'failed';
    m.rows[1].message = 'earlier problem';
    m.reconcile([], 'h2');
    assert.equal(m.rows[0].status, 'saved');
    assert.equal(m.rows[1].status, 'failed');
    assert.equal(m.rows[1].message, 'earlier problem');
    assert.equal(m.rows[2].status, 'failed');
});

test('reconcile ignores CRLF versus LF when comparing oldText', () => {
    const m = model();
    m.stageEdit(cue(2, 2000, 4000, 'a\nb'), 'a\nc');
    const s = m.reconcile([FILE[0], cue(2, 2000, 4000, 'a\r\nb'), FILE[2], FILE[3]], 'h2');
    assert.deepEqual(s.kept, ['e1']);
});

test('reconcile fails an edit whose cue number no longer exists', () => {
    const m = model();
    m.stageEdit(FILE[3], 'x');
    const s = m.reconcile([FILE[0], FILE[1]], 'h2');
    assert.deepEqual(s.failed, ['e1']);
});

test('reconcile finds an edit that moved when exactly one cue has the same times and oldText', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three fixed');
    const shifted = [cue(1, 0, 1000, 'inserted'), cue(2, 1000, 2000, 'One'), cue(3, 2000, 4000, 'Two'), cue(4, 4000, 6000, 'Three'), cue(5, 6000, 8000, 'Four')];
    const s = m.reconcile(shifted, 'h2');
    assert.deepEqual(s, { kept: ['e1'], failed: [], relocated: ['e1'], newLines: [] });
    assert.equal(m.snapshot()[0].cueIndex, 4);
    assert.equal(m.prepareSave().request.edits[0].cueIndex, 4);
});

test('reconcile does not guess when two cues match the moved edit', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three fixed');
    const twins = [cue(1, 4000, 6000, 'Three'), cue(2, 4000, 6000, 'Three'), cue(3, 4000, 6000, 'something else')];
    const s = m.reconcile(twins, 'h2');
    assert.deepEqual(s.failed, ['e1']);
    assert.deepEqual(s.relocated, []);
});

test('reconcile does not relocate to a cue with the same text at other times', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three fixed');
    const s = m.reconcile([cue(1, 0, 1000, 'Three')], 'h2');
    assert.deepEqual(s.failed, ['e1']);
});

test('reconcile does not relocate to a cue with the same start and text but another end', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three fixed');
    const s = m.reconcile([cue(1, 4000, 7000, 'Three')], 'h2');
    assert.deepEqual(s.failed, ['e1']);
    assert.deepEqual(s.relocated, []);
});

test('reconcile updates the times of a kept edit from the fresh cue', () => {
    const m = model();
    m.stageEdit(FILE[1], 'x');
    m.reconcile([FILE[0], cue(2, 2100, 4100, 'Two'), FILE[2], FILE[3]], 'h2');
    assert.deepEqual([m.snapshot()[0].start, m.snapshot()[0].end], [2100, 4100]);
});

test('a reconcile without a hash keeps the old base hash', () => {
    const m = model();
    m.reconcile(FILE);
    assert.equal(m.baseHash, 'hash-1');
});

test('reconcile after a 409: the whole round trip', () => {
    const m = model();
    m.stageEdit(FILE[1], 'Two fixed');
    m.stageAdd({ startMs: 8500, endMs: 9000, text: 'Added' });
    const first = m.prepareSave();
    const answer = m.applySaveError(first.rowIds, 409, { code: 'FileChanged', message: 'changed', currentHash: 'hash-2' });
    assert.equal(answer.action, 'reconcile');
    m.reconcile(FILE, answer.currentHash);
    const second = m.prepareSave();
    assert.equal(second.request.baseHash, 'hash-2');
    assert.equal(second.request.edits.length, 2);
    m.applySaveSuccess(second.rowIds, { hash: 'hash-3', addedCues: { n1: 5 } });
    assert.deepEqual(statusOf(m), { e1: 'saved', n1: 'saved' });
    assert.equal(m.snapshot()[1].position, 5);
});

// ---- renumbering and clientId marking after adds, together -----------------------------------------------------

test('after a save with adds, later edits use the new numbering and rows keep matching', () => {
    const m = model();
    m.stageEdit(FILE[3], 'Four fixed');
    m.stageAdd({ startMs: 100, endMs: 600, text: 'inserted early' });
    const p = m.prepareSave();
    m.applySaveSuccess(p.rowIds, { hash: 'h2', addedCues: { n1: 2 } });
    const fresh = [FILE[0], cue(2, 100, 600, 'inserted early'), FILE[1], FILE[2], cue(5, 6000, 8000, 'Four fixed')];
    m.setCues(fresh, 'h2');
    assert.equal(m.snapshot().find((r) => r.type === 'edit').cueIndex, 5);
    m.stageEdit(fresh[4], 'Four fixed again');
    assert.equal(m.snapshot().filter((r) => r.type === 'edit').length, 2);
    assert.equal(m.snapshot()[2].cueIndex, 5);
    assert.equal(m.snapshot()[2].oldText, 'Four fixed');
});

// ---- drafts: serialize and restore ------------------------------------------------------------------------------

test('serialize keeps what is not saved, as staged, and leaves saved rows out', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[1], 'b');
    m.stageAdd({ startMs: 5000, endMs: 5500, text: 'n' });
    m.rows[0].status = 'saved';
    m.rows[1].status = 'failed';
    m.rows[1].message = 'bad';
    const d = m.serialize();
    assert.equal(d.v, 1);
    assert.deepEqual(d.rows.map((r) => [r.rowId, r.status, r.message]), [['e2', 'staged', null], ['n1', 'staged', null]]);
});

test('restore brings back edits whose oldText still matches and fails the others', () => {
    const src = model();
    src.stageEdit(FILE[0], 'a');
    src.stageEdit(FILE[1], 'b');
    const draft = src.serialize();
    const fresh = model();
    const s = fresh.restore(draft, [FILE[0], cue(2, 2000, 4000, 'Two was changed'), FILE[2], FILE[3]], 'h9');
    assert.deepEqual(s, { restored: ['e1'], failed: ['e2'] });
    assert.deepEqual(statusOf(fresh), { e1: 'staged', e2: 'failed' });
    assert.match(fresh.snapshot()[1].message, /changed after your draft/);
    assert.equal(fresh.baseHash, 'h9');
});

test('restore brings back new lines, and fails one that no longer passes the checks', () => {
    const src = model();
    src.stageAdd({ startMs: 1000, endMs: 1500, text: 'fine' });
    src.stageAdd({ startMs: 58000, endMs: 59500, text: 'near the end' });
    const draft = src.serialize();
    const fresh = new L.StagingModel();
    const s = fresh.restore(draft, FILE, 'h2'); // no runtime known: both pass
    assert.deepEqual(s.restored, ['n1', 'n2']);
    const shortRuntime = new L.StagingModel({ runtimeMs: 59000 });
    const t = shortRuntime.restore(draft, FILE, 'h2');
    assert.deepEqual(t.restored, ['n1']);
    assert.deepEqual(t.failed, ['n2']);
    assert.match(shortRuntime.snapshot()[1].message, /end of the video/);
});

test('restore relocates a moved edit like reconcile does', () => {
    const src = model();
    src.stageEdit(FILE[2], 'x');
    const fresh = model();
    fresh.restore(src.serialize(), [cue(1, 0, 500, 'new'), cue(2, 0, 2000, 'One'), cue(3, 2000, 4000, 'Two'), cue(4, 4000, 6000, 'Three')], 'h2');
    assert.equal(fresh.snapshot()[0].status, 'staged');
    assert.equal(fresh.snapshot()[0].cueIndex, 4);
});

test('after a restore, new ids do not collide with restored ones', () => {
    const src = model();
    src.stageEdit(FILE[0], 'a');
    src.stageEdit(FILE[1], 'b');
    src.stageAdd({ startMs: 1000, endMs: 1500, text: 'x' });
    src.stageAdd({ startMs: 3000, endMs: 3500, text: 'y' });
    const fresh = model();
    fresh.restore(src.serialize(), FILE, 'h2');
    const next = fresh.stageAdd({ startMs: 5000, endMs: 5500, text: 'z' });
    assert.equal(next.row.clientId, 'n3');
    fresh.stageEdit(FILE[2], 'c');
    assert.deepEqual(fresh.snapshot().map((r) => r.rowId).sort(), ['e1', 'e2', 'e3', 'n1', 'n2', 'n3']);
});

test('restore ignores a draft that is missing, from another version, or malformed', () => {
    [null, undefined, {}, { v: 2, rows: [] }, { v: 1 }, { v: 1, rows: 'no' }].forEach((draft) => {
        const m = model();
        assert.deepEqual(m.restore(draft, FILE, 'h2'), { restored: [], failed: [] });
        assert.deepEqual(m.snapshot(), []);
    });
});

test('restore skips rows that are not edits or new lines', () => {
    const m = model();
    const s = m.restore({ v: 1, rows: [{ type: 'weird' }, { type: 'edit' }, { type: 'add' }] }, FILE, 'h2');
    assert.deepEqual(s, { restored: [], failed: [] });
    assert.deepEqual(m.snapshot(), []);
});
