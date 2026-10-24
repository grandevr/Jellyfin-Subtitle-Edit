'use strict';
// The panel's text colours against their backgrounds, from the variables in quickedit.css (WCAG AA: 4.5:1 for text,
// 3:1 for the focus ring).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const css = fs.readFileSync(path.join(__dirname, '..', 'quickedit.css'), 'utf8');

function variable(name) {
    const m = new RegExp('--sqe-' + name + ':\\s*(#[0-9a-fA-F]{6}|rgba\\([^)]*\\))').exec(css);
    assert.ok(m, '--sqe-' + name + ' is defined');
    return m[1];
}

/** [r, g, b] in 0..255; an rgba() colour is blended over black, the video behind the panel at its darkest. */
function rgb(value) {
    if (value[0] === '#') {
        return [1, 3, 5].map((i) => parseInt(value.slice(i, i + 2), 16));
    }

    const parts = value.replace(/rgba\(|\)/g, '').split(',').map(Number);
    return parts.slice(0, 3).map((c) => c * parts[3]);
}

function luminance(c) {
    const [r, g, b] = c.map((v) => {
        const s = v / 255;
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a, b) {
    const [hi, lo] = [luminance(rgb(a)), luminance(rgb(b))].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
}

test('panel text colours reach 4.5:1 on the panel', () => {
    const bg = variable('bg');
    ['fg', 'muted', 'error', 'warning'].forEach((name) => {
        const ratio = contrast(variable(name), bg);
        assert.ok(ratio >= 4.5, name + ' is ' + ratio.toFixed(2) + ':1');
    });
});

test('white text on the Save button reaches 4.5:1', () => {
    const ratio = contrast('#ffffff', variable('primary'));
    assert.ok(ratio >= 4.5, ratio.toFixed(2) + ':1');
});

test('the focus ring reaches 3:1 on the panel', () => {
    const ratio = contrast(variable('accent'), variable('bg'));
    assert.ok(ratio >= 3, ratio.toFixed(2) + ':1');
});
