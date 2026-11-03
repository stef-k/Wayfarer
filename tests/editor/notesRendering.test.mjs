import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { build } from 'esbuild';
import { chromium } from '@playwright/test';

/** Actual display helpers and shipped CSS retain standard styles and passive history without live editors. */
test('Notes display surfaces retain standard formatting and proxy privacy', async t => {
  const bundle = await build({ stdin: { contents: `export { notesHtmlForDisplay } from './wwwroot/js/util/notes-images.js';
    export { placeNotesPreviewHtml } from './ClientApps/trip-editor/src/displayHelpers';`, resolveDir: process.cwd(), loader: 'ts' },
    bundle: true, write: false, format: 'iife', globalName: 'Display' });
  const css = await Promise.all(['wwwroot/css/site.css', 'wwwroot/css/rich-notes.css', 'ClientApps/rich-notes/editor.css'].map(path => readFile(path, 'utf8')));
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  let direct = 0;
  await page.route('**/*', route => {
    if (route.request().url().startsWith('https://images.example.test')) direct++;
    return route.fulfill({ contentType: 'text/html', body: '<main></main>' });
  });
  await page.goto('http://notes.test/');
  for (const content of css) await page.addStyleTag({ content });
  await page.addScriptTag({ content: bundle.outputFiles[0].text });
  const result = await page.evaluate(() => {
    const html = '<h3 style="text-align: right;"><span style="font-family: monospace;">Heading</span></h3>'
      + '<p><strong>B</strong><em>I</em><u>U</u><s>S</s><a href="/safe">link</a><img src="https://images.example.test/a"></p>'
      + '<ol><li>ordered</li></ol><ul><li>bullet</li></ul><blockquote>quote</blockquote>'
      + '<p class="ql-align-center"><span class="ql-font-serif">historical</span></p>';
    return ['trip-notes', 'place-notes', 'notes-content', 'trip-notes-readable', 'rich-notes-content'].map((surface, i) => {
      const container = document.createElement('section');
      container.className = surface;
      container.innerHTML = i === 1 ? Display.placeNotesPreviewHtml(html, 1000) : Display.notesHtmlForDisplay(html);
      document.querySelector('main').append(container);
      return { surface, heading: getComputedStyle(container.querySelector('h3')).textAlign,
        font: getComputedStyle(container.querySelector('h3 span')).fontFamily,
        historicalAlign: getComputedStyle(container.querySelector('.ql-align-center')).textAlign,
        historicalFont: getComputedStyle(container.querySelector('.ql-font-serif')).fontFamily,
        image: container.querySelector('img').getAttribute('src'),
        tags: ['strong', 'em', 'u', 's', 'a', 'ol', 'ul', 'blockquote'].every(tag => container.querySelector(tag)),
        minHeight: getComputedStyle(container).minHeight, editable: container.querySelectorAll('[contenteditable]').length };
    });
  });
  for (const value of result) {
    assert.equal(value.heading, 'right', value.surface); assert.equal(value.font, 'monospace');
    assert.equal(value.historicalAlign, 'center'); assert.match(value.historicalFont, /Georgia/);
    assert.equal(value.image, '/Public/ProxyImage?url=https%3A%2F%2Fimages.example.test%2Fa');
    assert.equal(value.tags, true); assert.equal(value.editable, 0); assert.equal(value.minHeight, '0px');
  }
  assert.equal(direct, 0);
});
