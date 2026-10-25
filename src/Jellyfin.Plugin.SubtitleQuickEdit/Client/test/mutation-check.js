'use strict';
// Mutation-style check of the cue lookup and the reconcile / renumber code: each mutation breaks one rule in a scratch copy
// of logic.js, and the unit tests must fail for it. Usage (from the Client folder): node test/mutation-check.js
// Exit code 0 only if the control copy passes, every snippet is found exactly once, every broken copy is valid
// JavaScript, and every mutation is killed.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const logicPath = path.join(__dirname, '..', 'logic.js');
const source = fs.readFileSync(logicPath, 'utf8');
const testFiles = fs.readdirSync(__dirname).filter((f) => f.endsWith('.test.js')).map((f) => path.join(__dirname, f));

const MUTATIONS = [
    // ---- lookup ----
    { name: 'cuesAt: start becomes exclusive', from: 'return c.start <= t && t < c.end;', to: 'return c.start < t && t < c.end;' },
    { name: 'cuesAt: end becomes inclusive', from: 'return c.start <= t && t < c.end;', to: 'return c.start <= t && t <= c.end;' },
    { name: 'compareCues: no tie-break by position', from: 'return (a.start - b.start) || (a.index - b.index);', to: 'return (a.start - b.start);' },
    { name: 'nearestPrevious: a cue starting after t may be chosen', from: 'if (c.start > t || c.end > t) {', to: 'if (c.end > t) {' },
    { name: 'nearestPrevious: smallest end instead of largest', from: 'if (best === null || c.end > best.end ||', to: 'if (best === null || c.end < best.end ||' },
    { name: 'nearestPrevious: tie goes to the earlier start', from: '(c.end === best.end && (c.start > best.start ||', to: '(c.end === best.end && (c.start < best.start ||' },
    { name: 'nearestPrevious: tie goes to the earlier position', from: '(c.start === best.start && c.index > best.index)', to: '(c.start === best.start && c.index < best.index)' },
    { name: 'cueForTime: no nearest-previous fallback', from: 'if (previous) {', to: 'if (false) {' },
    { name: 'cueForTime: no upcoming-cue fallback', from: 'if (next) {', to: 'if (false) {' },
    { name: 'cueForTime: only the first active cue', from: 'return { mode: \'active\', cues: active };', to: 'return { mode: \'active\', cues: active.slice(0, 1) };' },
    { name: 'nextAfter: start equal to t counts as later', from: 'return c.start > t;\n        }));\n        return later.length', to: 'return c.start >= t;\n        }));\n        return later.length' },
    { name: 'stepCue: works on file order, not time order', from: 'const sorted = sortedCues(cues);', to: 'const sorted = (cues || []).slice();' },
    { name: 'stepCue: directions swapped', from: 'const target = direction < 0 ?', to: 'const target = direction > 0 ?' },
    { name: 'stepCue: Previous steps from the last shown cue', from: 'Math.min.apply(null, positions) - 1', to: 'Math.max.apply(null, positions) - 1' },
    { name: 'stepCue: Next steps from the first shown cue', from: 'Math.max.apply(null, positions) + 1', to: 'Math.min.apply(null, positions) + 1' },
    { name: 'stepCue: the first cue can never be reached', from: 'return target >= 0 && target < sorted.length', to: 'return target > 0 && target < sorted.length' },
    { name: 'stepCue: the last cue can never be reached', from: 'return target >= 0 && target < sorted.length', to: 'return target >= 0 && target < sorted.length - 1' },
    // ---- reconcile and restore ----
    { name: 'locateCue: compares against newText', from: 'if (atSame && sameText(atSame.text, row.oldText)) {', to: 'if (atSame && sameText(atSame.text, row.newText)) {' },
    { name: 'locateCue: takes the first of several relocation candidates', from: 'return candidates.length === 1 ? candidates[0] : null;', to: 'return candidates.length >= 1 ? candidates[0] : null;' },
    { name: 'locateCue: relocation ignores the end time', from: 'return c.start === row.start && c.end === row.end && sameText(c.text, row.oldText);', to: 'return c.start === row.start && sameText(c.text, row.oldText);' },
    { name: 'locateCue: relocation ignores the oldText', from: 'return c.start === row.start && c.end === row.end && sameText(c.text, row.oldText);', to: 'return c.start === row.start && c.end === row.end;' },
    { name: 'sameText: no line-break normalization', from: 'return normalizeText(a) === normalizeText(b);', to: 'return a === b;' },
    { name: 'reconcile: new lines are marked failed', from: 'summary.newLines.push(row.rowId);\n                    return;', to: 'row.status = \'failed\';\n                    summary.newLines.push(row.rowId);\n                    return;' },
    { name: 'reconcile: a mismatching edit stays staged', from: 'row.message = \'This line changed on disk after you loaded it, so your edit was not applied.\';', to: 'row.message = \'x\'; row.status = \'staged\';' },
    { name: 'reconcile: also re-checks failed rows', from: 'if (row.status !== \'staged\') {', to: 'if (row.status === \'saved\') {' },
    { name: 'reconcile: the new hash is dropped', from: 'this.baseHash = freshHash;', to: 'void freshHash;' },
    { name: 'reconcile: relocations are not reported', from: 'if (found.index !== row.cueIndex) {', to: 'if (false) {' },
    { name: 'reconcile: a kept edit keeps its old times', from: 'row.start = found.start;\n                    row.end = found.end;\n                    summary.kept.push(row.rowId);', to: 'summary.kept.push(row.rowId);' },
    { name: 'restore: no relocation, same number only', from: 'const found = locateCue(row, self.cues);', to: 'const found = findCueByIndex(self.cues, row.cueIndex);' },
    // ---- renumbering and error marking ----
    { name: 'shiftPosition: an insert at the cue\'s own number does not push it', from: 'if (added[i] <= position) {', to: 'if (added[i] < position) {' },
    { name: 'shiftPosition: inserted numbers are not sorted', from: 'const added = (addedPositions || []).slice().sort(function (a, b) {', to: 'const added = (addedPositions || []).slice().sort(function (a, b) {\n            return b - a;' },
    { name: 'applySaveSuccess: edit rows are not renumbered', from: 'row.cueIndex = shiftPosition(row.cueIndex, addedPositions);', to: 'void addedPositions;' },
    { name: 'applySaveSuccess: new lines do not get their number', from: 'row.position = mapping[row.clientId];', to: 'row.position = null;' },
    { name: 'applySaveError: the clientId in the error body is ignored', from: 'if (typeof b.clientId === \'string\' && b.clientId.length > 0) {', to: 'if (false) {' },
    { name: 'applySaveError: marks a new line that was not in the save', from: 'r.type === \'add\' && r.clientId === b.clientId && sent.has(r.rowId)', to: 'r.type === \'add\' && r.clientId === b.clientId' },
    { name: 'applySaveError: TextMismatch does not reconcile', from: 'if (status === 409 && (b.code === \'FileChanged\' || b.code === \'TextMismatch\')) {', to: 'if (status === 409 && b.code === \'FileChanged\') {' },
    { name: 'prepareSave: failed rows are sent too', from: 'return r.status === \'staged\';', to: 'return r.status !== \'saved\';' },
    { name: 'stageEdit: a second edit of a cue adds a second row', from: 'return r.type === \'edit\' && r.cueIndex === cue.index && r.status !== \'saved\';', to: 'return false;' },
    // ---- modes and submissions ----
    { name: 'editorMode: a user who can edit is sent to submit', from: "if (me.canEdit === true) {\n            return 'edit';", to: "if (me.canEdit === true && !me.canSubmit) {\n            return 'edit';" },
    { name: 'editorMode: any truthy canSubmit counts', from: "return me.canSubmit === true ? 'submit' : null;", to: "return me.canSubmit ? 'submit' : null;" },
    { name: 'applySubmitSuccess: removes every row', from: 'return !sent.has(r.rowId);', to: 'return false;' },
    { name: 'submissionsFor: ignores the stream', from: "=== want && s.streamIndex === streamIndex;", to: "=== want;" },
    { name: 'submissionsFor: oldest first', from: "return String(b.created || '').localeCompare(String(a.created || ''));", to: "return String(a.created || '').localeCompare(String(b.created || ''));" },
    { name: 'validateCueText: ASS refuses blank lines too', from: "if (format !== 'ass' && line.trim().length === 0) {", to: "if (line.trim().length === 0) {" },
    { name: 'validateCueText: WebVTT allows "-->"', from: "if (format === 'vtt' && line.indexOf('-->') >= 0) {", to: "if (false) {" },
    { name: 'splitStyleTags: VTT closing tags hidden without an opening tag', from: "const t = prefix ? VTT_TRAILING_TAGS.exec(rest) : null;", to: "const t = VTT_TRAILING_TAGS.exec(rest);" },
    { name: 'splitStyleTags: ASS keeps only the first block', from: "const ASS_LEADING_TAGS = /^(?:\\{[^}]*\\})+/;", to: "const ASS_LEADING_TAGS = /^\\{[^}]*\\}/;" },
    { name: 'screenText: \\h left as is', from: ".replace(/\\\\h/g, ' ')", to: "" },
    { name: 'hasStyleTags: not for WebVTT', from: "return format === 'ass' || format === 'vtt';", to: "return format === 'ass';" },
    // ---- making a track inside the video editable ----
    { name: 'extractChoice: an existing copy is not offered first', from: "if (typeof track.extractedStreamIndex === 'number') {", to: 'if (false) {' },
    { name: 'extractChoice: everyone may replace', from: "return { kind: preview.canReplace ? 'replace' : 'exists', index: index };", to: "return { kind: 'replace', index: index };" },
    { name: 'lineUpCues: the end time is not compared', from: 'left.start === right.start && left.end === right.end && left.text === right.text', to: 'left.start === right.start && left.text === right.text' },
    { name: 'lineUpCues: stops at the shorter side', from: 'for (let i = 0; i < Math.max(a.length, b.length); i++) {', to: 'for (let i = 0; i < Math.min(a.length, b.length); i++) {' },
    { name: 'playWithSubtitle: position sent in ms, not ticks', from: 'startPositionTicks: Math.max(0, Math.round(positionMs || 0)) * 10000,', to: 'startPositionTicks: Math.max(0, Math.round(positionMs || 0)),' },
];

