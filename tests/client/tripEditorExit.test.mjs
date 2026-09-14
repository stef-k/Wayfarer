import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createSSRApp, createRenderer, h, nextTick, proxyRefs, shallowReactive } from 'vue';
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

// Mount the real draft owner with reactive props; child rendering is irrelevant to draft lifetime.
const mountDraft = () => {
  let editor;
  const props = shallowReactive({ metadata: { name: 'Trip', isPublic: false, notesHtml: '',
    center: { latitude: 37.12345601, longitude: 23 }, zoom: 9 },
    capturedMapView: null, tagsBySlug: {}, tagOrder: [], tagOptions: {}, hasRegionDraftChanges: false,
    tripIndexUrl: '/User/Trip', editorEndpoint: '/editor', editorSurface: {
      registerTargetHandler: () => () => {}, isTargetActive: () => false
    }, autoOpen: false });
  const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {}, parentNode() {} });
  const app = renderer.createApp({ setup() {
    editor = proxyRefs(component.setup(props, { expose() {}, emit: (name, metadata) => {
      if (name === 'saved') props.metadata = metadata;
    } }));
    return () => null;
  } });
  app.mount({});
  return { editor, props, dispose: () => app.unmount(), capture: async (latitude, zoom = 10) => {
    props.capturedMapView = { center: { latitude, longitude: 24 }, zoom };
    await nextTick();
  } };
};

test('captured viewport survives closed settings and unrelated refresh, with equivalent capture and reset staying clean', async () => {
  const previousWindow = globalThis.window;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  try {
    owner.props.capturedMapView = { center: { latitude: 37.123456, longitude: 23 }, zoom: 9 };
    await nextTick();
    assert.equal(owner.editor.isDirty, false, 'six-decimal equivalence must not dirty persisted metadata');
    await owner.capture(38);
    assert.equal(owner.editor.isDirty, true);
    assert.equal(owner.editor.draft.centerLatitude, '38.000000');
    owner.props.tagsBySlug = {};
    owner.props.tagOrder = [];
    owner.props.metadata = { ...owner.props.metadata, name: 'Authoritative renamed Trip' };
    await nextTick();
    assert.equal(owner.editor.draft.name, 'Authoritative renamed Trip');
    assert.equal(owner.editor.draft.centerLatitude, '38.000000');
    owner.editor.resetDraft();
    assert.equal(owner.editor.isDirty, false);
    await owner.capture(38);
    assert.equal(owner.editor.isDirty, true, 'same viewport may be captured again after discard');
  } finally { owner.dispose(); globalThis.window = previousWindow; }
});

test('capture after PATCH starts survives the old response and Save & Exit, then saves normally', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  const navigations = [];
  globalThis.window = { addEventListener() {}, removeEventListener() {}, location: { assign: url => navigations.push(url) } };
  const owner = mountDraft();
  let complete;
  const requests = [];
  globalThis.fetch = async (_url, options) => {
    const body = JSON.parse(options.body);
    requests.push(body);
    await new Promise(resolve => { complete = resolve; });
    const metadata = { ...owner.props.metadata, ...body };
    return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
  };
  try {
    await owner.capture(38);
    const saving = owner.editor.saveAndExit();
    assert.equal(requests[0].center.latitude, 38);
    await owner.capture(39);
    complete();
    await saving;
    await nextTick();
    assert.equal(owner.props.metadata.center.latitude, 38);
    assert.equal(owner.editor.draft.centerLatitude, '39.000000');
    assert.equal(owner.editor.isDirty, true);
    assert.deepEqual(navigations, [], 'new capture must not be silently discarded through exit');
    const retry = owner.editor.saveAndExit();
    assert.equal(requests[1].center.latitude, 39);
    complete();
    await retry;
    await nextTick();
    assert.equal(owner.editor.isDirty, false);
    assert.deepEqual(navigations, ['/User/Trip']);
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

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
    assert.equal(warns(), true, 'interrupted navigation must not suppress a later unload');

    await open();
    await editor.saveAndExit();
    assert.deepEqual(navigations.pop(), { url: '/User/Trip', nativeWarning: false });
    assert.equal(warns(), true, 'Save & Exit approval is also consumed once');
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
