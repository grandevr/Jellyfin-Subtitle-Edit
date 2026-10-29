// Browser smoke test of the plugin settings page (Configuration/configPage.html) with a mock ApiClient and Dashboard.
// Needs no Jellyfin server, only Playwright for Node and its Chromium:
//   node tests/browser/config_page_smoke.js   (with Playwright on NODE_PATH)
const { chromium } = require('playwright');
const fs = require('fs');
const pagePath = require('path').join(__dirname, '..', '..', 'src', 'Jellyfin.Plugin.SubtitleQuickEdit', 'Configuration', 'configPage.html');
const body = fs.readFileSync(pagePath, 'utf8').replace(/^[\s\S]*<body>/, '').replace(/<\/body>[\s\S]*$/, '');
const ORIGIN = 'http://sqe-fake.test';

// The mock server: settings, users and submissions. SUBS_ON in the query turns submissions on.
const mock = `
window.sent = [];
// jellyfin-web's createElement polyfill takes a type-extension string, not an options object (it calls toLowerCase).
const realCreate = document.createElement.bind(document);
document.createElement = function (tag, ext) { if (ext !== undefined) { ext.toLowerCase(); } return realCreate(tag); };
window.config = { AllowUserSubmissions: location.search.indexOf('on=1') >= 0, UserEdits: 'Off', UserEditUserIds: ['u2'],
  MaxPendingSubmissionsPerUser: 50, BackupMode: 'OriginalOnly', BackupKeepLastSaves: 5 };
const edit = (i, oldText, newText) => ({ type: 'edit', cueIndex: i, cueStart: i * 1000, oldText: oldText, newText: newText, outcome: 'Pending' });
window.subs = [
  { id: 'a1', userName: 'testuser', itemId: 'i1', itemName: 'Show S01E01 Pilot', streamIndex: 2, streamLabel: 'eng', created: '2026-10-25T01:00:00Z',
    state: 'Pending', note: '<img src=x onerror="window.pwned=1">', edits: [edit(1, 'One', 'One fixed'), edit(2, 'Two', 'Two fixed')] },
  { id: 'b2', userName: 'other', itemId: 'i2', itemName: 'Movie', streamIndex: 3, created: '2026-10-25T00:00:00Z', state: 'Pending',
    edits: [{ type: 'add', clientId: 'c', startMs: 5000, endMs: 6000, text: 'New line', outcome: 'Pending' }] },
  { id: 'c3', userName: 'testuser', itemId: 'i1', itemName: 'Show S01E01 Pilot', streamIndex: 2, created: '2026-10-24T00:00:00Z', state: 'Pending',
    edits: [edit(4, 'Four', 'Four fixed')] },
];
window.copies = location.search.indexOf('copies=0') >= 0 ? [] : [
  { id: 'x1', itemId: 'i1', itemName: 'Show S01E01 Pilot', sourceStreamIndex: 2, fileName: 'Pilot.en.srt', streamIndex: 5, exists: true,
    edited: true, replaced: false, userId: 'u2', userName: '<b>testuser</b>', created: '2026-10-25T02:00:00Z' },
  { id: 'x2', itemId: 'i1', itemName: 'Show S01E01 Pilot', sourceStreamIndex: -1, fileName: 'Pilot.pl.srt', streamIndex: 6, exists: true,
    edited: false, replaced: false, userId: 'u1', userName: 'admin', created: '2026-10-29T20:00:00Z' },
];
function copy(x) { return JSON.parse(JSON.stringify(x)); }
window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() { window.savedOk = true; } };
window.ApiClient = {
  getPluginConfiguration: () => Promise.resolve(copy(window.config)),
  updatePluginConfiguration: (id, c) => { window.config = copy(c); return Promise.resolve({}); },
  getUsers: () => Promise.resolve([{ Id: 'u1', Name: 'testadmin', Policy: { IsAdministrator: true } },
    { Id: 'u2', Name: 'testuser', Policy: { IsAdministrator: false } }, { Id: 'u3', Name: 'limiteduser', Policy: { IsAdministrator: false } }]),
  getUrl: (p, params) => p + (params ? '?' + new URLSearchParams(params) : ''),
  getJSON: (u) => {
    if (u === 'SubtitleQuickEdit/Extracted') { return Promise.resolve(copy(window.copies)); }
    const status = new URLSearchParams(u.split('?')[1] || '').get('status');
    return Promise.resolve(copy(window.subs.filter((s) => (status === 'pending') === (s.state === 'Pending'))));
  },
  ajax: (o) => {
    if (o.type === 'DELETE') { window.sent.push({ url: o.url, type: 'DELETE' }); window.copies = []; return Promise.resolve({ fileName: 'Pilot.en.srt', backedUp: true }); }
    const b = JSON.parse(o.data); window.sent.push({ url: o.url, body: b });
    const m = /Submissions\\/(\\w+)\\/(Approve|Reject)$/.exec(o.url); const s = window.subs.find((x) => x.id === m[1]);
    if (m[2] === 'Reject') { s.state = 'Rejected'; s.reason = b.reason; s.decidedBy = 'testadmin'; return Promise.resolve(copy(s)); }
    if (s.id === 'c3') { s.edits[0].outcome = 'Conflict'; s.edits[0].message = 'The line changed.'; return Promise.resolve({ submission: copy(s), applied: false }); }
    s.state = b.edits ? 'PartlyApproved' : 'Approved';
    s.edits.forEach((e, i) => { e.outcome = !b.edits || b.edits.indexOf(i) >= 0 ? 'Applied' : 'NotChosen'; });
    return Promise.resolve({ submission: copy(s), applied: true, hash: 'h' });
  },
};
`;
const html = '<html><head><meta charset="utf-8"></head><body><script>' + mock + '</script>' + body +
  '<script>document.querySelector("#SubtitleQuickEditConfigPage").dispatchEvent(new Event("pageshow"));</script></body></html>';

