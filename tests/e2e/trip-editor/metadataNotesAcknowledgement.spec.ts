import { expect, test } from '@playwright/test';
import { build } from 'esbuild';

// Exercise the production draft owner and sanitizer in a real DOM without an application host.
let script: string;
test.beforeAll(async () => {
  const bundle = await build({ stdin: { contents: `export { default as Quill } from 'quill';
    export { createMetadataDraftState } from './ClientApps/trip-editor/src/components/metadataDraft';`,
    resolveDir: process.cwd(), loader: 'ts' }, bundle: true, write: false, format: 'iife',
    globalName: 'metadataNotesProbe', platform: 'browser' });
  script = bundle.outputFiles[0].text;
});

// These outputs represent RichNotes' link allow-list, tag unwrapping and cross-host proxy restoration.
const image = 'https://images.example.test/photo.jpg';
for (const example of [
  { label: 'Quill SMS link', link: 'sms:+1234',
    submitted: '<p><a href="sms:+1234">Contact</a></p>', stored: '<p><a>Contact</a></p>' },
  { label: 'Quill HTTPS control', link: 'https://example.test',
    submitted: '<p><a href="https://example.test">Contact</a></p>', stored: '<p><a href="https://example.test">Contact</a></p>' },
  { label: 'unsupported inline tag', link: null,
    submitted: '<p><mark>Contact</mark></p>', stored: '<p>Contact</p>' },
  { label: 'image proxy on another host', link: null,
    submitted: `<p><img src="https://backend.example.test/Public/ProxyImage?url=${encodeURIComponent(image)}"></p>`,
    stored: `<p><img src="${image}"></p>` }
]) test(`metadata acknowledges server-normalized ${example.label}`, async ({ page }) => {
  await page.setContent('<div id="notes"></div>');
  await page.addScriptTag({ content: script });
  const result = await page.evaluate(example => {
    const { Quill, createMetadataDraftState } = (window as any).metadataNotesProbe;
    const before = { id: 'trip', name: 'Trip', notesHtml: '', isPublic: true, shareProgressEnabled: false,
      coverImage: null, center: null, zoom: null,
      originalValues: { name: 'Trip', notesHtml: null, isPublic: true, coverImage: null, center: null, zoom: null } };
    const owner = createMetadataDraftState(before, [], {});
    if (example.link) {
      const quill = new Quill('#notes');
      quill.setText('Contact');
      quill.formatText(0, 7, 'link', example.link);
      owner.draft.notesHtml = quill.root.innerHTML;
    } else owner.draft.notesHtml = example.submitted;
    const submitted = owner.beginMetadata();
    const saved = { ...before, notesHtml: example.stored,
      originalValues: { ...before.originalValues, notesHtml: example.stored } };
    let error = null;
    try { owner.acknowledge(saved, submitted, false); }
    catch (failure) { error = String(failure); owner.failMetadata(failure); }
    return { submitted: submitted.changes.notesHtml, error, attempt: owner.metadataAttempt.value,
      draft: owner.draft.notesHtml, baseline: owner.baseline.notesHtml };
  }, example);
  expect(result.submitted).toBe(example.submitted);
  expect(result.error).toBeNull();
  expect(result.attempt).toBeNull();
  expect(result.draft).toBe(example.stored);
  expect(result.baseline).toBe(example.stored);
});
