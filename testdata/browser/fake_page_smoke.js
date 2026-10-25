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
const JASSUB = location.search.indexOf('jassub=1') >= 0; // ASS drawn by JASSUB on a canvas; the text track stays disabled
function showJassub() {
  if (!document.querySelector('.JASSUB')) {
    const box = document.createElement('div'); box.className = 'JASSUB'; box.appendChild(document.createElement('canvas'));
    document.querySelector('.videoPlayerContainer').appendChild(box); window.fakeJassubShown = (window.fakeJassubShown || 0) + 1;
  }
}
const ROLE = (location.search.match(/role=(\\w+)/) || [])[1] || 'admin'; // admin, submitter or none
const EMBEDDED = location.search.indexOf('embedded=1') >= 0; // the track is inside the video: the panel offers a copy
const EXISTING = location.search.indexOf('existing=1') >= 0; // ...and a file of the copy's name is already there
window.fakeParams = {};
window.fakeExtractSent = [];
window.fakeSubs = [];
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
const FORMAT = (location.search.match(/format=(\\w+)/) || [])[1] || 'srt'; // srt or ass
const initial = FORMAT === 'ass'
  ? { hash: 'h0', n: 0, cues: [{ index: 1, start: 0, end: 2000, text: '{\\\\an8}Hello there', style: 'Sign', readOnly: false },
    { index: 2, start: 3000, end: 4000, text: '{\\\\p1}m 0 0 l 1 1{\\\\p0}', style: 'Default', readOnly: true }] }
  : { hash: 'h0', n: 0, cues: [{ index: 1, start: 0, end: 2000, text: 'Hello there' }, { index: 2, start: 3000, end: 4000, text: 'Two' }] };
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
    'SubtitleQuickEdit/Items/abc/Subtitles': () => EMBEDDED
      ? [{ streamIndex: 0, external: false, format: 'subrip', editable: false, extractable: true, reason: 'Embedded subtitle tracks cannot be edited.' }]
        .concat(window.fakePlaying ? [{ streamIndex: 3, external: true, format: 'srt', editable: true, extractable: false }] : [])
      : [{ streamIndex: 0, external: true, format: FORMAT, editable: true, extractable: false }],
    'SubtitleQuickEdit/Items/abc/Subtitles/0/Extract': () => EXISTING
      ? Object.assign({ fileName: 'Ep.en.srt', format: 'srt', cueCount: 2, exists: true, existingHash: 'e1', existingStreamIndex: 3,
        existingIsExtracted: false, canReplace: ROLE === 'admin' }, ROLE === 'admin' ? {
        cues: [{ index: 1, start: 0, end: 2000, text: 'Hello there' }, { index: 2, start: 3000, end: 4000, text: 'Two' }],
        existingCues: [{ index: 1, start: 0, end: 2000, text: 'Hello there' }, { index: 2, start: 3000, end: 4000, text: 'Two, edited' }] } : {})
      : { fileName: 'Ep.en.srt', format: 'srt', cueCount: 2, exists: false, canReplace: ROLE === 'admin' },
    'SubtitleQuickEdit/Items/abc/Subtitles/0/Cues': () => { const f = file(); return { hash: f.hash, encoding: 'utf-8', format: FORMAT, timePrecisionMs: FORMAT === 'ass' ? 10 : 1, cues: f.cues }; },
    // Jellyfin leaves null fields out: a non-admin's answer has no pendingReviews.
    'SubtitleQuickEdit/Me': () => ROLE === 'admin' ? { isAdmin: true, canEdit: true, canSubmit: false, submissionsOn: true, pendingReviews: 2, maxNoteLength: 500 }
      : { isAdmin: false, canEdit: false, canSubmit: ROLE === 'submitter', submissionsOn: true, maxNoteLength: 500 },
    'SubtitleQuickEdit/Submissions/Mine': () => window.fakeSubs,
  };
  window.ApiClient = { getCurrentUserId: () => 'u1', getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: true } }),
    isLoggedIn: () => true, accessToken: () => 't', deviceId: () => 'd', getUrl: (p, q) => { if (q) { window.fakeParams[p] = q; } return p; },
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
      if (o.url === 'SubtitleQuickEdit/Items/abc/Subtitles/0/Extract') {
        window.fakeExtractSent.push(body);
        if (EXISTING && !body.replace) { return refused(409, { code: 'FileExists', message: 'exists', currentHash: 'e1' }); }
        return Promise.resolve({ fileName: 'Ep.en.srt', hash: 'x1', replaced: Boolean(body.replace), streamIndex: 3, refreshPending: false });
      }
      if (o.url === 'Sessions/s1/Playing') {
        window.fakePlaying = window.fakeParams['Sessions/s1/Playing'];
        // Like jellyfin-web with the player open: the same <video> gets the new source (Sessions still shows the old track).
        setTimeout(() => document.querySelector('video').dispatchEvent(new Event('emptied')), 200);
        return o.dataType === 'json' ? Promise.reject(new SyntaxError('Unexpected end of JSON input')) : Promise.resolve(undefined);
      }
      if (o.url === 'SubtitleQuickEdit/Items/abc/Subtitles/0/Submissions') {
        const f = file();
        if (body.baseHash !== f.hash) { return refused(409, { code: 'FileChanged', message: 'changed', currentHash: f.hash }); }
        const sub = { id: 's' + (window.fakeSubs.length + 1), itemId: 'abc', streamIndex: 0, created: new Date().toISOString(), state: 'Pending',
          note: body.note || undefined, baseHash: body.baseHash, userName: 'u',
          edits: body.edits.map((e) => Object.assign({ outcome: 'Pending', cueStart: e.cueIndex ? f.cues[e.cueIndex - 1].start : undefined }, e)) };
        window.fakeSubs.push(sub);
        return Promise.resolve(copy(sub));
      }
      const withdraw = /^SubtitleQuickEdit\\/Submissions\\/(\\w+)\\/Withdraw$/.exec(o.url);
      if (withdraw) {
        const sub = window.fakeSubs.find((x) => x.id === withdraw[1]);
        if (!sub || sub.state !== 'Pending') { return refused(409, { code: 'NotPending', message: 'no longer pending' }); }
        sub.state = 'Withdrawn';
        return Promise.resolve(copy(sub));
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
        if (JASSUB) {
          if (body.Arguments.Index === '-1') { const box = document.querySelector('.JASSUB'); if (box) { box.remove(); } } else { setTimeout(showJassub, 300); }
        } else if (CUSTOM) {
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
  if (JASSUB) { const track = v.addTextTrack('subtitles', 'manualTrack', 'und'); track.mode = 'disabled'; showJassub(); }
  else if (CUSTOM) { showCustom(); } else { const track = v.addTextTrack('subtitles', 'manualTrack', 'und'); track.mode = 'showing'; fillTrack(track); }
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
  await p.waitForFunction(() => { const a = document.querySelector('.sqe-pending'); return a && !a.hidden; }, null, { timeout: 3000 }).catch(() => null);
  checks['admin: the panel links to the 2 submissions waiting for review'] =
    (await p.$eval('.sqe-pending', (a) => !a.hidden && a.textContent)) === '2 submissions are waiting for your review';
  checks['admin: no Submit button or note box'] = !(await p.$('.sqe-submit')) && !(await p.$('.sqe-note'));
  checks['the text box has focus'] = (await p.evaluate(() => document.activeElement.className)) === 'sqe-cue-text';
  checks['srt: no "Show style tags" toggle'] = await p.$eval('.sqe-tags', (e) => e.hidden && e.offsetParent === null);
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

  // Undo on a saved change stages the old text again; on a line changed again since, it says why it cannot.
  const undoSavedOn = (text) => p.evaluate((t) => Array.from(document.querySelectorAll('.sqe-change-saved'))
    .find((r) => r.querySelector('.sqe-change-new').textContent === t).querySelector('.sqe-undo-saved').click(), text);
  await undoSavedOn('Hello draft');
  checks['Undo on a saved change to a line that changed since is refused with a reason'] =
    (await status()).indexOf('changed again') > 0 && !(await rows()).some((r) => r.endsWith('|staged'));
  await undoSavedOn('Hello again');
  checks['Undo on a saved change stages the old text and shows it in the player'] =
    (await rows()).some((r) => r.indexOf('|Hello draft|staged') > 0) && (await trackTexts()).split('|')[0] === 'Hello draft';

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
  // Subtitles off and on again by the player itself (no menu click, no DOM change): the button follows the text track.
  const setMode = (m) => p.evaluate((mode) => { Array.from(document.querySelector('video').textTracks).find((t) => t.label === 'manualTrack').mode = mode; }, m);
  await setMode('disabled');
  const goneOff = await p.waitForFunction(() => !document.querySelector('.sqe-edit-button'), null, { timeout: 3000 }).then(() => true, () => false);
  await setMode('showing');
  const backOn = await p.waitForSelector('.sqe-edit-button', { timeout: 3000 }).then(() => true, () => false);
  checks['the button goes when the text track is switched off and comes back when it is shown again'] = goneOff && backOn;
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

  // A user who may only submit: same editing, but Submit for review instead of Save, and their own submissions.
  const r = await b.newPage();
  await r.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  r.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
  r.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  r.on('dialog', (d) => d.accept());
  await r.goto(ORIGIN + '/web/index.html?role=submitter');
  await r.waitForSelector('.sqe-edit-button', { timeout: 8000 });
  checks['submitter: the button says Suggest a subtitle fix'] = (await r.getAttribute('.sqe-edit-button', 'title')) === 'Suggest a subtitle fix';
  await r.click('.sqe-edit-button');
  await r.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  checks['submitter: Submit for review and a note box, no Save, Reload, Revert or pending link'] =
    (await r.$eval('.sqe-title', (e) => e.textContent)) === 'Suggest a subtitle fix' && Boolean(await r.$('.sqe-submit')) &&
    Boolean(await r.$('.sqe-note')) && !(await r.$('.sqe-save')) && !(await r.$('.sqe-reload')) && !(await r.$('.sqe-revert')) &&
    (await r.$eval('.sqe-pending', (a) => a.hidden));
  const rTrack = () => r.evaluate(() => Array.from(Array.from(document.querySelector('video').textTracks).find((t) => t.label === 'manualTrack').cues)
    .map((c) => c.text.replace(/[\u200e\u200f]/g, '')).join('|'));
  await r.fill('.sqe-cue-text', 'Hello suggested');
  await r.click('.sqe-apply');
  checks['submitter: a staged change shows in the player'] = (await rTrack()).split('|')[0] === 'Hello suggested';
  await r.fill('.sqe-note', 'Typo in the first line');
  await r.keyboard.press('Control+s');
  await r.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Submitted') === 0, null, { timeout: 5000 });
  const subs = await r.evaluate(() => window.fakeSubs);
  checks['submitter: Ctrl+S submits the change with the note and the base hash'] = subs.length === 1 && subs[0].note === 'Typo in the first line' &&
    subs[0].baseHash === 'h0' && subs[0].edits.length === 1 && subs[0].edits[0].newText === 'Hello suggested';
  checks['submitter: the file is not changed and the player shows its text again'] =
    (await r.evaluate(() => file().cues[0].text)) === 'Hello there' && (await rTrack()).split('|')[0] === 'Hello there';
  checks['submitter: the submitted change leaves the list and the note box is cleared'] =
    (await r.$$('.sqe-change')).length === 0 && (await r.inputValue('.sqe-note')) === '';
  await r.waitForSelector('.sqe-mine-item', { timeout: 3000 });
  checks['submitter: the submission is listed as waiting for review, with a Withdraw button'] =
    (await r.$eval('.sqe-mine-item .sqe-badge', (e) => e.textContent)) === 'waiting for review' && Boolean(await r.$('.sqe-withdraw')) &&
    (await r.$eval('.sqe-mine-item', (e) => e.textContent.indexOf('Hello suggested') >= 0 && e.textContent.indexOf('Typo in the first line') >= 0));
  await r.click('.sqe-withdraw');
  await r.waitForFunction(() => { const e = document.querySelector('.sqe-mine-item .sqe-badge'); return e && e.textContent === 'withdrawn'; }, null, { timeout: 3000 });
  checks['submitter: Withdraw takes it back and the button goes away'] = !(await r.$('.sqe-withdraw')) &&
    (await r.evaluate(() => window.fakeSubs[0].state)) === 'Withdrawn';
  await r.evaluate(() => window.fakeExternalChange());
  await r.fill('.sqe-cue-text', 'Hello late');
  await r.click('.sqe-apply');
  await r.click('.sqe-submit');
  await r.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('The file changed') === 0, null, { timeout: 5000 });
  checks['submitter: a file changed on disk submits nothing and keeps the matching change staged'] =
    (await r.evaluate(() => window.fakeSubs.length)) === 1 && (await r.$$eval(".sqe-change .sqe-badge", (e) => e.map((x) => x.textContent))).join() === "staged";
  await r.close();
  const n = await b.newPage();
  await n.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  n.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  await n.goto(ORIGIN + '/web/index.html?role=none');
  await n.waitForTimeout(2500);
  checks['no rights: no button, and the client knows it'] = !(await n.$('.sqe-edit-button')) &&
    (await n.evaluate(() => window.SubtitleQuickEdit.debugState().mode)) === null;
  await n.close();

  // 5d: an ASS file: the "Show style tags" toggle, the style name, a read-only drawing, the 10 ms hint.
  const a = await b.newPage();
  await a.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  a.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  a.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
  a.on('dialog', (d) => d.accept());
  await a.goto(ORIGIN + '/web/index.html?format=ass');
  await a.waitForSelector('.sqe-edit-button', { timeout: 8000 });
  await a.click('.sqe-edit-button');
  await a.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  checks['ass: the "Show style tags" toggle is shown and off'] =
    await a.$eval('.sqe-tags', (e) => !e.hidden && e.offsetParent !== null && !e.querySelector('input').checked);
  checks['ass: tags hidden, the box shows the text without its leading override block'] = (await a.inputValue('.sqe-cue-text')) === 'Hello there';
  checks['ass: the line shows its style name and a styled badge'] =
    (await a.$eval('.sqe-cue-style', (e) => e.textContent)) === 'Sign' && Boolean(await a.$('.sqe-badge-styled'));
  await a.fill('.sqe-cue-text', 'Hello world');
  await a.keyboard.press('Control+Enter');
  checks['ass: the player overlay shows the staged text without tags'] = await a.evaluate(() => Array.from(Array.from(document.querySelector('video').textTracks)
    .find((t) => t.label === 'manualTrack').cues).some((c) => c.text.replace(/\u200e/g, '') === 'Hello world'));
  await a.keyboard.press('Control+s');
  await a.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Saved') === 0, null, { timeout: 5000 });
  const assSent = await a.evaluate(() => window.fakeEditsSent);
  checks['ass: Save puts the hidden override block back in front of the new text'] = assSent.length === 1 &&
    assSent[0].edits[0].oldText === '{\\an8}Hello there' && assSent[0].edits[0].newText === '{\\an8}Hello world';
  await a.click('.sqe-tags-toggle');
  checks['ass: tags shown, the box shows the full text'] = (await a.inputValue('.sqe-cue-text')) === '{\\an8}Hello world';
  await a.click('.sqe-next');
  checks['ass: a drawing is read-only, with a hint'] = await a.$eval('.sqe-cue', (e) => e.querySelector('textarea').readOnly &&
    e.textContent.indexOf('drawing') >= 0);
  await a.click('.sqe-add');
  await a.waitForSelector('.sqe-new-line', { timeout: 3000 });
  checks['ass: Add line says the file keeps times to 10 ms'] = (await a.$eval('.sqe-new-line', (e) => e.textContent)).indexOf('keeps times to 10 ms') >= 0;
  await a.reload();
  await a.waitForSelector('.sqe-edit-button', { timeout: 8000 });
  await a.click('.sqe-edit-button');
  await a.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  checks['ass: the toggle is remembered after a page reload'] = await a.$eval('.sqe-tags-toggle', (e) => e.checked);
  await a.close();

  // 5d: ASS drawn by JASSUB (no text track showing): the editor still opens, and Reload waits for the player.
  const j = await b.newPage();
  await j.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
  j.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
  j.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
  j.on('dialog', (d) => d.accept());
  await j.goto(ORIGIN + '/web/index.html?format=ass&jassub=1');
  await j.waitForSelector('.sqe-edit-button', { timeout: 8000 });
  await j.click('.sqe-edit-button');
  await j.waitForSelector('.sqe-cue-text', { timeout: 5000 });
  checks['jassub: the editor opens for ASS drawn on a canvas'] = (await j.inputValue('.sqe-cue-text')) === 'Hello there';
  await j.fill('.sqe-cue-text', 'Hello canvas');
  await j.keyboard.press('Control+Enter');
  checks['jassub: the staged note says the player keeps the old text until Save and Reload'] =
    (await j.$eval('.sqe-status', (e) => e.textContent)).indexOf('keeps showing the old text for now: Save, then Reload subtitles') >= 0;
  await j.keyboard.press('Control+s');
  await j.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Saved') === 0, null, { timeout: 5000 });
  checks['jassub: after Save the note points to Reload subtitles'] =
    (await j.$eval('.sqe-status', (e) => e.textContent)).indexOf('Reload subtitles shows it in the player.') > 0;
  await j.click('.sqe-reload');
  await j.waitForFunction(() => document.querySelector('.sqe-status').textContent.indexOf('Subtitles reloaded') === 0, null, { timeout: 12000 })
    .catch(() => null);
  checks['jassub: Reload switches the subtitle off and on, and finishes'] =
    (await j.$eval('.sqe-status', (e) => e.textContent)).indexOf('Subtitles reloaded') === 0 &&
    (await j.evaluate(() => window.fakeJassubShown)) === 2 && Boolean(await j.$('.JASSUB'));
  await j.close();

  // 6c: a subtitle track inside the video. The panel offers an editable copy, then restarts playback on it.
  async function embeddedPage(query) {
    const page = await b.newPage();
    await page.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
    page.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
    page.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
    await page.goto(ORIGIN + '/web/index.html?embedded=1' + query);
    await page.waitForSelector('.sqe-edit-button', { timeout: 8000 });
    return page;
  }

  const e1 = await embeddedPage('');
  checks['embedded: the button offers to make the subtitle editable'] =
    (await e1.$eval('.sqe-edit-button', (x) => x.getAttribute('aria-label'))) === 'Make this subtitle editable';
  await e1.click('.sqe-edit-button');
  await e1.waitForSelector('.sqe-make', { timeout: 5000 });
  checks['embedded: the panel names the copy and its lines, and edits nothing yet'] =
    (await e1.$eval('.sqe-title', (x) => x.textContent)) === 'Make this subtitle editable' &&
    (await e1.$eval('.sqe-extract-what', (x) => x.textContent)).indexOf('\u201cEp.en.srt\u201d (2 lines)') >= 0 &&
    !(await e1.$('.sqe-cue-text')) && await e1.$eval('.sqe-actions', (x) => x.hidden && x.offsetParent === null) &&
    await e1.$eval('.sqe-nav', (x) => x.offsetParent === null);
  await e1.keyboard.press('Control+s');
  checks['embedded: Ctrl+S does not make a copy'] = (await e1.evaluate(() => window.fakeExtractSent.length)) === 0;
  await e1.click('.sqe-make');
  await e1.waitForFunction(() => window.fakePlaying, null, { timeout: 5000 }).catch(() => null);
  const playing = await e1.evaluate(() => window.fakePlaying);
  checks['embedded: Make sends one plain request'] = JSON.stringify(await e1.evaluate(() => window.fakeExtractSent)) === '[{}]';
  checks['embedded: then playback restarts at the same place on the new track'] = Boolean(playing) && playing.playCommand === 'PlayNow' &&
    playing.itemIds === 'abc' && playing.subtitleStreamIndex === 3 && playing.startPositionTicks === 0;
  await e1.waitForSelector('.sqe-toast', { timeout: 3000 }).catch(() => null);
  checks['embedded: a message outside the panel says what happened'] =
    ((await e1.$eval('.sqe-toast', (x) => x.textContent).catch(() => '')) || '').indexOf('Made \u201cEp.en.srt\u201d.') === 0;
  const onCopy = await e1.waitForFunction(() => { const d = window.SubtitleQuickEdit.debugState(); return d.target && d.target.streamIndex === 3; },
    null, { timeout: 5000 }).then(() => true, () => false);
  checks['embedded: after the restart in the same <video>, the button edits the copy'] = onCopy && !(await e1.$('.sqe-panel')) &&
    (await e1.$eval('.sqe-edit-button', (x) => x.getAttribute('aria-label')).catch(() => '')) === 'Edit subtitle line';
  await e1.close();

  const e2 = await embeddedPage('&existing=1&role=submitter');
  await e2.click('.sqe-edit-button');
  await e2.waitForSelector('.sqe-extract-what', { timeout: 5000 });
  checks['embedded, name taken, submitter: no Replace, a switch to that file'] =
    (await e2.$eval('.sqe-extract-what', (x) => x.textContent)).indexOf('only an admin can replace it') >= 0 &&
    !(await e2.$('.sqe-replace')) && Boolean(await e2.$('.sqe-switch')) && !(await e2.$('.sqe-compare'));
  await e2.close();

  const e3 = await embeddedPage('&existing=1');
  await e3.click('.sqe-edit-button');
  await e3.waitForSelector('.sqe-compare-table', { timeout: 5000 });
  checks['embedded, name taken, admin: the two files side by side, the differing line marked'] =
    (await e3.$eval('.sqe-compare-summary', (x) => x.textContent)) === '1 line differs.' &&
    (await e3.$$eval('.sqe-compare-table tr.sqe-differs td', (els) => els.map((x) => x.textContent).join('|'))) === '00:00:03,000Two, edited|00:00:03,000Two';
  await e3.click('.sqe-replace');
  checks['embedded, admin: Replace asks for a second click'] = (await e3.evaluate(() => window.fakeExtractSent.length)) === 0 &&
    (await e3.$eval('.sqe-replace', (x) => x.textContent)) === 'Click again to replace';
  await e3.click('.sqe-replace');
  await e3.waitForFunction(() => window.fakePlaying, null, { timeout: 5000 }).catch(() => null);
  checks['embedded, admin: Replace sends the hash of the file compared'] =
    JSON.stringify(await e3.evaluate(() => window.fakeExtractSent)) === '[{"replace":true,"expectedExistingHash":"e1"}]' &&
    Boolean(await e3.evaluate(() => window.fakePlaying));
  await e3.close();

  checks['no errors in the console'] = logs.every((l) => !/^(error|pageerror)/.test(l));

  Object.keys(checks).forEach((k) => console.log((checks[k] ? 'PASS ' : 'FAIL ') + k));
  if (!Object.values(checks).every(Boolean)) {
    console.log(JSON.stringify({ info, logs }, null, 1));
    process.exitCode = 1;
  }
  await b.close();
})().catch((e) => { console.error('FAIL', e.message.split('\n')[0]); console.error(JSON.stringify(global.sqeLogs || [])); process.exit(1); });