(async () => {
  const b = await chromium.launch();
  const logs = [];
  const checks = {};
  async function open(query) {
    const p = await b.newPage();
    await p.route(ORIGIN + '/**', (route) => route.fulfill({ status: 200, contentType: 'text/html', body: html }));
    p.on('console', (m) => logs.push(m.type() + ': ' + m.text()));
    p.on('pageerror', (e) => logs.push('pageerror: ' + e.message));
    await p.goto(ORIGIN + '/web/config.html' + query);
    await p.waitForSelector('#SqeReviewList .sqe-sub, #SqeReviewEmpty:not([hidden])', { timeout: 5000 });
    return p;
  }

  // Submissions off: the list shows, but nothing can be decided.
  let p = await open('');
  checks['user list hidden while user edits are Off'] = await p.$eval('#UserEditUsers', (e) => e.hidden);
  await p.selectOption('#UserEdits', 'SelectedUsers');
  const users = await p.$$eval('.sqe-user-edit', (e) => e.map((x) => x.getAttribute('data-user-id') + ':' + x.checked));
  checks['Selected users lists the non-admins, with the saved ones ticked'] = !(await p.$eval('#UserEditUsers', (e) => e.hidden)) &&
    users.join() === 'u2:true,u3:false';
  await p.click('.sqe-user-edit[data-user-id="u3"]');
  await p.click('#SubtitleQuickEditConfigForm button[type=submit]');
  await p.waitForFunction(() => window.savedOk === true, null, { timeout: 3000 });
  const saved = await p.evaluate(() => window.config);
  checks['Save stores the mode and the ticked users'] = saved.UserEdits === 'SelectedUsers' && saved.UserEditUserIds.join() === 'u2,u3';
  checks['submissions off: the pending ones are listed with the off notice and no buttons'] =
    (await p.$$('#SqeReviewList .sqe-sub')).length === 3 && !(await p.$eval('#SqeReviewOff', (e) => e.hidden)) && !(await p.$('.sqe-approve'));
  checks['a note is shown as text, never as HTML'] = (await p.evaluate(() => !window.pwned && !document.querySelector('#SqeReviewList img'))) &&
    (await p.$eval('.sqe-sub-note', (e) => e.textContent)).indexOf('<img') >= 0;
  await p.close();

  // Submissions on: approve some, approve all, conflicts, reject.
  p = await open('?on=1');
  checks['the title counts the waiting submissions'] = (await p.$eval('#SqeReviewTitle', (e) => e.textContent)) === 'Submissions (3 waiting)';
  checks['each submission shows its item, stream, user, and changes'] =
    (await p.$eval('[data-submission-id="a1"]', (e) => e.textContent)).indexOf('Show S01E01 Pilot · eng') >= 0 &&
    (await p.$eval('[data-submission-id="a1"]', (e) => e.textContent)).indexOf('testuser') >= 0 &&
    (await p.$$('[data-submission-id="a1"] .sqe-pick')).length === 2;
  await p.click('[data-submission-id="a1"] .sqe-pick[data-index="1"]');
  await p.click('[data-submission-id="a1"] .sqe-approve');
  await p.waitForSelector('[data-submission-id="a1"] .sqe-sub-result:not([hidden])');
  let last = await p.evaluate(() => window.sent[window.sent.length - 1]);
  checks['Approve selected sends only the ticked changes'] = last.url.endsWith('/a1/Approve') && JSON.stringify(last.body.edits) === '[0]';
  checks['a partly approved submission shows its outcomes and loses its buttons'] =
    (await p.$eval('[data-submission-id="a1"] .sqe-sub-result', (e) => e.textContent)).indexOf('Partly approved') === 0 &&
    (await p.$$eval('[data-submission-id="a1"] .sqe-outcome', (e) => e.map((x) => x.textContent))).join() === 'applied,not chosen' &&
    !(await p.$('[data-submission-id="a1"] .sqe-approve'));
  await p.click('[data-submission-id="b2"] .sqe-approve');
  await p.waitForSelector('[data-submission-id="b2"] .sqe-sub-result:not([hidden])');
  last = await p.evaluate(() => window.sent[window.sent.length - 1]);
  checks['Approve with everything ticked sends edits null (all)'] = last.body.edits === null &&
    (await p.$eval('[data-submission-id="b2"] .sqe-sub-result', (e) => e.textContent)).indexOf('Approved') === 0;
  await p.click('[data-submission-id="c3"] .sqe-approve');
  await p.waitForSelector('[data-submission-id="c3"] .sqe-sub-result:not([hidden])');
  checks['when nothing fits, the conflicts show and the submission stays decidable'] =
    (await p.$eval('[data-submission-id="c3"] .sqe-sub-result', (e) => e.textContent)).indexOf('Nothing was written') === 0 &&
    (await p.$eval('[data-submission-id="c3"] .sqe-outcome', (e) => e.textContent)) === 'conflict: The line changed.' &&
    Boolean(await p.$('[data-submission-id="c3"] .sqe-reject'));
  await p.fill('[data-submission-id="c3"] .sqe-reason', 'Already fixed');
  await p.click('[data-submission-id="c3"] .sqe-reject');
  await p.waitForFunction(() => document.querySelector('[data-submission-id="c3"] .sqe-sub-result').textContent === 'Rejected.');
  last = await p.evaluate(() => window.sent[window.sent.length - 1]);
  checks['Reject sends the reason and the count drops to none'] = last.url.endsWith('/c3/Reject') && last.body.reason === 'Already fixed' &&
    (await p.$eval('#SqeReviewTitle', (e) => e.textContent)) === 'Submissions';
  await p.selectOption('#SqeReviewStatus', 'decided');
  await p.waitForFunction(() => document.querySelectorAll('#SqeReviewList .sqe-sub').length === 3);
  checks['Decided lists the three decided ones, without buttons'] = !(await p.$('.sqe-approve')) &&
    (await p.$eval('[data-submission-id="c3"]', (e) => e.textContent)).indexOf('Reason: Already fixed') >= 0;
  await p.close();

  // 6c: the editable copies made from subtitle tracks inside videos, with Delete.
  p = await open('');
  await p.waitForSelector('#SqeExtractedList .sqe-copy', { timeout: 5000 });
  checks['copies: each copy shows its item, file, who made it (as text) and whether it was edited'] =
    (await p.$eval('[data-extracted-id="x1"]', (e) => e.textContent)).indexOf('Pilot.en.srt') >= 0 &&
    (await p.$eval('[data-extracted-id="x1"] .sqe-sub-meta', (e) => e.textContent)).indexOf('<b>testuser</b>') === 0 &&
    (await p.$eval('[data-extracted-id="x1"] .sqe-sub-meta', (e) => e.textContent)).indexOf('edited since') >= 0 &&
    !(await p.$('[data-extracted-id="x1"] b'));
  checks['copies: a copy and a new track say which they are'] =
    (await p.$eval('[data-extracted-id="x1"] .sqe-sub-meta', (e) => e.textContent)).indexOf('a copy of a track inside the video') >= 0 &&
    (await p.$eval('[data-extracted-id="x2"] .sqe-sub-meta', (e) => e.textContent)).indexOf('a new track') >= 0;
  await p.click('[data-extracted-id="x1"] .sqe-delete-copy');
  checks['copies: Delete asks for a second click'] = (await p.evaluate(() => window.sent.length)) === 0 &&
    (await p.$eval('[data-extracted-id="x1"] .sqe-delete-copy', (e) => e.textContent)) === 'Click again to delete';
  await p.click('[data-extracted-id="x1"] .sqe-delete-copy');
  await p.waitForFunction(() => document.querySelector('[data-extracted-id="x1"] .sqe-sub-result').textContent.indexOf('Deleted') === 0);
  last = await p.evaluate(() => window.sent[window.sent.length - 1]);
  checks['copies: Delete sends one DELETE for that copy and says it is in the backups'] = last.type === 'DELETE' &&
    last.url === 'SubtitleQuickEdit/Extracted/x1' && !(await p.$('[data-extracted-id="x1"] .sqe-delete-copy'));
  await p.close();
  p = await open('?copies=0');
  await p.waitForSelector('#SqeExtractedEmpty:not([hidden])', { timeout: 5000 }).catch(() => null);
  checks['copies: with none, the list says so'] = !(await p.$eval('#SqeExtractedEmpty', (e) => e.hidden)) && !(await p.$('.sqe-copy'));
  await p.close();

  checks['no errors in the console'] = logs.every((l) => !/^(error|pageerror)/.test(l));
  Object.keys(checks).forEach((k) => console.log((checks[k] ? 'PASS ' : 'FAIL ') + k));
  if (!Object.values(checks).every(Boolean)) {
    console.log(JSON.stringify(logs, null, 1));
    process.exitCode = 1;
  }

  await b.close();
})().catch((e) => { console.error('FAIL', e.message.split('\n')[0]); process.exit(1); });
