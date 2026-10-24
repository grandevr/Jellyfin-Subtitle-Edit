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
const CUSTOM = location.search.indexOf('custom=1') >= 0; // jellyfin-web's custom subtitle renderer instead of a text track
window.fakeEvents = null;
function renderCustom() { // jellyfin-web's updateSubtitleText: the first cue that covers the time, or hidden
  const inner = document.querySelector('.videoSubtitlesInner'); const v = document.querySelector('video');
  if (!inner || !window.fakeEvents) { return; }
  const t = v.currentTime * 1000; const c = window.fakeEvents.find((x) => x.start <= t && t <= x.end);
  if (c) { inner.textContent = c.text; inner.classList.remove('hide'); } else { inner.classList.add('hide'); }
}
function showCustom() {
  window.fakeEvents = file().cues;
  if (!document.querySelector('.videoSubtitles')) {
    const box = document.createElement('div'); box.className = 'videoSubtitles';
    const inner = document.createElement('div'); inner.className = 'videoSubtitlesInner'; box.appendChild(inner);
    document.querySelector('.videoPlayerContainer').appendChild(box);
  }
}
const FILE_KEY = 'fake-file';
const initial = { hash: 'h0', n: 0, cues: [{ index: 1, start: 0, end: 2000, text: 'Hello there' }, { index: 2, start: 3000, end: 4000, text: 'Two' }] };
function file() { return JSON.parse(sessionStorage.getItem(FILE_KEY) || JSON.stringify(initial)); }
function store(f) { sessionStorage.setItem(FILE_KEY, JSON.stringify(f)); keep(f); }
// Every version the "file" has had, by hash: the plugin's backups, for Restore.
function versions() { const v = JSON.parse(sessionStorage.getItem('fake-versions') || '{}'); v[initial.hash] = v[initial.hash] || copy(initial.cues); return v; }
function keep(f) { const v = versions(); v[f.hash] = copy(f.cues); sessionStorage.setItem('fake-versions', JSON.stringify(v)); }
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
        body.edits.filter((e) => e.type === 'edit').forEach((e) => { f.cues[e.cueIndex - 1].text = e.newText; });
        const added = {};
        body.edits.filter((e) => e.type === 'add').forEach((e) => {
          const cue = { start: e.startMs, end: e.endMs, text: e.text, clientId: e.clientId };
          const at = f.cues.findIndex((c) => c.start > e.startMs); // before the first cue that starts later
          f.cues.splice(at < 0 ? f.cues.length : at, 0, cue);
        });
        f.cues.forEach((c, i) => { c.index = i + 1; if (c.clientId) { added[c.clientId] = c.index; delete c.clientId; } });
        f.n++; f.hash = 'h' + f.n; store(f);
        return Promise.resolve({ hash: f.hash, savedCueIndexes: body.edits.filter((e) => e.type === 'edit').map((e) => e.cueIndex),
          addedCues: added, writeMethod: 'Rename' });
      }
      if (o.url === 'SubtitleQuickEdit/Items/abc/Subtitles/0/Restore') {
        window.fakeRestoreSent = body;
        const f = file(); const kept = versions()[body.targetHash];
        if (body.baseHash !== f.hash) { return refused(409, { code: 'FileChanged', message: 'changed', currentHash: f.hash }); }
        if (!kept) { return refused(404, { code: 'RestorePointNotFound', message: 'none' }); }
        f.cues = kept; f.n++; f.hash = body.targetHash; store(f);
        return Promise.resolve({ hash: f.hash, restored: true, writeMethod: 'Rename' });
      }
      if (o.url === 'Sessions/s1/Command' && body.Name === 'SetSubtitleStreamIndex') {
        if (CUSTOM) {
          if (body.Arguments.Index === '-1') { const box = document.querySelector('.videoSubtitles'); if (box) { box.remove(); } } else { setTimeout(showCustom, 300); }
        } else {
          const track = Array.from(document.querySelector('video').textTracks).find((t) => t.label === 'manualTrack');
          if (body.Arguments.Index === '-1') { track.mode = 'disabled'; } else { track.mode = 'showing'; setTimeout(() => fillTrack(track), 300); }
        }
        // The real server carries the command out and answers 204 with no body, which ApiClient fails to parse as JSON.
        return o.dataType === 'json' ? Promise.reject(new SyntaxError('Unexpected end of JSON input')) : Promise.resolve(undefined);
      }
      return refused(404, { message: 'no route' });
    } };
}, 300);
setTimeout(() => {
  const c = document.createElement('div'); c.className = 'videoPlayerContainer';
  const v = document.createElement('video'); v.className = 'htmlvideoplayer'; c.appendChild(v); document.body.appendChild(c);
  // A video with no source fires no seek events: make setting currentTime behave like a real seek.
  let time = 0;
  Object.defineProperty(v, 'currentTime', { get: () => time, set: (x) => { time = x; v.dispatchEvent(new Event('seeked')); v.dispatchEvent(new Event('timeupdate')); } });
  v.addEventListener('timeupdate', renderCustom); // jellyfin-web's own handler, registered before the editor sees the video
  if (CUSTOM) { showCustom(); } else { const track = v.addTextTrack('subtitles', 'manualTrack', 'und'); track.mode = 'showing'; fillTrack(track); }
  document.body.insertAdjacentHTML('beforeend', '<div class="videoOsdBottom"><div class="osdControls"><div class="buttons"><button class="btnSubtitles">CC</button><button class="btnFullscreen">F</button></div></div></div>');
}, 800);`;
const html = `<html><head><meta charset="utf-8"><style>${css}</style></head><body><script>${script}</script><script>${fake}</script></body></html>`;

(async () => {
  const b = await chromium.launch();
  const p = await b.newPage();
  await p.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  const logs = []; global.sqeLogs = logs;
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
  const trackTexts = () => p.evaluate(() => Array.from(Array.from(document.querySelector('video').textTracks)
    .find((t) => t.label === 'manualTrack').cues).map((c) => c.text.replace(/\u200e/g, '')).join('|'));
  checks['a staged edit shows in the player\'s text track'] = (await trackTexts()) === 'Hello world|Two';
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
  checks['undo puts the player\'s text back'] = (await trackTexts()) === 'Hello there|Two';
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
  checks['the first save of the session asks the server to keep a restore point'] = sent[0].keepRestorePoint === true;
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

  // 3f: add a line.
  await p.evaluate(() => { document.querySelector('video').currentTime = 0; });
  await p.click('.sqe-add');
  await p.waitForSelector('.sqe-new-line', { timeout: 3000 });
  const field = (k) => p.inputValue('.sqe-new-line .sqe-' + k);
  const warning = () => p.$eval('.sqe-warning', (e) => (e.hidden ? '' : e.textContent));
  const fieldErr = (k) => p.$eval('#sqe-new-' + k, (e) => { const x = e.closest('.sqe-field').querySelector('.sqe-field-error'); return x.hidden ? '' : x.textContent; });
  checks['Add line prefills start at the current time and end 2 s later'] =
    (await field('start')) === '00:00:00,000' && (await field('end')) === '00:00:02,000';
  checks['the new-line text box has focus'] = (await p.evaluate(() => document.activeElement.id)) === 'sqe-new-text';
  checks['an overlap is warned about'] = (await warning()).indexOf('Overlaps cue 1') === 0;
  await p.click('.sqe-start-plus');
  await p.click('.sqe-start-plus');
  await p.click('.sqe-start-minus');
  checks['the nudge buttons move the time by 100 ms'] = (await field('start')) === '00:00:00,100';
  await p.fill('.sqe-new-line .sqe-end', '00:00:00,050');
  await p.fill('.sqe-new-text', 'Brand new');
  await p.click('.sqe-apply');
  checks['an end before the start is refused with a clear message on the end field'] =
    (await fieldErr('end')) === 'A new line must end after it starts.' && (await rows()).every((r) => r.indexOf('Brand new') < 0);
  await p.fill('.sqe-new-line .sqe-end', '00:01:30,000');
  await p.click('.sqe-apply');
  checks['an end past the runtime is refused'] = (await fieldErr('end')) === 'A new line must not end after the end of the video.';
  await p.fill('.sqe-new-line .sqe-end', '00:00:02,500');
  await p.keyboard.press('Control+Enter');
  const added1 = await rows();
  checks['Apply stages the new line in the changes list'] = !(await p.$('.sqe-new-line')) &&
    added1.some((r) => r === 'New · 00:00:00,100 → 00:00:02,500|Brand new|staged');
  checks['a staged new line shows in the player\'s text track'] = (await trackTexts()).split('|').indexOf('Brand new') >= 0;
  await p.click('.sqe-change-staged');
  checks['clicking a staged new line reopens it in the form'] = (await p.inputValue('.sqe-new-text')) === 'Brand new' &&
    (await field('start')) === '00:00:00,100';
  if (process.env.SQE_SHOTS) { // optional screenshots of the form, wide and at phone width
    await p.screenshot({ path: process.env.SQE_SHOTS + '/fake-3f-form.png' });
    await p.setViewportSize({ width: 390, height: 844 });
    await p.screenshot({ path: process.env.SQE_SHOTS + '/fake-3f-form-phone.png' });
    await p.setViewportSize({ width: 1280, height: 720 });
  }
  await p.fill('.sqe-new-text', 'Brand new line');
  await p.click('.sqe-apply');
  const added2 = (await rows()).filter((r) => r.indexOf('New') === 0);
  checks['changing it updates the same row'] = added2.length === 1 && added2[0].indexOf('|Brand new line|') > 0;
  await p.click('.sqe-add');
  await p.click('.sqe-cancel');
  checks['Cancel goes back to the lines'] = !(await p.$('.sqe-new-line')) && (await p.$$('.sqe-cue')).length > 0;
  await p.click('.sqe-save');
  await p.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Saved') === 0, null, { timeout: 5000 });
  const lastSent = await p.evaluate(() => window.fakeEditsSent[window.fakeEditsSent.length - 1]);
  const fileNow = await p.evaluate(() => JSON.parse(sessionStorage.getItem('fake-file')).cues.map((c) => c.index + ':' + c.text).join('|'));
  checks['later saves do not keep another restore point'] = lastSent.keepRestorePoint === false;
  checks['Save sends the new line with its clientId and times'] = lastSent.edits.length === 1 && lastSent.edits[0].type === 'add' &&
    lastSent.edits[0].startMs === 100 && lastSent.edits[0].endMs === 2500 && typeof lastSent.edits[0].clientId === 'string';
  checks['the saved line lands in time order and the row shows its new number'] =
    fileNow === '1:Hello again|2:Brand new line|3:Two (changed on disk)' &&
    (await rows()).some((r) => r.indexOf('Line 2 (new)') === 0 && r.endsWith('|saved'));
  info.fileNow = fileNow;

  // 3g: at phone width the form fits, and every button is a 44 px touch target.
  await p.setViewportSize({ width: 390, height: 844 });
  await p.click('.sqe-add');
  const phone = await p.evaluate(() => {
    const panel = document.querySelector('.sqe-panel');
    const small = Array.from(panel.querySelectorAll('button')).filter((b) => b.getClientRects().length > 0)
      .filter((b) => b.getBoundingClientRect().height < 44).map((b) => b.className);
    const rows = Array.from(panel.querySelectorAll('.sqe-time-row')).map((r) =>
      new Set(Array.from(r.children).map((c) => Math.round(c.getBoundingClientRect().top))).size);
    return { small, rows, overflow: panel.scrollWidth - panel.clientWidth };
  });
  checks['phone width: buttons are at least 44 px high, time rows fit, no sideways scroll'] =
    phone.small.length === 0 && phone.rows.join() === '1,1' && phone.overflow <= 0;
  info.phone = phone;
  await p.click('.sqe-cancel');
  await p.setViewportSize({ width: 1280, height: 720 });

  // Revert to original: puts back the file as this tab first loaded it, drops every change, reloads the subtitles.
  const revertShown = () => p.$eval('.sqe-revert', (e) => !e.hidden);
  checks['Revert to original is offered once the file differs from the original'] = await revertShown();
  await p.fill('.sqe-cue-text', 'Throwaway');
  await p.click('.sqe-apply');
  await p.click('.sqe-revert');
  await p.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Reverted to the original') === 0, null, { timeout: 12000 });
  const reverted = await p.evaluate(() => ({ file: JSON.parse(sessionStorage.getItem('fake-file')), sent: window.fakeRestoreSent }));
  checks['Revert asks to restore the first hash of the session'] = reverted.sent.targetHash === 'h0' && reverted.sent.baseHash === lastSent.baseHash.replace(/^h(\d+)$/, (m, n) => 'h' + (Number(n) + 1));
  checks['Revert puts the original lines back in the file'] = reverted.file.hash === 'h0' &&
    reverted.file.cues.map((c) => c.text).join('|') === 'Hello there|Two';
  checks['Revert drops every change, saved or not, and hides itself'] = (await rows()).length === 0 &&
    (await p.$eval('.sqe-changes', (e) => e.hidden)) && !(await revertShown());
  // The player fetches the track again on its own a moment after it is switched back on.
  await p.waitForFunction(() => Array.from(Array.from(document.querySelector('video').textTracks).find((t) => t.label === 'manualTrack').cues)
    .map((c) => c.text.replace(/\u200e/g, '')).join('|') === 'Hello there|Two', null, { timeout: 3000 }).catch(() => null);
  const revertedTrack = (await trackTexts()).replace(/\u200e/g, '');
  checks['after Revert the player shows the original lines'] = revertedTrack === 'Hello there|Two';
  info.revertedTrack = revertedTrack;

  await p.focus('.sqe-cue-text');
  await p.keyboard.press('Escape');
  checks['Esc closed the panel'] = !(await p.$('.sqe-panel'));
  // The custom subtitle renderer (a jellyfin-web user setting): no text track, jellyfin-web's own element instead.
  const q = await b.newPage();
  await q.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  q.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
  q.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  await q.goto(ORIGIN + '/web/index.html?custom=1');
  await q.waitForSelector('.sqe-edit-button', { timeout: 8000 });
  await q.evaluate(() => { document.querySelector('video').currentTime = 0.5; });
  await q.click('.sqe-edit-button');
  await q.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  const shown = () => q.evaluate(() => { const e = document.querySelector('.videoSubtitlesInner'); return e && !e.classList.contains('hide') ? e.textContent : null; });
  const tick = () => q.evaluate(() => document.querySelector('video').dispatchEvent(new Event('timeupdate')));
  const qStatus = () => q.$eval('.sqe-status', (e) => e.textContent);
  checks['custom renderer: the button and panel work without a text track'] = (await q.inputValue('.sqe-cue-text')) === 'Hello there';
  await q.fill('.sqe-cue-text', 'Hello custom');
  await q.click('.sqe-apply');
  const c1 = await shown(); await tick(); const c2 = await shown();
  checks['custom renderer: a staged edit shows on screen, also after the player redraws'] = c1 === 'Hello custom' && c2 === 'Hello custom';
  await q.click('.sqe-undo');
  checks['custom renderer: undo puts the player\'s text back'] = (await shown()) === 'Hello there';
  await q.fill('.sqe-cue-text', 'Hello saved');
  await q.click('.sqe-apply');
  await q.click('.sqe-save');
  await q.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Saved') === 0, null, { timeout: 5000 });
  await tick();
  checks['custom renderer: after Save the saved text stays on screen'] = (await shown()) === 'Hello saved';
  await q.click('.sqe-reload');
  await q.waitForFunction(() => /Subtitles reloaded|Could not/.test(document.querySelector('.sqe-status').textContent), null, { timeout: 12000 });
  const reloadStatus = await qStatus();
  checks['custom renderer: Reload subtitles reports success'] = reloadStatus.indexOf('Subtitles reloaded') === 0;
  checks['custom renderer: after Reload the player shows the file\'s new text'] =
    (await q.evaluate(() => window.fakeEvents[0].text)) === 'Hello saved' && (await shown()) === 'Hello saved';
  await q.evaluate(() => { document.querySelector('video').currentTime = 2.5; });
  await q.click('.sqe-add');
  await q.fill('.sqe-new-line .sqe-start', '00:00:02,200');
  await q.fill('.sqe-new-line .sqe-end', '00:00:02,800');
  await q.fill('.sqe-new-text', 'Gap line');
  await q.click('.sqe-apply');
  await tick();
  checks['custom renderer: a staged new line shows on screen at its time'] = (await shown()) === 'Gap line';
  info.custom = { c1, c2, reloadStatus };
  await q.close();

  checks['no errors in the console'] = logs.every((l) => !/^(error|pageerror)/.test(l));

  Object.keys(checks).forEach((k) => console.log((checks[k] ? 'PASS ' : 'FAIL ') + k));
  if (!Object.values(checks).every(Boolean)) {
    console.log(JSON.stringify({ info, logs }, null, 1));
    process.exitCode = 1;
  }
  await b.close();
})().catch((e) => { console.error('FAIL', e.message.split('\n')[0]); console.error(JSON.stringify(global.sqeLogs || [])); process.exit(1); });