function runTests(logicFile) {
    const result = spawnSync(process.execPath, ['--test'].concat(testFiles), {
        env: Object.assign({}, process.env, { SQE_LOGIC_PATH: logicFile }),
        encoding: 'utf8',
    });
    const failed = /ℹ fail (\d+)/.exec(result.stdout);
    return { status: result.status, failing: failed ? Number(failed[1]) : null };
}

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sqe-mutation-'));
let problems = 0;

const controlFile = path.join(dir, 'control.js');
fs.writeFileSync(controlFile, source);
const control = runTests(controlFile);
console.log('control (unchanged copy): ' + (control.status === 0 ? 'tests pass' : 'TESTS FAIL (' + control.failing + ')'));
if (control.status !== 0) {
    console.log('The control run must pass before mutations mean anything.');
    process.exit(1);
}

let killed = 0;
MUTATIONS.forEach((m, i) => {
    const count = source.split(m.from).length - 1;
    if (count !== 1) {
        console.log('BAD SNIPPET  ' + m.name + ': found ' + count + ' times, need exactly 1');
        problems++;
        return;
    }

    const file = path.join(dir, 'mutant-' + i + '.js');
    fs.writeFileSync(file, source.replace(m.from, () => m.to));
    const syntax = spawnSync(process.execPath, ['--check', file], { encoding: 'utf8' });
    if (syntax.status !== 0) {
        console.log('BAD MUTANT   ' + m.name + ': the broken copy is not valid JavaScript');
        problems++;
        return;
    }

    const r = runTests(file);
    if (r.status === 0) {
        console.log('SURVIVED     ' + m.name);
        problems++;
    } else {
        killed++;
        console.log('killed (' + String(r.failing).padStart(2) + ' failing)  ' + m.name);
    }
});

fs.rmSync(dir, { recursive: true, force: true });
console.log('\n' + killed + ' of ' + MUTATIONS.length + ' mutations killed' + (problems ? ', ' + problems + ' problem(s)' : ''));
process.exit(problems === 0 ? 0 : 1);
