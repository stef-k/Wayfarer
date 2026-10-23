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

// Model the server's compatible display projection plus exact original-value evidence.
const withOriginals = metadata => ({ ...metadata, originalValues: { name: metadata.name, notesHtml: metadata.notesHtml ?? null,
  isPublic: metadata.isPublic, coverImage: metadata.coverImage ? { rawUrl: metadata.coverImage.rawUrl } : null,
  center: metadata.center ?? { latitude: null, longitude: null }, zoom: metadata.zoom ?? null } });

// Mount the real draft owner under a reactive parent so Vue schedules child prop updates.
const mountDraft = () => {
  let editor;
  const props = shallowReactive({ metadata: withOriginals({ name: 'Trip', isPublic: false, notesHtml: '',
    center: { latitude: 37.12345601, longitude: 23 }, zoom: 9 }),
    capturedMapView: null, tagsBySlug: {}, tagOrder: [], tagOptions: {}, hasRegionDraftChanges: false,
    tripIndexUrl: '/User/Trip', editorEndpoint: '/editor', antiforgeryToken: 'test-token', editorSurface: {
      registerTargetHandler: () => () => {}, isTargetActive: () => false
    }, autoOpen: false });
  // Only rendering is stubbed; production setup, watchers and emitted events belong to the child.
  const child = { ...component, setup(childProps, context) {
    editor = proxyRefs(component.setup(childProps, context));
    return () => null;
  } };
  const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {},
    parentNode: () => ({}), nextSibling: () => null });
  const app = renderer.createApp({ setup() {
    return () => h(child, { ...props,
      onSaved: metadata => { props.metadata = metadata; },
      onMutationApplied: result => {
        if (result.affected.metadata) props.metadata = result.affected.metadata;
        if (!Array.isArray(result.data)) return;
        props.tagOrder = result.data.map(tag => tag.slug);
        props.tagsBySlug = Object.fromEntries(result.data.map(tag => [tag.slug, tag]));
      }
    });
  } });
  app.mount({});
  return { editor, props, dispose: () => app.unmount(), capture: async (latitude, zoom = 10) => {
    props.capturedMapView = { center: { latitude, longitude: 24 }, zoom };
    await nextTick();
  } };
};

// Exercise actual metadata/sharing actions; rejected authentication never advances a draft or navigates away.
for (const sharing of [false, true]) test(`session rejection retains ${sharing ? 'sharing' : 'metadata'} draft and reports fresh-login guidance`, async () => {
  const previous = { window: globalThis.window, fetch: globalThis.fetch };
  const navigations = [];
  globalThis.window = { addEventListener() {}, removeEventListener() {}, location: { assign: url => navigations.push(url) } };
  globalThis.fetch = async () => new Response(JSON.stringify({ message: 'Your session has ended or your account is disabled. Sign in again.' }), { status: 401 });
  const owner = mountDraft();
  try {
    if (sharing) {
      owner.props.metadata = withOriginals({ ...owner.props.metadata, isPublic: true, shareProgressEnabled: false });
      await nextTick();
      owner.editor.draft.shareProgressEnabled = true;
    } else owner.editor.draft.name = 'Unsaved name';
    const before = structuredClone(owner.props.metadata);
    await owner.editor.saveAndExit();
    assert.match(owner.editor.saveError, /session has ended|account is disabled/i);
    assert.match(owner.editor.saveError, /sign in again/i);
    assert.equal(owner.editor.isDirty, true);
    assert.equal(sharing ? owner.editor.draft.shareProgressEnabled : owner.editor.draft.name, sharing ? true : 'Unsaved name');
    assert.deepEqual(owner.props.metadata, before);
    assert.equal(owner.editor.lastSavedAt, null);
    assert.deepEqual(navigations, []);
  } finally { owner.dispose(); Object.assign(globalThis, previous); }
});

