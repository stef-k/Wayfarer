import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createSSRApp, h, proxyRefs } from 'vue';
import { renderToString } from 'vue/server-renderer';

// Run the production exit actions and unload guard with controlled user/API responses.
const filename = 'ClientApps/trip-editor/src/components/MetadataEditor.vue';
const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
const script = compileScript(descriptor, { id: 'editor-exit-test' });
const bundled = await build({ stdin: { contents: script.content, loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
  external: ['vue'], plugins: [{ name: 'exit-boundaries', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, () => ({ contents: 'export default {};' }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = options => globalThis.exitConfirm(options);' }));
  } }] });
const code = bundled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const component = (await import(`data:text/javascript;base64,${Buffer.from(code + '\n//# sourceURL=editor-exit-test.mjs').toString('base64')}`)).default;

test('custom exit confirmation replaces the native warning only after approval and successful save', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  let editor;
  let approved = false;
  let confirmations = 0;
  const navigations = [];
  const warns = () => {
    const event = { prevented: false, preventDefault() { this.prevented = true; } };
    editor.confirmUnload(event);
    return event.prevented;
  };
  globalThis.exitConfirm = async () => { confirmations++; return approved; };
  globalThis.window = { location: { assign: url => navigations.push({ url, nativeWarning: warns() }) } };
  const open = async (childDirty = true) => {
    await renderToString(createSSRApp({ setup() {
      editor = proxyRefs(component.setup({ metadata: { name: 'Trip', isPublic: false, zoom: null },
        tagsBySlug: {}, tagOrder: [], tagOptions: {}, hasRegionDraftChanges: childDirty,
        tripIndexUrl: '/User/Trip', editorEndpoint: '/editor', editorSurface: {}, autoOpen: false },
      { expose() {}, emit() {} }));
      return () => h('div');
    } }));
  };
  try {
    await open();
    assert.equal(warns(), true, 'reload/tab close still warns for child drafts');
    await editor.backToTrips();
    assert.equal(navigations.length, 0);
    assert.equal(warns(), true, 'cancel retains protection');
    approved = true;
    await editor.backToTrips();
    assert.equal(confirmations, 2);
    assert.deepEqual(navigations.pop(), { url: '/User/Trip', nativeWarning: false });

    await open();
    await editor.saveAndExit();
    assert.deepEqual(navigations.pop(), { url: '/User/Trip', nativeWarning: false });
    await open();
    editor.draft.name = 'Edited trip';
    globalThis.fetch = async () => new Response('{}', { status: 500 });
    await editor.saveAndExit();
    assert.equal(navigations.length, 0, 'failed save stays on the page');
    assert.equal(warns(), true, 'failed save retains protection');

    await open(false);
    assert.equal(warns(), false);
    editor.draft.name = 'Unsaved metadata';
    assert.equal(warns(), true, 'metadata remains independently protected');
    await editor.backToTrips();
    assert.equal(navigations.pop().nativeWarning, false);
  } finally {
    globalThis.window = previousWindow;
    globalThis.fetch = previousFetch;
    delete globalThis.exitConfirm;
  }
});
