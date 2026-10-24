// Browser smoke test of the client against a fake jellyfin-web page (mock ApiClient, OSD markup copied from
// docs/client-notes.md). Needs no Jellyfin server, only Playwright for Node and its Chromium:
//   node testdata/browser/fake_page_smoke.js   (with Playwright on NODE_PATH)
// It catches browser-only problems that the Node unit tests cannot (for example "Illegal invocation" from timers).
const { chromium } = require('playwright');
const fs = require('fs');
const C = require('path').join(__dirname, '..', '..', 'src', 'Jellyfin.Plugin.SubtitleQuickEdit', 'Client') + '/';
const script = fs.readFileSync(C + 'logic.js', 'utf8') + '\n' + fs.readFileSync(C + 'quickedit.js', 'utf8');
const css = fs.readFileSync(C + 'quickedit.css', 'utf8');
const fake = `
window.jfKeys = [];
setTimeout(() => {
  document.addEventListener('keydown', (e) => window.jfKeys.push(e.key)); // jellyfin-web's own listener, added later
  window.addEventListener('keydown', (e) => window.jfKeys.push('w:' + e.key), true);
  const R = {
    'Sessions': [{ NowPlayingItem: { Id: 'abc', RunTimeTicks: 600000000 }, PlayState: { MediaSourceId: 'abc', SubtitleStreamIndex: 0 } }],
    'SubtitleQuickEdit/Items/abc/Subtitles': [{ streamIndex: 0, external: true, format: 'srt', editable: true }],
    'SubtitleQuickEdit/Items/abc/Subtitles/0/Cues': { hash: 'h', encoding: 'utf-8', cues: [{ index: 1, start: 0, end: 2000, text: 'Hello there' }, { index: 2, start: 3000, end: 4000, text: 'Two' }] },
  };
  window.ApiClient = { getCurrentUserId: () => 'u1', getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: true } }),
    isLoggedIn: () => true, accessToken: () => 't', deviceId: () => 'd', getUrl: (p) => p,
    getJSON: (u) => R[u] ? Promise.resolve(JSON.parse(JSON.stringify(R[u]))) : Promise.reject(new Error('no ' + u)) };
}, 300);
setTimeout(() => {
  const c = document.createElement('div'); c.className = 'videoPlayerContainer';
  const v = document.createElement('video'); v.className = 'htmlvideoplayer'; c.appendChild(v); document.body.appendChild(c);
  document.body.insertAdjacentHTML('beforeend', '<div class="videoOsdBottom"><div class="osdControls"><div class="buttons"><button class="btnSubtitles">CC</button><button class="btnFullscreen">F</button></div></div></div>');
}, 800);`;
(async () => {
  const b = await chromium.launch(); const p = await b.newPage();
  const logs = []; p.on('console', (m) => logs.push(m.type() + ': ' + m.text())); p.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  await p.setContent(`<html><head><style>${css}</style></head><body><script>${script}</script><script>${fake}</script></body></html>`);
  await p.waitForSelector('.sqe-edit-button', { timeout: 8000 });
  const order = await p.evaluate(() => Array.from(document.querySelectorAll('.buttons button')).map((x) => x.className.split(' ')[0]));
  await p.click('.sqe-edit-button');
  await p.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  const txt = await p.inputValue('.sqe-cue-text');
  const focused = await p.evaluate(() => document.activeElement.className);
  await p.keyboard.type(' f k');
  await p.keyboard.press('ArrowLeft');
  const keys = await p.evaluate(() => window.jfKeys.slice());
  // Stage 3d: Apply, the changes list, editing a cue twice, Next/Previous, row click, undo.
  const texts = () => p.$$eval('.sqe-cue', (els) => els.map((e) => e.getAttribute('data-cue-index') + ':' + e.querySelector('textarea').value));
  const rows = () => p.$$eval('.sqe-change', (els) => els.map((e) => e.querySelector('.sqe-change-where').textContent + '|' + e.querySelector('.sqe-change-new').textContent));
  await p.fill('.sqe-cue-text', 'Hello world');
  await p.keyboard.press('Control+Enter');
  const rows1 = await rows();
  await p.fill('.sqe-cue-text', 'Hello, world');
  await p.click('.sqe-apply');
  const rows2 = await rows();
  await p.click('.sqe-next');
  const afterNext = await texts();
  const prevDisabledAtStart = await p.$eval('.sqe-prev', (b) => b.disabled);
  await p.click('.sqe-change');
  const afterRowClick = await texts();
  await p.click('.sqe-undo');
  const rows3 = await rows();
  const afterUndo = await texts();
  await p.fill('.sqe-cue-text', 'bad\n\nblank line');
  await p.click('.sqe-apply');
  const fieldError = await p.$eval('.sqe-field-error', (e) => (e.hidden ? '' : e.textContent));
  await p.focus('.sqe-cue-text');
  await p.keyboard.press('Escape');
  const open = await p.$('.sqe-panel');
  const checks = {
    'button sits right after the subtitle button': order.join() === 'btnSubtitles,sqe-edit-button,btnFullscreen',
    'panel shows the cue at the current time': txt === 'Hello there',
    'the text box has focus': focused === 'sqe-cue-text',
    'no key reached the page\'s own listeners': keys.length === 0,
    'Ctrl+Enter stages an edit and adds a row': rows1.length === 1 && rows1[0].indexOf('Hello world') > 0,
    'editing the same cue again updates its row': rows2.length === 1 && rows2[0].indexOf('Hello, world') > 0,
    'Next shows the next line': afterNext.join() === '2:Two',
    'Previous is enabled after Next': prevDisabledAtStart === false,
    'clicking a row shows its line with the staged text': afterRowClick.join() === '1:Hello, world',
    'undo removes the row and restores the text': rows3.length === 0 && afterUndo.join() === '1:Hello there',
    'invalid text shows an error and stages nothing': fieldError.length > 0,
    'Esc closed the panel': !open,
    'no errors in the console': logs.every((l) => !/^(error|pageerror)/.test(l)),
  };
  Object.keys(checks).forEach((k) => console.log((checks[k] ? 'PASS ' : 'FAIL ') + k));
  if (!Object.values(checks).every(Boolean)) {
    console.log(JSON.stringify({ order, txt, focused, keys, rows1, rows2, afterNext, afterRowClick, rows3, afterUndo, fieldError, logs }, null, 1));
    process.exitCode = 1;
  }
  await b.close();
})().catch((e) => { console.error('FAIL', e.message); process.exit(1); });