test('captured viewport survives closed settings and unrelated refresh, with equivalent capture and reset staying clean', async () => {
  const previousWindow = globalThis.window;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  try {
    owner.editor.draft.centerLatitude = 38.5; // Vue's number input emits a number, not a string.
    owner.editor.draft.centerLongitude = 24;
    owner.editor.draft.zoom = 10;
    assert.equal(owner.editor.isDirty, true, 'manual numeric fields retain their existing save contract');
    owner.editor.resetDraft();
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
    const metadata = withOriginals({ ...owner.props.metadata, ...body.changes });
    return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
  };
  try {
    await owner.capture(38);
    const saving = owner.editor.saveAndExit();
    assert.equal(requests[0].changes.center.latitude, 38);
    await owner.capture(39);
    complete();
    await saving;
    await nextTick();
    assert.equal(owner.props.metadata.center.latitude, 38);
    assert.equal(owner.editor.draft.centerLatitude, '39.000000');
    assert.equal(owner.editor.isDirty, true);
    assert.deepEqual(navigations, [], 'new capture must not be silently discarded through exit');
    const retry = owner.editor.saveAndExit();
    assert.equal(requests[1].changes.center.latitude, 39);
    complete();
    await retry;
    await nextTick();
    assert.equal(owner.editor.isDirty, false);
    assert.deepEqual(navigations, ['/User/Trip']);
    await owner.capture(40);
    owner.editor.draft.centerLatitude = 39; // Manual edit back to the saved view also permits clean exit.
    await owner.editor.saveAndExit();
    assert.equal(navigations.length, 2);
    await owner.capture(40);
    globalThis.fetch = async () => new Response('{}', { status: 500 });
    await owner.editor.saveAndExit();
    assert.equal(owner.editor.draft.centerLatitude, '40.000000');
    assert.equal(owner.editor.isDirty, true, 'failed PATCH retains the captured draft');
    assert.equal(navigations.length, 2, 'failed PATCH retains exit protection');
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

// A capture owns the current manual values even when no field assignment is needed.
test('capture matching a manual zoom edit survives an older PATCH and saves on retry', async () => {
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
    const metadata = withOriginals({ ...owner.props.metadata, ...body.changes });
    return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
  };
  try {
    await owner.capture(38, 10);
    const saving = owner.editor.saveAndExit();
    assert.equal(requests[0].changes.zoom, 10);
    owner.editor.draft.zoom = 11; // Match the still-enabled numeric input's value.
    await owner.capture(38, 11);
    complete();
    await saving;
    await nextTick();
    assert.equal(owner.editor.draft.zoom, 11);
    assert.equal(owner.editor.isMetadataDirty, true);
    assert.deepEqual(navigations, [], 'the newer unsaved view must retain the editor');
    const retry = owner.editor.saveAndExit();
    assert.equal(requests[1].changes.zoom, 11);
    complete();
    await retry;
    await nextTick();
    assert.equal(owner.editor.isDirty, false);
    assert.deepEqual(navigations, ['/User/Trip']);
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

test('dirty Name and Notes keep their original base and visible conflict across props refresh and retry', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    return new Response(JSON.stringify({ code: 'mutation-base-stale' }), { status: 409 });
  };
  try {
    owner.editor.draft.name = 'My name';
    owner.editor.draft.notesHtml = '<p>My notes</p>';
    owner.props.metadata = withOriginals({ ...owner.props.metadata, name: 'Other device', notesHtml: '<p>Other notes</p>', zoom: 11 });
    await nextTick();
    assert.equal(owner.editor.draft.name, 'My name');
    assert.equal(owner.editor.draft.zoom, '11', 'clean fields may refresh independently');
    await owner.editor.save(false);
    assert.deepEqual(requests[0], { url: '/editor/metadata/conditional', body: {
      expected: { name: 'Trip', notesHtml: '' }, changes: { name: 'My name', notesHtml: '<p>My notes</p>' } } });
    assert.match(owner.editor.saveError, /draft is retained/);
    owner.props.tagsBySlug = {};
    await nextTick();
    assert.match(owner.editor.saveError, /draft is retained/, 'unrelated props cannot clear a conflict');
    await owner.editor.save(false);
    assert.deepEqual(requests[1].body, requests[0].body, 'failed save cannot adopt an intervening base');
    assert.equal(requests.length, 2, 'only explicit saves dispatch; there is no fallback or blind replay');
    owner.editor.resetDraft();
    owner.editor.draft.name = 'Explicit reviewed edit';
    await owner.editor.save(false);
    assert.equal(requests[2].body.expected.name, 'Other device', 'explicit discard/review adopts the visible current base');
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

test('older responses preserve newer manual edits and their acknowledged base, including fields first edited during save', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  let complete;
  let body;
  globalThis.fetch = async (_url, options) => {
    body = JSON.parse(options.body);
    await new Promise(resolve => { complete = resolve; });
    const metadata = withOriginals({ ...owner.props.metadata, ...body.changes });
    return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
  };
  try {
    owner.editor.draft.name = 'First name';
    const saving = owner.editor.save(false);
    owner.editor.draft.name = 'Newer name';
    owner.editor.draft.notesHtml = 'Newer notes';
    complete();
    await saving;
    assert.equal(owner.editor.draft.name, 'Newer name');
    assert.equal(owner.editor.draft.notesHtml, 'Newer notes');
    assert.deepEqual(owner.editor.draftState.request(), { expected: { name: 'First name', notesHtml: '' },
      changes: { name: 'Newer name', notesHtml: 'Newer notes' } });
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

test('missing original evidence and unsupported protected route fail closed without losing the draft', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  const calls = [];
  globalThis.fetch = async url => { calls.push(url); return new Response('{}', { status: 404 }); };
  try {
    owner.props.metadata = { ...owner.props.metadata, originalValues: undefined };
    await nextTick();
    owner.editor.draft.name = 'Retained';
    await owner.editor.save(false);
    assert.match(owner.editor.saveError, /Original Trip values are unavailable/);
    assert.deepEqual(calls, []);
    owner.props.metadata = withOriginals(owner.props.metadata);
    await nextTick();
    owner.editor.resetDraft();
    owner.editor.draft.name = 'Still retained';
    await owner.editor.save(false);
    assert.deepEqual(calls, ['/editor/metadata/conditional']);
    assert.equal(owner.editor.draft.name, 'Still retained');
    assert.equal(owner.editor.isDirty, true);
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

test('publication acknowledgements retain the derived sharing baseline when privatizing and republishing', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  const calls = [];
  globalThis.fetch = async (url, options) => {
    calls.push(url);
    const body = JSON.parse(options.body);
    const metadata = withOriginals({ ...owner.props.metadata, ...body.changes, shareProgressEnabled: false });
    return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
  };
  try {
    owner.props.metadata = withOriginals({ ...owner.props.metadata, isPublic: true, shareProgressEnabled: true });
    await nextTick();
    owner.editor.draft.isPublic = false;
    await nextTick();
    await owner.editor.save(false);
    assert.equal(owner.editor.isDirty, false);
    owner.editor.draft.isPublic = true;
    await owner.editor.save(false);
    assert.equal(owner.editor.isDirty, false, 'derived sharing cannot remain an old dirty obligation after republishing');
    assert.deepEqual(calls, ['/editor/metadata/conditional', '/editor/metadata/conditional']);
    assert.equal(owner.editor.draft.shareProgressEnabled, false);
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

// A later checkbox choice may match the old baseline; it still follows the pending privacy save.
for (const initiallyShared of [false, true]) {
  test(`delayed privacy acknowledgement retains newer publication and sharing intent (initial sharing ${initiallyShared})`, async () => {
    const previousWindow = globalThis.window;
    const previousFetch = globalThis.fetch;
    const navigations = [];
    globalThis.window = { addEventListener() {}, removeEventListener() {}, location: { assign: url => navigations.push(url) } };
    const owner = mountDraft();
    const requests = [];
    let complete;
    let serverMetadata;
    let notifySharingStarted;
    const sharingStarted = new Promise(resolve => { notifySharingStarted = resolve; });
    globalThis.fetch = async (url, options) => {
      const body = JSON.parse(options.body);
      requests.push({ url, body });
      assert.equal(options.method, 'PATCH');
      assert.equal(options.credentials, 'same-origin');
      const isSharing = url === '/editor/share-progress';
      assert.ok(isSharing || url === '/editor/metadata/conditional', 'no legacy metadata fallback');
      if (isSharing) assert.equal(serverMetadata.isPublic, true, 'server publication must be acknowledged first');
      await new Promise(resolve => { complete = resolve; if (isSharing) notifySharingStarted(); });
      serverMetadata = withOriginals({ ...serverMetadata, ...body.changes,
        shareProgressEnabled: isSharing ? body.enabled : body.changes.isPublic === false ? false : serverMetadata.shareProgressEnabled });
      return new Response(JSON.stringify({ data: serverMetadata, affected: { metadata: serverMetadata }, warnings: [] }));
    };
    try {
      owner.props.metadata = withOriginals({ ...owner.props.metadata, isPublic: true, shareProgressEnabled: initiallyShared });
      await nextTick();
      serverMetadata = owner.props.metadata;
      owner.editor.draft.isPublic = false;
      await nextTick();
      assert.equal(owner.editor.draft.shareProgressEnabled, false, 'making private derives a sharing disable');
      const privatizing = owner.editor.saveAndExit();
      assert.deepEqual(requests[0].body, { expected: { isPublic: true }, changes: { isPublic: false } });
      owner.editor.draft.isPublic = true;
      await nextTick();
      assert.equal(owner.editor.shareProgressUnavailable, false);
      owner.editor.visibleShareProgressEnabled = true;
      complete();
      await privatizing;
      // Exercise a later props refresh as well as the save's own acknowledgement/refresh.
      owner.props.metadata = withOriginals({ ...owner.props.metadata });
      await nextTick();
      assert.equal(owner.props.metadata.isPublic, false);
      assert.equal(owner.props.metadata.shareProgressEnabled, false);
      assert.equal(owner.editor.draft.isPublic, true);
      assert.equal(owner.editor.draft.shareProgressEnabled, true, 'newer authored sharing must survive the private reply');
      assert.equal(owner.editor.isDirty, true);
      assert.equal(owner.editor.statusText, 'Unsaved changes');
      assert.equal(owner.editor.shareProgressUnavailable, true, 'sharing remains unavailable on the committed private Trip');
      assert.equal(requests.length, 1, 'an old reply cannot dispatch the newer sharing choice');
      assert.deepEqual(navigations, [], 'Save & Exit must retain newer unsaved intent');

      const republishing = owner.editor.saveAndExit();
      assert.deepEqual(requests[1], { url: '/editor/metadata/conditional', body: {
        expected: { isPublic: false }, changes: { isPublic: true } } });
      assert.equal(requests.length, 2, 'sharing cannot start while publication is pending');
      complete();
      // A skipped sharing request must fail an assertion instead of hanging on an unfulfilled stub.
      await Promise.race([sharingStarted, republishing]);
      assert.deepEqual(requests[2], { url: '/editor/share-progress', body: { enabled: true } });
      assert.equal(serverMetadata.isPublic, true);
      assert.equal(serverMetadata.shareProgressEnabled, false, 'sharing has not been acknowledged yet');
      assert.deepEqual(navigations, [], 'sharing also requires its own acknowledgement before exit');
      complete();
      await republishing;
      await nextTick();
      assert.equal(owner.editor.isDirty, false);
      assert.equal(owner.props.metadata.shareProgressEnabled, true);
      assert.equal(owner.editor.draft.shareProgressEnabled, true);
      assert.equal(requests.length, 3);
      assert.deepEqual(navigations, ['/User/Trip']);
    } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
  });
}

// Seed the retained F1 draft proven above, then challenge only publication/sharing dispatch guards.
for (const outcome of ['publication conflict', 'publication failure', 'sharing failure', 'newer private draft']) {
  test(`retained sharing cannot exit or enable unsafely after ${outcome}`, async () => {
    const previousWindow = globalThis.window;
    const previousFetch = globalThis.fetch;
    const navigations = [];
    globalThis.window = { addEventListener() {}, removeEventListener() {}, location: { assign: url => navigations.push(url) } };
    const owner = mountDraft();
    const requests = [];
    let complete;
    globalThis.fetch = async (url, options) => {
      const body = JSON.parse(options.body);
      requests.push({ url, body });
      if (outcome === 'publication conflict') return new Response('{}', { status: 409 });
      if (outcome === 'publication failure' || url === '/editor/share-progress') return new Response('{}', { status: 500 });
      await new Promise(resolve => { complete = resolve; });
      const metadata = withOriginals({ ...owner.props.metadata, ...body.changes, shareProgressEnabled: false });
      return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
    };
    try {
      owner.editor.draft.isPublic = true;
      owner.editor.draft.shareProgressEnabled = true;
      const saving = owner.editor.saveAndExit();
      assert.deepEqual(requests[0], { url: '/editor/metadata/conditional', body: {
        expected: { isPublic: false }, changes: { isPublic: true } } });
      if (outcome === 'newer private draft') owner.editor.draft.isPublic = false;
      complete?.();
      await saving;
      assert.equal(requests.length, outcome === 'sharing failure' ? 2 : 1, 'no retry or sharing after failed/private publication');
      if (outcome === 'sharing failure') assert.deepEqual(requests[1], { url: '/editor/share-progress', body: { enabled: true } });
      assert.equal(owner.editor.isDirty, true);
      assert.deepEqual(navigations, []);
      if (outcome === 'newer private draft') assert.equal(owner.editor.draft.isPublic, false);
      else {
        assert.equal(owner.editor.draft.shareProgressEnabled, true);
        if (outcome === 'sharing failure') {
          assert.equal(owner.editor.statusText, 'Sharing change not confirmed');
          assert.equal(owner.editor.sharingUnconfirmed, true);
        } else {
          assert.equal(owner.editor.statusText, 'Save failed');
          assert.ok(owner.editor.saveError);
        }
      }
    } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
  });
}

// A share-progress reply owns only the submitted choice, just like a metadata reply.
test('sharing disabled during its own PATCH remains dirty until the next explicit save', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  const navigations = [];
  globalThis.window = { addEventListener() {}, removeEventListener() {}, location: { assign: url => navigations.push(url) } };
  const owner = mountDraft();
  const requests = [];
  let complete;
  globalThis.fetch = async (url, options) => {
    const body = JSON.parse(options.body);
    requests.push({ url, body });
    const metadata = withOriginals({ ...owner.props.metadata, shareProgressEnabled: body.enabled });
    await new Promise(resolve => { complete = resolve; });
    return new Response(JSON.stringify({ data: metadata, affected: { metadata }, warnings: [] }));
  };
  try {
    owner.props.metadata = withOriginals({ ...owner.props.metadata, isPublic: true, shareProgressEnabled: false });
    await nextTick();
    owner.editor.visibleShareProgressEnabled = true;
    const enabling = owner.editor.saveAndExit();
    owner.editor.visibleShareProgressEnabled = false;
    complete();
    await enabling;
    await nextTick();
    assert.equal(owner.props.metadata.shareProgressEnabled, true);
    assert.equal(owner.editor.draft.shareProgressEnabled, false);
    assert.equal(owner.editor.isDirty, true);
    assert.equal(owner.editor.statusText, 'Unsaved changes');
    assert.equal(requests.length, 1, 'the newer disable needs an explicit save');
    assert.deepEqual(navigations, []);
    const disabling = owner.editor.saveAndExit();
    assert.deepEqual(requests, [
      { url: '/editor/share-progress', body: { enabled: true } },
      { url: '/editor/share-progress', body: { enabled: false } }
    ]);
    complete();
    await disabling;
    assert.equal(owner.editor.isDirty, false);
    assert.equal(owner.props.metadata.shareProgressEnabled, false);
    assert.deepEqual(navigations, ['/User/Trip']);
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.fetch = previousFetch; }
});

test('tag edits authored during tag save survive its response and remain unsaved until their own acknowledgement', async () => {
  const previousWindow = globalThis.window;
  const previousFetch = globalThis.fetch;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  let complete;
  globalThis.fetch = async (_url, options) => {
    const { tags } = JSON.parse(options.body);
    await new Promise(resolve => { complete = resolve; });
    return new Response(JSON.stringify({ data: tags.map(name => ({ name, slug: name.toLowerCase() })), affected: {}, warnings: [] }));
  };
  try {
    owner.editor.draft.tags = ['First'];
    const saving = owner.editor.save(false);
    owner.editor.draft.tags.push('Newer');
    complete();
    await saving;
    assert.deepEqual([...owner.editor.draft.tags], ['First', 'Newer']);
    assert.equal(owner.editor.isTagsDirty, true);
    const nextSave = owner.editor.save(false);
    complete();
    await nextSave;
    assert.equal(owner.editor.isDirty, false);
    assert.deepEqual([...owner.editor.draft.tags], ['First', 'Newer']);
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
