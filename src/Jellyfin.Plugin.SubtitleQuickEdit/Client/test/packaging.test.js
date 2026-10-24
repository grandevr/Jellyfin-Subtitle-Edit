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

const pageFile = path.join(__dirname, '..', 'quickedit.js');
const pageSource = fs.readFileSync(pageFile, 'utf8');
const pageCode = pageSource.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');

test('the page code parses and avoids ?. and ?? as well', () => {
    assert.doesNotThrow(() => new vm.Script(pageSource));
    assert.equal(/\?\.[A-Za-z_$(\[]/.test(pageCode), false, 'optional chaining found');
    assert.equal(/\?\?/.test(pageCode), false, 'nullish coalescing found');
});

test('every logic function the page code calls exists', () => {
    const L = require('./load.js');
    const used = new Set();
    pageCode.replace(/\bL\.([A-Za-z_$][\w$]*)/g, (m, name) => used.add(name));
    assert.ok(used.size > 0);
    used.forEach((name) => assert.notEqual(L[name], undefined, 'logic.js does not export ' + name));
});

test('the page code never logs a URL or a token', () => {
    const logs = pageCode.match(/console\.\w+\([^)]*\)/g) || [];
    logs.forEach((call) => assert.equal(/getUrl|accessToken|ApiKey|src/.test(call), false, call));
});
