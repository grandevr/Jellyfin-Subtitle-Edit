'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

const cue = (index, start, end, text) => ({ index: index, start: start, end: end, text: text === undefined ? 'cue ' + index : text });
const FILE = [cue(1, 0, 2000, 'One'), cue(2, 2000, 4000, 'Two'), cue(3, 4000, 6000, 'Three'), cue(4, 6000, 8000, 'Four')];

function model(options) {
    const m = new L.StagingModel(options);
    m.setCues(FILE, 'hash-1', 60000);
    return m;
}

// ---- edits ----------------------------------------------------------------------------------------------------

test('stageEdit adds one row with the file text as oldText', () => {
    const m = model();
    const r = m.stageEdit(FILE[1], 'Two fixed');
    assert.equal(r.ok, true);
    assert.deepEqual(m.snapshot(), [{
        rowId: 'e1', type: 'edit', cueIndex: 2, start: 2000, end: 4000, oldText: 'Two', newText: 'Two fixed', status: 'staged', message: null,
    }]);
});

test('editing the same cue twice updates its row instead of adding one', () => {
    const m = model();
    m.stageEdit(FILE[1], 'first try');
    const second = m.stageEdit(FILE[1], 'second try');
    const rows = m.snapshot();
    assert.equal(rows.length, 1);
    assert.equal(rows[0].newText, 'second try');
    assert.equal(rows[0].oldText, 'Two');
    assert.equal(second.row.rowId, rows[0].rowId);
});

test('editing a different cue adds a second row', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[2], 'b');
    assert.equal(m.snapshot().length, 2);
});

test('setting the text back to the original removes the row', () => {
    const m = model();
    m.stageEdit(FILE[1], 'changed');
    const r = m.stageEdit(FILE[1], 'Two');
    assert.deepEqual(r, { ok: true, row: null, removed: true });
    assert.deepEqual(m.snapshot(), []);
});

test('staging the unchanged text of an untouched cue stages nothing', () => {
    const m = model();
    assert.deepEqual(m.stageEdit(FILE[1], 'Two'), { ok: true, row: null, removed: false });
    assert.deepEqual(m.snapshot(), []);
});

test('stageEdit refuses text the server would refuse, and stages nothing', () => {
    const m = model();
    ['', '   ', 'a\n\nb', '00:00:01,000 --> 00:00:02,000', 'bell\u0007'].forEach((text) => {
        const r = m.stageEdit(FILE[1], text);
        assert.equal(r.ok, false);
        assert.ok(r.error.message);
    });
    assert.deepEqual(m.snapshot(), []);
});

test('stageEdit stores line breaks as LF and compares with the file ignoring CRLF', () => {
    const m = model();
    m.stageEdit(cue(2, 2000, 4000, 'a\r\nb'), 'a\r\nc');
    assert.equal(m.snapshot()[0].oldText, 'a\nb');
    assert.equal(m.snapshot()[0].newText, 'a\nc');
    assert.deepEqual(m.stageEdit(cue(2, 2000, 4000, 'a\r\nb'), 'a\nb'), { ok: true, row: null, removed: true });
});

test('a failed row goes back to staged when the cue is edited again', () => {
    const m = model();
    m.stageEdit(FILE[1], 'x');
    m.rows[0].status = 'failed';
    m.rows[0].message = 'nope';
    m.stageEdit(FILE[1], 'y');
    assert.equal(m.snapshot()[0].status, 'staged');
    assert.equal(m.snapshot()[0].message, null);
    assert.equal(m.snapshot().length, 1);
});

test('editing a cue again after it was saved adds a new row and keeps the saved one', () => {
    const m = model();
    m.stageEdit(FILE[1], 'saved text');
    const prepared = m.prepareSave();
    m.applySaveSuccess(prepared.rowIds, { hash: 'hash-2', addedCues: {} });
    m.setCues([FILE[0], cue(2, 2000, 4000, 'saved text'), FILE[2], FILE[3]], 'hash-2');
    m.stageEdit(cue(2, 2000, 4000, 'saved text'), 'saved text, again');
    const rows = m.snapshot();
    assert.equal(rows.length, 2);
    assert.equal(rows[0].status, 'saved');
    assert.equal(rows[1].status, 'staged');
    assert.equal(rows[1].oldText, 'saved text');
});

// ---- undo and status ------------------------------------------------------------------------------------------

test('undo drops a staged row and refuses a saved one', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[1], 'b');
    assert.equal(m.undo('e1'), true);
    assert.deepEqual(m.snapshot().map((r) => r.rowId), ['e2']);
    const p = m.prepareSave();
    m.applySaveSuccess(p.rowIds, { hash: 'h2', addedCues: {} });
    assert.equal(m.undo('e2'), false);
    assert.equal(m.snapshot().length, 1);
    assert.equal(m.undo('nope'), false);
});

