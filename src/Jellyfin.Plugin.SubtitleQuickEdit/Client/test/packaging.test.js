'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const file = process.env.SQE_LOGIC_PATH || path.join(__dirname, '..', 'logic.js');
const source = fs.readFileSync(file, 'utf8');

test('loaded as a plain script (no module object) it defines window.SqeLogic', () => {
    const sandbox = {};
    sandbox.self = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox);
    assert.equal(typeof sandbox.SqeLogic, 'object');
    assert.equal(sandbox.SqeLogic.parseTime('00:00:01,500'), 1500);
    assert.equal(typeof sandbox.SqeLogic.StagingModel, 'function');
});

test('it needs nothing from the page just to be loaded', () => {
    const sandbox = { self: {} };
    vm.createContext(sandbox);
    assert.doesNotThrow(() => vm.runInContext(source, sandbox));
});

test('it avoids syntax that older Android WebViews may not know (?. and ??)', () => {
    const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
    assert.equal(/\?\.[A-Za-z_$(\[]/.test(code), false, 'optional chaining found');
    assert.equal(/\?\?/.test(code), false, 'nullish coalescing found');
});

test('it makes no network or page calls of its own', () => {
    const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
    ['document.', 'XMLHttpRequest', 'fetch(', 'localStorage', 'sessionStorage'].forEach((word) => {
        assert.equal(code.includes(word), false, 'logic.js must not use ' + word);
    });
});
