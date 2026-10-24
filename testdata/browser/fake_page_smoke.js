// Browser smoke test of the client against a fake jellyfin-web page (mock ApiClient and server, OSD markup copied
// from docs/client-notes.md). Needs no Jellyfin server, only Playwright for Node and its Chromium:
//   node testdata/browser/fake_page_smoke.js   (with Playwright on NODE_PATH)
// It catches browser-only problems that the Node unit tests cannot (for example "Illegal invocation" from timers).
// The page is served from a fake origin so that localStorage and page reloads work like on a real server.
const { chromium } = require('playwright');
const fs = require('fs');
const C = require('path').join(__dirname, '..', '..', 'src', 'Jellyfin.Plugin.SubtitleQuickEdit', 'Client') + '/';
const script = fs.readFileSync(C + 'logic.js', 'utf8') + '\n' + fs.readFileSync(C + 'quickedit.js', 'utf8');
const css = fs.readFileSync(C + 'quickedit.css', 'utf8');
const ORIGIN = 'http://sqe-fake.test';

// The fake server and jellyfin-web. The "file" lives in sessionStorage so it survives a page reload.
const fake = `
window.jfKeys = [];
const FILE_KEY = 'fake-file';
const initial = { hash: 'h0', n: 0, cues: [{ index: 1, start: 0, end: 2000, text: 'Hello there' }, { index: 2, start: 3000, end: 4000, text: 'Two' }] };
function file() { return JSON.parse(sessionStorage.getItem(FILE_KEY) || JSON.stringify(initial)); }
function store(f) { sessionStorage.setItem(FILE_KEY, JSON.stringify(f)); }
function copy(x) { return JSON.parse(JSON.stringify(x)); }
function refused(status, body) { return Promise.reject({ status: status, json: () => Promise.resolve(body) }); }
window.fakeEditsSent = [];
window.fakeExternalChange = function () {
  const f = file(); f.cues[1].text = 'Two (changed on disk)'; f.n++; f.hash = 'h' + f.n + 'x'; store(f);
};
function fillTrack(track) {
  Array.from(track.cues || []).forEach((c) => track.removeCue(c));
  file().cues.forEach((c) => track.addCue(new VTTCue(c.start / 1000, c.end / 1000, '\\u200e' + c.text)));
}
setTimeout(() => {
  document.addEventListener('keydown', (e) => window.jfKeys.push(e.key)); // jellyfin-web's own listener, added later
  window.addEventListener('keydown', (e) => window.jfKeys.push('w:' + e.key), true);
  const R = {
    'Sessions': () => [{ Id: 's1', NowPlayingItem: { Id: 'abc', RunTimeTicks: 600000000 }, PlayState: { MediaSourceId: 'abc', SubtitleStreamIndex: 0 } }],
    'SubtitleQuickEdit/Items/abc/Subtitles': () => [{ streamIndex: 0, external: true, format: 'srt', editable: true }],
    'SubtitleQuickEdit/Items/abc/Subtitles/0/Cues': () => { const f = file(); return { hash: f.hash, encoding: 'utf-8', cues: f.cues }; },
  };
  window.ApiClient = { getCurrentUserId: () => 'u1', getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: true } }),
    isLoggedIn: () => true, accessToken: () => 't', deviceId: () => 'd', getUrl: (p) => p,
    getJSON: (u) => R[u] ? Promise.resolve(copy(R[u]())) : Promise.reject(new Error('no ' + u)),
    ajax: (o) => {
      const body = o.data ? JSON.parse(o.data) : null;
      if (o.url === 'SubtitleQuickEdit/Items/abc/Subtitles/0/Edits') {
        window.fakeEditsSent.push(body);
        const f = file();
        if (body.baseHash !== f.hash) { return refused(409, { code: 'FileChanged', message: 'changed', currentHash: f.hash }); }
        body.edits.forEach((e) => { f.cues[e.cueIndex - 1].text = e.newText; });
        f.n++; f.hash = 'h' + f.n; store(f);
        return Promise.resolve({ hash: f.hash, savedCueIndexes: body.edits.map((e) => e.cueIndex), addedCues: {}, writeMethod: 'Rename' });
      }
      if (o.url === 'Sessions/s1/Command' && body.Name === 'SetSubtitleStreamIndex') {
        const track = Array.from(document.querySelector('video').textTracks).find((t) => t.label === 'manualTrack');
        if (body.Arguments.Index === '-1') { track.mode = 'disabled'; } else { track.mode = 'showing'; setTimeout(() => fillTrack(track), 300); }
        return Promise.resolve(null);
      }
      return refused(404, { message: 'no route' });
    } };
}, 300);
setTimeout(() => {
  const c = document.createElement('div'); c.className = 'videoPlayerContainer';
  const v = document.createElement('video'); v.className = 'htmlvideoplayer'; c.appendChild(v); document.body.appendChild(c);
  const track = v.addTextTrack('subtitles', 'manualTrack', 'und'); track.mode = 'showing'; fillTrack(track);
  document.body.insertAdjacentHTML('beforeend', '<div class="videoOsdBottom"><div class="osdControls"><div class="buttons"><button class="btnSubtitles">CC</button><button class="btnFullscreen">F</button></div></div></div>');
}, 800);`;
const html = `<html><head><style>${css}</style></head><body><script>${script}</script><script>${fake}</script></body></html>`;

