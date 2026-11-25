import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { chromium } from '@playwright/test';
import { build } from 'esbuild';

/** Checks that both switches, both badges and the copy link show the confirmed setting. */
const assertSharingDisplay = async (page, enabled, saving = false) => {
  for (const id of ['shareProgressSwitch', 'modalShareProgressSwitch']) {
    const toggle = page.locator(`#${id}`);
    assert.equal(await toggle.isChecked(), enabled, id);
    assert.equal(await toggle.isDisabled(), saving, id);
  }
  for (const id of ['shareProgressStatus', 'modalShareStatus']) {
    assert.equal(await page.locator(`#${id}`).textContent(), enabled ? 'Δημόσια ορατό' : 'Ιδιωτικό', id);
  }
  assert.equal(await page.locator('#btnCopyProgressLink').evaluate(button => button.classList.contains('d-none')), !enabled);
};

/** Runs the live Viewer handler with Greek document messages and controlled controller replies. */
test('Viewer sharing stays confirmed during a save and distinguishes failed replies', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  const script = await build({ stdin: { contents: `
    import { initShareProgressToggle } from './wwwroot/js/Areas/User/Trip/shareProgressToggle.js';
    initShareProgressToggle();`, resolveDir: process.cwd() }, bundle: true, write: false, format: 'iife' });
  await page.setContent(`
    <input id="shareProgressSwitch" data-trip-id="trip" type="checkbox">
    <input id="modalShareProgressSwitch" data-trip-id="trip" type="checkbox">
    <span id="shareProgressStatus"></span><span id="modalShareStatus"></span>
    <button id="btnCopyProgressLink" class="d-none">Copy</button>
    <span id="viewer-alert" role="alert"></span>
    <script id="wayfarer-locale" type="application/json"></script>`);
  const catalog = await readFile('Resources/Viewer.el-GR.resx', 'utf8');
  await page.evaluate(source => {
    const entries = new DOMParser().parseFromString(source, 'application/xml').querySelectorAll('data');
    const messages = Object.fromEntries(Array.from(entries, item => [item.getAttribute('name'), item.querySelector('value').textContent]));
    document.getElementById('wayfarer-locale').textContent = JSON.stringify({ locale: 'el-GR', fallback: 'en-US', messages, english: {} });
    window.wayfarer = { showAlert: (_kind, copy) => { document.getElementById('viewer-alert').textContent = copy; } };
    window.requests = [];
    window.fetch = async url => {
      window.requests.push(url);
      const { status, body } = await window.reply;
      if (!status) throw new TypeError('Failed to fetch');
      return Response.json(body, { status });
    };
  }, catalog);
  await page.addScriptTag({ content: script.outputFiles[0].text });

  const main = page.locator('#shareProgressSwitch');
  const modal = page.locator('#modalShareProgressSwitch');
  const alert = page.getByRole('alert');
  // Try a second click while the first save is pending, then simulate its lost reply.
  await page.evaluate(() => {
    const pending = Promise.withResolvers();
    window.reply = pending.promise;
    window.finishReply = pending.resolve;
  });
  await modal.click();
  await page.waitForFunction(() => window.requests.length === 1);
  await modal.click({ force: true });
  assert.deepEqual(await page.evaluate(() => window.requests), ['/User/Trip/ToggleShareProgress?id=trip&enabled=true']);
  await assertSharingDisplay(page, false, true);
  await page.evaluate(() => window.finishReply({}));
  await alert.filter({ hasText: /μπορεί ακόμη να ολοκληρωθεί.*Ανανεώστε τη σελίδα για να ελέγξετε την τρέχουσα ρύθμιση/ }).waitFor({ timeout: 3000 });
  await assertSharingDisplay(page, false);

  for (const [reply, message] of [
    [{ status: 200, body: { success: false, error: 'Trip not found or unauthorized' } }, /δεν έχετε πλέον δικαίωμα/],
    [{ status: 200, body: { success: false, error: 'Trip must be public to share progress' } }, /Ορίστε το ταξίδι ως δημόσιο/]
  ]) {
    await page.evaluate(value => { window.reply = value; document.getElementById('viewer-alert').textContent = ''; }, reply);
    await modal.click();
    await alert.filter({ hasText: message }).waitFor({ timeout: 3000 });
    await assertSharingDisplay(page, false);
  }

  await page.evaluate(() => { window.reply = { status: 200, body: { success: true, enabled: true } }; });
  await main.click();
  await page.locator('#modalShareProgressSwitch:checked').waitFor({ timeout: 3000 });
  await assertSharingDisplay(page, true);

  await page.evaluate(() => { window.reply = { status: 401, body: null }; });
  await modal.click();
  await alert.filter({ hasText: /Η συνεδρία σας έληξε/ }).waitFor({ timeout: 3000 });
  await assertSharingDisplay(page, true);
});
