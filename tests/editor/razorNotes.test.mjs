import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';
import { chromium } from '@playwright/test';

// Real page entrypoints own ordinary form submission. The small form fixture supplies only their existing DOM contract.
test('ordinary Razor entries retain Notes, validation inputs and bulk operation selection', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  for (const [entry, name, field, form] of [
    ['location-create', 'Notes', 'Notes', 'createForm'],
    ['location-bulk', 'Notes', 'hiddenNotes', 'locationForm'],
    ['visit-edit', 'NotesHtml', 'hiddenNotes', 'visitForm']
  ]) {
    await t.test(entry, async () => {
      const bundle = await build({ entryPoints: [`ClientApps/rich-notes/${entry}.js`], bundle: true, write: false,
        format: 'iife', loader: { '.css': 'empty' }, plugins: [{ name: 'map-boundary', setup(builder) {
          builder.onLoad({ filter: /retryTileLayer\.js$/ }, () => ({ contents: 'export const createTileLayer = () => ({});' }));
        } }] });
      const page = await browser.newPage();
      page.setDefaultTimeout(5000);
      page.on('pageerror', error => console.log(entry, error.message));
      try {
        await page.setContent(`<div id="browser-antiforgery" data-subject-id="original"></div>
          <div id="location-create-private"><form id="${form}">
          ${entry === 'location-create' ? `<input name="CreateKey" value="same-key" type="hidden">
          <input name="CreateIntent" value="owner-intent" type="hidden"><input name="AttemptedValues" type="hidden">
          <input name="CreateMayHaveEffect" value="false" type="hidden"><input name="__RequestVerificationToken" value="csrf" type="hidden">
          <div id="create-status" hidden></div><button type="submit">Save</button>` : ''}
          <label id="notes-label">Notes</label><div id="notes-toolbar"><select data-heading><option value="0">Paragraph</option><option value="3">Heading 3</option></select>
          ${entry === 'visit-edit' ? '<button type="button" data-action="strike">Strike</button>' : '<select data-font><option value="serif">Serif</option></select><button type="button" data-action="clear">Clear</button>'}</div>
          <div id="notes"></div><input id="${field}" name="${name}" type="hidden">
          <p id="notes-feedback"></p><input id="Latitude" value="invalid latitude"><span class="field-validation-error">Invalid coordinate</span>
          <input id="LocalTimestamp" value="2026-11-01T12:00"><select id="Country" name="Country"><option value="GR">GR</option></select>
          <select id="Region" name="Region"><option value="Attica">Attica</option></select><select id="Place" name="Place"><option value="Athens">Athens</option></select>
          <input id="FromDate" name="FromDate" value="2026-11-01"><input id="ToDate" name="ToDate" value="2026-11-03">
          <input type="checkbox" id="Append" name="Append" value="true" checked><input type="checkbox" id="ClearNotes" name="ClearNotes" value="true">
          <button type="button" id="applyBtn">Apply</button></form>
          <dialog id="notes-dialog"><h2 id="notes-dialog-title"></h2><input id="notes-url"><p id="notes-dialog-error"></p>
          <button id="notes-dialog-cancel"></button><button id="notes-dialog-apply"></button><button id="notes-dialog-unlink"></button><button id="notes-dialog-follow"></button></dialog></div>`);
        const original = '<p class="ql-align-center">Validation-retained Notes</p>';
        await page.evaluate(({ original, field, entry }) => {
          document.getElementById('notes').dataset.notesContent = original;
          document.getElementById(field).value = original;
          // Create's real async owner retains input on an unconfirmed same-owner reply; other forms use their ordinary endpoint.
          window.fetch = async () => entry === 'location-create'
            ? new Response('<div id="browser-antiforgery" data-subject-id="original"></div>', { status: 500 })
            : new Response('23', { headers: { 'content-type': 'application/json' } });
        }, { original, field, entry });
        await page.addScriptTag({ content: bundle.outputFiles[0].text });
        const editor = page.locator('#notes [contenteditable]');
        await editor.focus();
        const submit = async () => {
          const values = await page.evaluate(form => {
            const element = document.getElementById(form);
            element.dispatchEvent(new Event('submit', { cancelable: true }));
            return Object.fromEntries(new FormData(element));
          }, form);
          if (entry === 'location-create')
            await page.waitForFunction(() => document.getElementById('create-status').textContent.startsWith('The save could not be confirmed.'));
          return values;
        };
        assert.equal((await submit())[name], original);
        assert.equal(await page.locator('#Latitude').inputValue(), 'invalid latitude');
        assert.equal(await page.locator('#LocalTimestamp').inputValue(), '2026-11-01T12:00');
        assert.equal(await page.locator('.field-validation-error').textContent(), 'Invalid coordinate');
        await editor.fill('Edited');
        assert.equal((await submit())[name], '<p>Edited</p>');
        if (entry === 'visit-edit') {
          await editor.press('Control+A');
          await page.locator('[data-action=strike]').click();
          assert.equal((await submit())[name], '<p><s>Edited</s></p>');
          await editor.fill('');
        } else await page.locator('[data-action=clear]').click();
        assert.equal((await submit())[name], '');
        if (entry === 'location-bulk') {
          assert.deepEqual(await submit(), { Notes: '', Country: 'GR', Region: 'Attica', Place: 'Athens', FromDate: '2026-11-01', ToDate: '2026-11-03', Append: 'true' });
          await page.locator('#Append').uncheck();
          assert.equal((await submit()).Append, undefined);
          await page.locator('#ClearNotes').check();
          assert.equal((await submit()).ClearNotes, 'true');
          await page.waitForFunction(() => document.getElementById('applyBtn').textContent.includes('23'));
        }
      } finally { await page.close(); }
    });
  }
});
