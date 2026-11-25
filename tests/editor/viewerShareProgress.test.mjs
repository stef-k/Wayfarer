import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { chromium } from '@playwright/test';
import { build } from 'esbuild';

/** Runs the live Viewer handler with Greek document messages and controlled controller replies. */
test('Viewer sharing distinguishes a lost reply, controller refusals and an expired session', async t => {
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
    <span id="viewer-alert" role="alert"></span>
    <script id="wayfarer-locale" type="application/json"></script>`);
  const catalog = await readFile('Resources/Viewer.el-GR.resx', 'utf8');
  await page.evaluate(source => {
    const entries = new DOMParser().parseFromString(source, 'application/xml').querySelectorAll('data');
    const messages = Object.fromEntries(Array.from(entries, item => [item.getAttribute('name'), item.querySelector('value').textContent]));
    document.getElementById('wayfarer-locale').textContent = JSON.stringify({ locale: 'el-GR', fallback: 'en-US', messages, english: {} });
    window.wayfarer = { showAlert: (_kind, copy) => { document.getElementById('viewer-alert').textContent = copy; } };
    window.fetch = async () => {
      const { status, body } = window.reply;
      if (!status) throw new TypeError('Failed to fetch');
      return Response.json(body, { status });
    };
  }, catalog);
  await page.addScriptTag({ content: script.outputFiles[0].text });

  const main = page.locator('#shareProgressSwitch');
  const modal = page.locator('#modalShareProgressSwitch');
  const alert = page.getByRole('alert');
  for (const [reply, message] of [
    [{}, /μπορεί ακόμη να ολοκληρωθεί.*Ανανεώστε τη σελίδα για να ελέγξετε την τρέχουσα ρύθμιση/],
    [{ status: 200, body: { success: false, error: 'Trip not found or unauthorized' } }, /δεν έχετε πλέον δικαίωμα/],
    [{ status: 200, body: { success: false, error: 'Trip must be public to share progress' } }, /Ορίστε το ταξίδι ως δημόσιο/],
    [{ status: 401, body: null }, /Η συνεδρία σας έληξε/]
  ]) {
    await page.evaluate(value => { window.reply = value; document.getElementById('viewer-alert').textContent = ''; }, reply);
    await modal.click();
    await alert.filter({ hasText: message }).waitFor({ timeout: 3000 });
    assert.equal(await main.isChecked(), false);
    assert.equal(await modal.isChecked(), false);
  }

  await page.evaluate(() => { window.reply = { status: 200, body: { success: true, enabled: true } }; });
  await main.click();
  await page.locator('#modalShareProgressSwitch:checked').waitFor({ timeout: 3000 });
});