test('undo also drops a failed row', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.rows[0].status = 'failed';
    assert.equal(m.undo('e1'), true);
});

test('hasUnsaved is true for staged and failed rows only', () => {
    const m = model();
    assert.equal(m.hasUnsaved(), false);
    m.stageEdit(FILE[0], 'a');
    assert.equal(m.hasUnsaved(), true);
    m.rows[0].status = 'failed';
    assert.equal(m.hasUnsaved(), true);
    m.rows[0].status = 'saved';
    assert.equal(m.hasUnsaved(), false);
});

test('sortedRows lists rows in time order, edits before new lines at the same time', () => {
    const m = model({ newClientId: (n) => 'x' + n });
    m.stageEdit(FILE[2], 'late');
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'early add' });
    m.stageEdit(FILE[0], 'first');
    m.stageAdd({ startMs: 0, endMs: 500, text: 'at zero' });
    assert.deepEqual(m.sortedRows().map((r) => r.rowId), ['e2', 'x2', 'x1', 'e1']);
});

// ---- new lines ------------------------------------------------------------------------------------------------

test('stageAdd stages a valid line with a unique client id', () => {
    const m = model();
    const a = m.stageAdd({ startMs: 1000, endMs: 2500, text: 'Hello' });
    const b = m.stageAdd({ startMs: 3000, endMs: 3500, text: 'World' });
    assert.equal(a.ok, true);
    assert.equal(a.row.clientId, 'n1');
    assert.equal(b.row.clientId, 'n2');
    assert.deepEqual(m.snapshot()[0], {
        rowId: 'n1', type: 'add', clientId: 'n1', position: null, startMs: 1000, endMs: 2500, text: 'Hello', status: 'staged', message: null,
    });
});

test('stageAdd runs the client-side checks and stages nothing when they fail', () => {
    const m = model();
    const cases = [
        { startMs: 5000, endMs: 4000, text: 'x' },
        { startMs: 5000, endMs: 5050, text: 'x' },
        { startMs: 59000, endMs: 61000, text: 'x' },
        { startMs: 1000, endMs: 2000, text: '' },
        { startMs: 1000, endMs: 2000, text: 'a\n\nb' },
        { startMs: -5, endMs: 2000, text: 'x' },
    ];
    cases.forEach((c) => {
        const r = m.stageAdd(c);
        assert.equal(r.ok, false);
        assert.ok(r.errors.length > 0);
    });
    assert.deepEqual(m.snapshot(), []);
});

test('the runtime check uses the runtime given to setCues', () => {
    const m = new L.StagingModel();
    m.setCues(FILE, 'h', 10000);
    assert.equal(m.stageAdd({ startMs: 9000, endMs: 10001, text: 'x' }).ok, false);
    assert.equal(m.stageAdd({ startMs: 9000, endMs: 10000, text: 'x' }).ok, true);
});

test('updateAdd changes a staged line and re-checks it', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 2000, text: 'old' });
    const ok = m.updateAdd('n1', { startMs: 1500, endMs: 2600, text: 'new' });
    assert.equal(ok.ok, true);
    assert.deepEqual([m.snapshot()[0].startMs, m.snapshot()[0].endMs, m.snapshot()[0].text], [1500, 2600, 'new']);
    const bad = m.updateAdd('n1', { startMs: 3000, endMs: 2000, text: 'new' });
    assert.equal(bad.ok, false);
    assert.equal(m.snapshot()[0].startMs, 1500);
    assert.equal(m.updateAdd('missing', { startMs: 1, endMs: 1000, text: 'x' }).ok, false);
});

test('updateAdd turns a failed new line back into staged', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 2000, text: 'x' });
    m.rows[0].status = 'failed';
    m.rows[0].message = 'bad';
    m.updateAdd('n1', { startMs: 1000, endMs: 2000, text: 'fixed' });
    assert.equal(m.snapshot()[0].status, 'staged');
    assert.equal(m.snapshot()[0].message, null);
});

test('stagedOverlaps finds staged new lines that overlap a span', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 2000, text: 'a' });
    m.stageAdd({ startMs: 5000, endMs: 6000, text: 'b' });
    assert.deepEqual(m.stagedOverlaps(1500, 2500).map((r) => r.clientId), ['n1']);
    assert.deepEqual(m.stagedOverlaps(2000, 5000), []);
    assert.deepEqual(m.stagedOverlaps(1500, 2500, 'n1'), []);
});

// ---- the save request -----------------------------------------------------------------------------------------

