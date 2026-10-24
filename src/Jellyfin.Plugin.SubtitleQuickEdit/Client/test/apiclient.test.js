'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

// A fake clock and scheduler: time moves only when the test says so.
function fakeTime() {
    const t = { now: 0, timers: [], scheduled: 0 };
    t.nowFn = () => t.now;
    t.setTimer = (fn, ms) => { t.scheduled++; t.timers.push({ fn: fn, at: t.now + ms }); };
    t.tick = (ms) => {
        t.now += ms;
        const due = t.timers.filter((x) => x.at <= t.now);
        t.timers = t.timers.filter((x) => x.at > t.now);
        due.forEach((x) => x.fn());
    };
    return t;
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

function readyClient(extra) {
    return Object.assign({
        getCurrentUser: () => Promise.resolve({}),
        getUrl: (n) => '/' + n,
        isLoggedIn: () => true,
        accessToken: () => 'token-value',
    }, extra || {});
}

function options(t, getClient, more) {
    return Object.assign({ getClient: getClient, now: t.nowFn, setTimer: t.setTimer, timeoutMs: 1000, intervalMs: 100 }, more || {});
}

test('resolves at once with a ready client, without waiting', async () => {
    const t = fakeTime();
    const client = readyClient();
    assert.equal(await L.waitForApiClient(options(t, () => client)), client);
    assert.equal(t.scheduled, 0);
});

test('keeps checking until the client appears', async () => {
    const t = fakeTime();
    let client;
    const p = L.waitForApiClient(options(t, () => client));
    t.tick(100);
    t.tick(100);
    client = readyClient();
    t.tick(100);
    assert.equal(await p, client);
    assert.equal(t.scheduled, 3);
});

test('waits for the login: a client that exists but is not logged in is not ready', async () => {
    const t = fakeTime();
    let loggedIn = false;
    const client = readyClient({ isLoggedIn: () => loggedIn });
    const p = L.waitForApiClient(options(t, () => client));
    t.tick(100);
    t.tick(100);
    loggedIn = true;
    t.tick(100);
    assert.equal(await p, client);
});

test('a client with no access token yet is not ready', async () => {
    const t = fakeTime();
    let token = '';
    const client = readyClient({ accessToken: () => token });
    const p = L.waitForApiClient(options(t, () => client));
    t.tick(100);
    token = 'abc';
    t.tick(100);
    assert.equal(await p, client);
});

test('requireLogin: false accepts a client that is not logged in', async () => {
    const t = fakeTime();
    const client = readyClient({ isLoggedIn: () => false });
    assert.equal(await L.waitForApiClient(options(t, () => client, { requireLogin: false })), client);
});

test('a client missing getCurrentUser or getUrl is not ready', async () => {
    const t = fakeTime();
    const p = L.waitForApiClient(options(t, () => ({ isLoggedIn: () => true }), { timeoutMs: 300 }));
    const failed = p.then(() => 'resolved', (e) => e.message);
    t.tick(100);
    t.tick(100);
    t.tick(100);
    assert.match(await failed, /Timed out/);
});

test('reads the client fresh each time, so a replaced instance is picked up', async () => {
    const t = fakeTime();
    let current = { half: 'built' };
    const second = readyClient();
    const p = L.waitForApiClient(options(t, () => current));
    t.tick(100);
    current = second;
    t.tick(100);
    assert.equal(await p, second);
});

test('rejects after the timeout, checking at each interval until then', async () => {
    const t = fakeTime();
    const p = L.waitForApiClient(options(t, () => undefined, { timeoutMs: 500, intervalMs: 100 }));
    const outcome = p.then(() => 'resolved', (e) => e);
    for (let i = 0; i < 5; i++) {
        await flush();
        t.tick(100);
    }

    const error = await outcome;
    assert.ok(error instanceof Error);
    assert.match(error.message, /Timed out waiting for ApiClient/);
    assert.equal(t.scheduled, 5);
    assert.equal(t.timers.length, 0);
});

test('does not reject before the timeout has passed', async () => {
    const t = fakeTime();
    let settled = false;
    const p = L.waitForApiClient(options(t, () => undefined, { timeoutMs: 500, intervalMs: 100 }));
    p.then(() => { settled = true; }, () => { settled = true; });
    t.tick(100);
    t.tick(100);
    t.tick(100);
    t.tick(100);
    await flush();
    assert.equal(settled, false);
    t.tick(100);
    await flush();
    assert.equal(settled, true);
});

test('a getClient that throws counts as not ready, and the wait goes on', async () => {
    const t = fakeTime();
    let calls = 0;
    const client = readyClient();
    const p = L.waitForApiClient(options(t, () => {
        calls++;
        if (calls < 3) {
            throw new Error('not yet');
        }

        return client;
    }));
    t.tick(100);
    t.tick(100);
    assert.equal(await p, client);
});

test('an isLoggedIn that throws counts as not ready', async () => {
    const t = fakeTime();
    let broken = true;
    const client = readyClient({ isLoggedIn: () => { if (broken) { throw new Error('x'); } return true; } });
    const p = L.waitForApiClient(options(t, () => client));
    t.tick(100);
    broken = false;
    t.tick(100);
    assert.equal(await p, client);
});

test('works with the real clock too', async () => {
    let client;
    setTimeout(() => { client = readyClient(); }, 30);
    const got = await L.waitForApiClient({ getClient: () => client, intervalMs: 5, timeoutMs: 2000 });
    assert.equal(got, client);
    await assert.rejects(L.waitForApiClient({ getClient: () => undefined, intervalMs: 5, timeoutMs: 30 }), /Timed out/);
});
