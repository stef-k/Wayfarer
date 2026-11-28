import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { chromium, expect } from '@playwright/test';
import { build } from 'esbuild';

/** Checks that both switches, both badges and the copy link reflect saved state and retained intent. */
async function assertSharingDisplay(page, enabled, { checked = enabled, disabled = false } = {}) {
  for (const id of ['shareProgressSwitch', 'modalShareProgressSwitch']) {
    const toggle = page.locator('#' + id);
    await expect(toggle).toHaveJSProperty('checked', checked);
    await expect(toggle).toHaveJSProperty('disabled', disabled);
  }
  for (const id of ['shareProgressStatus', 'modalShareStatus'])
    await expect(page.locator('#' + id)).toHaveText(enabled ? 'Δημόσια ορατό' : 'Ιδιωτικό');
  assert.equal(await page.locator('#btnCopyProgressLink').evaluate(button => button.classList.contains('d-none')), !enabled);
}

/** Runs the receipt-aware Viewer against controlled replies and the actual Greek messages. */
test('Viewer sharing stays confirmed during a save and distinguishes failed replies', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  const trip = '12345678-1234-4234-8234-123456789abc';
  const endpoint = '/User/Trip/' + trip + '/share-progress/conditional';
  const script = await build({ stdin: { contents: `
    export { tripReceiptReplies, tripReceipt } from './tests/editor/tripReceiptReplies.mjs';
  `, resolveDir: process.cwd() }, bundle: true, write: false, format: 'iife', globalName: 'Replies' });
  const viewer = await build({ entryPoints: ['ClientApps/trip-editor/src/recovery/viewerSharing.ts'],
    bundle: true, write: false, format: 'iife' });
  await page.route('http://localhost/', route => route.fulfill({ contentType: 'text/html', body: `
    <input id="shareProgressSwitch" type="checkbox">
    <input id="modalShareProgressSwitch" type="checkbox">
    <span id="shareProgressStatus"></span><span id="modalShareStatus"></span>
    <button id="btnCopyProgressLink" class="d-none">Copy</button>
    <section id="viewer-sharing-recovery" data-account-id="account" data-trip-id="${trip}">
      <input name="__RequestVerificationToken" value="token">
      <span role="status"></span><button data-recover>Recover</button>
      <button data-adopt hidden>Adopt</button><button data-submit hidden>Save choice</button>
    </section>
    <script id="wayfarer-locale" type="application/json"></script>` }));
  await page.goto('http://localhost/');
  await page.addScriptTag({ content: script.outputFiles[0].text });
  const catalogs = await Promise.all(['Viewer', 'Editor'].map(name => readFile('Resources/' + name + '.el-GR.resx', 'utf8')));
  await page.evaluate(({ catalogs, trip }) => {
    const entries = catalogs.flatMap(source => Array.from(new DOMParser().parseFromString(source, 'application/xml').querySelectorAll('data')));
    const messages = Object.fromEntries(entries.map(item => [item.getAttribute('name'), item.querySelector('value').textContent]));
    document.getElementById('wayfarer-locale').textContent = JSON.stringify({ locale: 'el-GR', fallback: 'en-US', messages, english: {} });

    const originalValues = { name: 'Trip', notesHtml: null };
    let current = { id: trip, isPublic: true, shareProgressEnabled: false, originalValues };
    let saved;
    const pending = Promise.withResolvers();
    window.finishReply = pending.resolve;
    window.writes = [];
    window.lookups = 0;
    window.fetch = Replies.tripReceiptReplies(() => current, async (url, init, permit) => {
      const { enabled } = JSON.parse(init.body);
      window.writes.push([url, init.method, enabled]);
      if (window.writes.length === 3) {
        current = { ...current, isPublic: false };
        return Response.json(Replies.tripReceipt(permit, 'terminalUnapplied', { code: 'share-progress-requires-public' }), { status: 400 });
      }
      current = { ...current, shareProgressEnabled: enabled };
      saved = Replies.tripReceipt(permit, 'applied', { sharingResult: { isPublic: true, shareProgressEnabled: enabled } });
      if (window.writes.length === 1) {
        await pending.promise;
        throw new TypeError('Lost reply after commit');
      }
      return Response.json(saved);
    }, { lookup: () => { window.lookups++; return Response.json(saved); } });
  }, { catalogs, trip });
  await page.addScriptTag({ content: viewer.outputFiles[0].text });

  const main = page.locator('#shareProgressSwitch');
  const modal = page.locator('#modalShareProgressSwitch');
  const status = page.getByRole('status');
  await assertSharingDisplay(page, false);
  await modal.click();
  await page.waitForFunction(() => window.writes.length === 1);
  await modal.click({ force: true });
  assert.deepEqual(await page.evaluate(() => window.writes), [[endpoint, 'POST', true]]);
  await assertSharingDisplay(page, false, { disabled: true });

  await page.evaluate(() => window.finishReply());
  await expect(status).toContainText('Μια προηγούμενη αποθήκευση ταξιδιού δεν έχει επιβεβαιωθεί.');
  await assertSharingDisplay(page, false, { checked: true, disabled: true });
  await page.locator('[data-recover]').click();
  await expect(status).toContainText('Η προηγούμενη αποθήκευση του ταξιδιού επιβεβαιώθηκε.');
  await assertSharingDisplay(page, true);
  assert.deepEqual(await page.evaluate(() => [window.writes.length, window.lookups]), [1, 1]);

  await main.click();
  await assertSharingDisplay(page, false);
  await modal.click();
  await expect(status).toContainText(/Ορίστε το ταξίδι ως δημόσιο/);
  await assertSharingDisplay(page, false, { checked: true, disabled: true });
  await expect(page.locator('[data-submit]')).toBeHidden();
  assert.deepEqual(await page.evaluate(() => window.writes), [
    [endpoint, 'POST', true], [endpoint, 'POST', false], [endpoint, 'POST', true]
  ]);
});