test('prepareSave is null when nothing is staged', () => {
    assert.equal(model().prepareSave(), null);
});

test('prepareSave builds the Edits body: edits by cue number, then new lines', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three fixed');
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'added' });
    m.stageEdit(FILE[0], 'One fixed');
    const p = m.prepareSave();
    assert.deepEqual(p.request, {
        baseHash: 'hash-1',
        edits: [
            { type: 'edit', cueIndex: 1, oldText: 'One', newText: 'One fixed' },
            { type: 'edit', cueIndex: 3, oldText: 'Three', newText: 'Three fixed' },
            { type: 'add', clientId: 'n1', startMs: 1000, endMs: 1500, text: 'added' },
        ],
    });
    assert.deepEqual(p.rowIds.sort(), ['e1', 'e2', 'n1']);
});

test('prepareSave leaves out failed and saved rows', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[1], 'b');
    m.stageEdit(FILE[2], 'c');
    m.rows[0].status = 'failed';
    m.rows[1].status = 'saved';
    const p = m.prepareSave();
    assert.deepEqual(p.rowIds, ['e3']);
    assert.equal(p.request.edits.length, 1);
});

test('prepareSave refuses to build a request without a base hash', () => {
    const m = new L.StagingModel();
    m.stageEdit(FILE[0], 'a');
    assert.throws(() => m.prepareSave(), /hash/);
});

// ---- after a successful save ---------------------------------------------------------------------------------

test('applySaveSuccess marks the sent rows saved and takes the new hash', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    m.stageEdit(FILE[1], 'b');
    const p = m.prepareSave();
    m.stageEdit(FILE[3], 'staged after the request was built');
    m.applySaveSuccess(p.rowIds, { hash: 'hash-2', savedCueIndexes: [1, 2], addedCues: {}, writeMethod: 'AtomicRename' });
    assert.deepEqual(m.snapshot().map((r) => r.status), ['saved', 'saved', 'staged']);
    assert.equal(m.baseHash, 'hash-2');
});

test('applySaveSuccess gives each new line its cue number from the clientId mapping', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'a' });
    m.stageAdd({ startMs: 5000, endMs: 5500, text: 'b' });
    const p = m.prepareSave();
    m.applySaveSuccess(p.rowIds, { hash: 'h2', addedCues: { n1: 2, n2: 5 } });
    assert.deepEqual(m.snapshot().map((r) => [r.clientId, r.position, r.status]), [['n1', 2, 'saved'], ['n2', 5, 'saved']]);
});

test('applySaveSuccess renumbers edit rows for the lines inserted before them', () => {
    const m = model();
    m.stageEdit(FILE[2], 'Three fixed'); // was cue 3
    m.stageEdit(FILE[3], 'Four fixed'); // was cue 4
    m.stageAdd({ startMs: 3000, endMs: 3500, text: 'a' });
    m.stageAdd({ startMs: 7000, endMs: 7500, text: 'b' });
    const p = m.prepareSave();
    // new file in time order: 1 One, 2 Two, 3 a (new), 4 Three, 5 Four, 6 b (new)
    m.applySaveSuccess(p.rowIds, { hash: 'h2', addedCues: { n1: 3, n2: 6 } });
    const edits = m.snapshot().filter((r) => r.type === 'edit');
    assert.deepEqual(edits.map((r) => r.cueIndex), [4, 5]);
});

test('applySaveSuccess without added lines changes no cue numbers', () => {
    const m = model();
    m.stageEdit(FILE[2], 'x');
    const p = m.prepareSave();
    m.applySaveSuccess(p.rowIds, { hash: 'h2', addedCues: {} });
    assert.equal(m.snapshot()[0].cueIndex, 3);
    m.applySaveSuccess([], { hash: 'h3' });
    assert.equal(m.snapshot()[0].cueIndex, 3);
});

test('shiftPosition counts the inserted lines at or before the cue\'s new place', () => {
    assert.equal(L.shiftPosition(3, []), 3);
    assert.equal(L.shiftPosition(3, [3]), 4);
    assert.equal(L.shiftPosition(2, [3]), 2);
    assert.equal(L.shiftPosition(3, [3, 5]), 4);
    assert.equal(L.shiftPosition(4, [3, 5]), 6);
    assert.equal(L.shiftPosition(4, [5, 3]), 6);
    assert.equal(L.shiftPosition(1, [1, 2, 3]), 4);
    assert.equal(L.shiftPosition(10, [1]), 11);
});

// ---- refused saves --------------------------------------------------------------------------------------------