(async () => {
  const b = await chromium.launch();
  const p = await b.newPage();
  await p.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  const logs = [];
  p.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
  p.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  p.on('dialog', (d) => d.accept());
  const checks = {};
  const info = {};
  const texts = () => p.$$eval('.sqe-cue', (els) => els.map((e) => e.getAttribute('data-cue-index') + ':' + e.querySelector('textarea').value));
  const rows = () => p.$$eval('.sqe-change', (els) => els.map((e) => e.querySelector('.sqe-change-where').textContent + '|' +
    e.querySelector('.sqe-change-new').textContent + '|' + e.querySelector('.sqe-badge').textContent));
  const status = () => p.$eval('.sqe-status', (e) => e.textContent);
  async function openPanel() {
    await p.waitForSelector('.sqe-edit-button', { timeout: 8000 });
    await p.click('.sqe-edit-button');
    await p.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  }

  await p.goto(ORIGIN + '/web/index.html');
  await openPanel();
  const order = await p.evaluate(() => Array.from(document.querySelectorAll('.buttons button')).map((x) => x.className.split(' ')[0]));
  checks['button sits right after the subtitle button'] = order.join() === 'btnSubtitles,sqe-edit-button,btnFullscreen';
  checks['panel shows the cue at the current time'] = (await p.inputValue('.sqe-cue-text')) === 'Hello there';
  checks['the text box has focus'] = (await p.evaluate(() => document.activeElement.className)) === 'sqe-cue-text';
  await p.keyboard.type(' f k');
  await p.keyboard.press('ArrowLeft');
  checks['no key reached the page\'s own listeners'] = (await p.evaluate(() => window.jfKeys.length)) === 0;

  // 3d: Apply, the changes list, editing a cue twice, Next/Previous, row click, undo.
  await p.fill('.sqe-cue-text', 'Hello world');
  await p.keyboard.press('Control+Enter');
  const rows1 = await rows();
  checks['Ctrl+Enter stages an edit and adds a row'] = rows1.length === 1 && rows1[0].indexOf('Hello world') > 0;
  await p.fill('.sqe-cue-text', 'Hello, world');
  await p.click('.sqe-apply');
  const rows2 = await rows();
  checks['editing the same cue again updates its row'] = rows2.length === 1 && rows2[0].indexOf('Hello, world') > 0;
  await p.click('.sqe-next');
  checks['Next shows the next line'] = (await texts()).join() === '2:Two';
  checks['Previous is enabled after Next'] = (await p.$eval('.sqe-prev', (x) => x.disabled)) === false;
  await p.click('.sqe-change');
  checks['clicking a row shows its line with the staged text'] = (await texts()).join() === '1:Hello, world';
  await p.click('.sqe-undo');
  checks['undo removes the row and restores the text'] = (await rows()).length === 0 && (await texts()).join() === '1:Hello there';
  await p.fill('.sqe-cue-text', 'bad\n\nblank line');
  await p.click('.sqe-apply');
  checks['invalid text shows an error and stages nothing'] =
    (await p.$eval('.sqe-field-error', (e) => (e.hidden ? '' : e.textContent))).length > 0 && (await rows()).length === 0;

  // 3e: a staged change survives a page reload.
  await p.fill('.sqe-cue-text', 'Hello draft');
  await p.click('.sqe-apply');
  await p.reload();
  await openPanel();
  const restored = await rows();
  checks['a staged change survives a page reload'] = restored.length === 1 && restored[0].indexOf('Hello draft') > 0 &&
    (await status()).indexOf('Restored 1') === 0;
  info.restored = restored;

  // 3e: Save.
  await p.focus('.sqe-cue-text');
  await p.keyboard.press('Control+s');
  await p.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Saved') === 0, null, { timeout: 5000 });
  const sent = await p.evaluate(() => window.fakeEditsSent);
  const saved = await rows();
  checks['Ctrl+S saves the staged change in one request'] = sent.length === 1 && sent[0].baseHash === 'h0' &&
    sent[0].edits.length === 1 && sent[0].edits[0].oldText === 'Hello there' && sent[0].edits[0].newText === 'Hello draft';
  checks['the saved row is marked saved'] = saved.length === 1 && saved[0].endsWith('|saved');
  checks['the draft is cleared after saving'] = (await p.evaluate(() => Object.keys(localStorage).filter((k) => k.indexOf('sqe:') === 0).length)) === 0;
  info.saved = saved;

  // 3e: Reload subtitles (the fake player fetches the track again when switched off and on).
  await p.click('.sqe-reload');
  await p.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Subtitles reloaded') === 0, null, { timeout: 12000 });
  const trackText = await p.evaluate(() => Array.from(Array.from(document.querySelector('video').textTracks)
    .find((t) => t.label === 'manualTrack').cues).map((c) => c.text).join('|'));
  checks['Reload subtitles makes the player show the saved text'] = trackText.indexOf('Hello draft') >= 0;
  checks['Reload went through the session command (the player\'s own fetch), not the fallback'] = trackText.indexOf('\u200e') >= 0;
  info.trackText = trackText;

  // 3e: the file changes on disk mid-session; Save says so and writes nothing.
  await p.click('.sqe-next');
  await p.fill('.sqe-cue-text', 'Two edited');
  await p.click('.sqe-apply');
  await p.click('.sqe-prev');
  await p.fill('.sqe-cue-text', 'Hello again');
  await p.click('.sqe-apply');
  await p.evaluate(() => window.fakeExternalChange());
  await p.click('.sqe-save');
  await p.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('changed on disk') > 0, null, { timeout: 5000 });
  const conflict = await rows();
  const fileAfter = await p.evaluate(() => JSON.parse(sessionStorage.getItem('fake-file')).cues.map((c) => c.text).join('|'));
  checks['a changed file gives the "file changed" message and writes nothing'] = fileAfter === 'Hello draft|Two (changed on disk)';
  checks['after the conflict, the still-matching edit stays staged and the other is failed'] =
    conflict.some((r) => r.indexOf('Hello again') > 0 && r.endsWith('|staged')) && conflict.some((r) => r.indexOf('Two edited') > 0 && r.endsWith('|failed'));
  info.conflict = conflict;
  await p.click('.sqe-save');
  await p.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Saved') === 0, null, { timeout: 5000 });
  checks['saving again writes the still-matching edit'] =
    (await p.evaluate(() => JSON.parse(sessionStorage.getItem('fake-file')).cues[0].text)) === 'Hello again';

  await p.focus('.sqe-cue-text');
  await p.keyboard.press('Escape');
  checks['Esc closed the panel'] = !(await p.$('.sqe-panel'));
  checks['no errors in the console'] = logs.every((l) => !/^(error|pageerror)/.test(l));

  Object.keys(checks).forEach((k) => console.log((checks[k] ? 'PASS ' : 'FAIL ') + k));
  if (!Object.values(checks).every(Boolean)) {
    console.log(JSON.stringify({ info, logs }, null, 1));
    process.exitCode = 1;
  }
  await b.close();
})().catch((e) => { console.error('FAIL', e.message); process.exit(1); });
