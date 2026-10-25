'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

// ---- text rules per format ------------------------------------------------------------------------------------

test('srt text keeps its rules: no blank line, no time line', () => {
    assert.equal(L.validateCueText('a\n\nb').error.code, 'TextBlankLine');
    assert.equal(L.validateCueText('00:00:01,000 --> 00:00:02,000').error.code, 'TextLooksLikeTime');
    assert.equal(L.validateCueText('a --> b').ok, true);
    assert.equal(L.validateCueText('a\n\nb', 'srt').error.code, 'TextBlankLine');
});

test('vtt text may not hold an arrow or a blank line', () => {
    assert.equal(L.validateCueText('a --> b', 'vtt').error.code, 'TextArrow');
    assert.equal(L.validateCueText('a\n\nb', 'vtt').error.code, 'TextBlankLine');
    assert.equal(L.validateCueText('<v Anna>Hi</v>', 'vtt').ok, true);
});

test('ass text may hold blank lines and arrows, but not control characters or nothing', () => {
    assert.deepEqual(L.validateCueText('a\r\n\r\nb', 'ass'), { ok: true, text: 'a\n\nb' });
    assert.equal(L.validateCueText('a --> b', 'ass').ok, true);
    assert.equal(L.validateCueText('a\u0001', 'ass').error.code, 'TextControlChar');
    assert.equal(L.validateCueText('', 'ass').error.code, 'TextEmpty');
});

test('new lines and the staging model check text for their format', () => {
    const line = { startMs: 1000, endMs: 3000, text: 'a --> b' };
    assert.equal(L.validateNewLine(line, { format: 'vtt' }).ok, false);
    assert.equal(L.validateNewLine(line, { format: 'srt' }).ok, true);
    const m = new L.StagingModel({ format: 'vtt' });
    m.setCues([{ index: 1, start: 0, end: 1000, text: 'One' }], 'h');
    assert.equal(m.stageEdit({ index: 1, start: 0, end: 1000, text: 'One' }, 'x --> y').error.code, 'TextArrow');
    assert.equal(m.stageAdd(line).ok, false);
    const ass = new L.StagingModel({ format: 'ass' });
    ass.setCues([{ index: 1, start: 0, end: 1000, text: 'One' }], 'h');
    assert.equal(ass.stageEdit({ index: 1, start: 0, end: 1000, text: 'One' }, 'a\n\nb').ok, true);
});

// ---- style tags -------------------------------------------------------------------------------------------------

test('ass: the override blocks at the start are split off, inline ones stay', () => {
    assert.deepEqual(L.splitStyleTags('{\\an8}{\\b1}Hello {\\i1}there{\\i0}', 'ass'),
        { prefix: '{\\an8}{\\b1}', body: 'Hello {\\i1}there{\\i0}', suffix: '' });
    assert.deepEqual(L.splitStyleTags('Plain', 'ass'), { prefix: '', body: 'Plain', suffix: '' });
    assert.deepEqual(L.splitStyleTags('Not {\\an8}first', 'ass'), { prefix: '', body: 'Not {\\an8}first', suffix: '' });
});

test('vtt: opening tags and the closing tags at the end are split off', () => {
    assert.deepEqual(L.splitStyleTags('<v Anna><i>Hello\nthere</i></v>', 'vtt'),
        { prefix: '<v Anna><i>', body: 'Hello\nthere', suffix: '</i></v>' });
    assert.deepEqual(L.splitStyleTags('<i>a</i> and <i>b</i>', 'vtt'), { prefix: '<i>', body: 'a</i> and <i>b', suffix: '</i>' });
    assert.deepEqual(L.splitStyleTags('ends in </i>', 'vtt'), { prefix: '', body: 'ends in </i>', suffix: '' });
});

test('splitting never changes the text and srt has no tags to hide', () => {
    ['{\\an8}x', '<v A>x</v>', 'x', '', '<b>', '{\\p1}m 0 0{\\p0}'].forEach((t) => {
        ['ass', 'vtt', 'srt'].forEach((f) => {
            const p = L.splitStyleTags(t, f);
            assert.equal(p.prefix + p.body + p.suffix, t);
        });
    });
    assert.deepEqual(L.splitStyleTags('<i>x</i>', 'srt'), { prefix: '', body: '<i>x</i>', suffix: '' });
    assert.equal(L.hasStyleTags('ass'), true);
    assert.equal(L.hasStyleTags('vtt'), true);
    assert.equal(L.hasStyleTags('srt'), false);
});

test('screenText drops ass override blocks for comparing with the player track', () => {
    assert.equal(L.screenText('{\\an8}Hello\\hthere {\\i1}you{\\i0}', 'ass'), 'Hello there you');
    assert.equal(L.screenText('<i>x</i>', 'vtt'), '<i>x</i>');
    assert.equal(L.screenText('{x}', 'srt'), '{x}');
});