test('a 409 FileChanged or TextMismatch asks for a reconcile', () => {
    const m = model();
    m.stageEdit(FILE[0], 'a');
    const p = m.prepareSave();
    assert.deepEqual(m.applySaveError(p.rowIds, 409, { code: 'FileChanged', message: 'x', currentHash: 'newhash' }),
        { action: 'reconcile', code: 'FileChanged', currentHash: 'newhash', cueIndex: null });
    assert.deepEqual(m.applySaveError(p.rowIds, 409, { code: 'TextMismatch', message: 'x', cueIndex: 1 }),
        { action: 'reconcile', code: 'TextMismatch', currentHash: null, cueIndex: 1 });
    assert.equal(m.snapshot()[0].status, 'staged');
});

test('an error naming a clientId marks that new line failed and leaves the others staged', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'a' });
    m.stageAdd({ startMs: 3000, endMs: 3500, text: 'b' });
    m.stageEdit(FILE[0], 'edit');
    const p = m.prepareSave();
    const r = m.applySaveError(p.rowIds, 400, { code: 'InvalidEdit', message: 'A new line must end after it starts.', clientId: 'n2', cueIndex: null });
    assert.deepEqual(r, { action: 'marked', rowId: 'n2' });
    const byId = Object.fromEntries(m.snapshot().map((x) => [x.rowId, x]));
    assert.equal(byId.n2.status, 'failed');
    assert.equal(byId.n2.message, 'A new line must end after it starts.');
    assert.equal(byId.n1.status, 'staged');
    assert.equal(byId.e1.status, 'staged');
});

test('an error naming a cueIndex marks that edit failed', () => {
    const m = model();
    m.stageEdit(FILE[1], 'x');
    m.stageEdit(FILE[2], 'y');
    const p = m.prepareSave();
    const r = m.applySaveError(p.rowIds, 400, { code: 'CueNotFound', message: 'Cue 3 does not exist.', cueIndex: 3 });
    assert.deepEqual(r, { action: 'marked', rowId: 'e2' });
    assert.deepEqual(m.snapshot().map((x) => x.status), ['staged', 'failed']);
});

test('the clientId wins over the cueIndex when an error has both', () => {
    const m = model();
    m.stageEdit(FILE[0], 'x');
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'a' });
    const p = m.prepareSave();
    const r = m.applySaveError(p.rowIds, 400, { code: 'InvalidEdit', message: 'm', clientId: 'n1', cueIndex: 1 });
    assert.equal(r.rowId, 'n1');
});

test('an error that names no row of this save is shown, and every row stays staged', () => {
    const m = model();
    m.stageEdit(FILE[0], 'x');
    const p = m.prepareSave();
    [
        [500, { code: 'NotWritable', message: 'The folder is not writable.' }],
        [413, { code: 'RequestTooLarge', message: 'Too big.' }],
        [400, { code: 'InvalidEdit', message: 'There are no edits to save.' }],
        [400, { code: 'InvalidEdit', message: 'm', clientId: 'unknown' }],
        [400, { code: 'InvalidEdit', message: 'm', cueIndex: 99 }],
        [403, null],
    ].forEach(([status, body]) => {
        const r = m.applySaveError(p.rowIds, status, body);
        assert.equal(r.action, 'show-error');
        assert.ok(r.message);
    });
    assert.equal(m.snapshot()[0].status, 'staged');
});

test('an error only marks rows that were part of the save', () => {
    const m = model();
    m.stageAdd({ startMs: 1000, endMs: 1500, text: 'a' });
    const p = m.prepareSave();
    m.stageAdd({ startMs: 3000, endMs: 3500, text: 'b' });
    const r = m.applySaveError(p.rowIds, 400, { code: 'InvalidEdit', message: 'm', clientId: 'n2' });
    assert.equal(r.action, 'show-error');
    assert.equal(m.snapshot()[1].status, 'staged');
});

test('dropSaved forgets saved rows and keeps staged and failed ones', () => {
    const m = new L.StagingModel();
    m.setCues([{ index: 1, start: 0, end: 1000, text: 'a' }, { index: 2, start: 2000, end: 3000, text: 'b' }, { index: 3, start: 4000, end: 5000, text: 'c' }], 'h1');
    m.stageEdit(m.cues[0], 'A');
    const save = m.prepareSave();
    m.applySaveSuccess(save.rowIds, { hash: 'h2', savedCueIndexes: [1], addedCues: {} });
    m.stageEdit(m.cues[1], 'B');
    m.stageEdit(m.cues[2], 'C');
    m.applySaveError([m.rows[2].rowId], 400, { code: 'InvalidEdit', message: 'no', cueIndex: 3 });
    assert.equal(m.dropSaved(), 1);
    assert.deepEqual(m.rows.map((r) => r.status), ['staged', 'failed']);
    assert.equal(m.dropSaved(), 0);
});
