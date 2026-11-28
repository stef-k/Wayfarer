import assert from 'node:assert/strict';
import test from 'node:test';
import { createSSRApp, h, nextTick, proxyRefs } from 'vue';
import { renderToString } from 'vue/server-renderer';
import { component, mountDraft } from './tripEditorExitHost.mjs';

test('captured viewport survives closed settings and unrelated refresh, with equivalent capture and reset staying clean', async () => {
  const previousWindow = globalThis.window;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  const owner = mountDraft();
  try {
    owner.editor.draft.centerLatitude = 38.5; // Vue's number input emits a number, not a string.
    owner.editor.draft.centerLongitude = 24;
    owner.editor.draft.zoom = 10;
    assert.equal(owner.editor.isDirty, true, 'manual numeric fields retain their existing save contract');
    await owner.editor.resetDraft();
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
    await owner.editor.resetDraft();
    assert.equal(owner.editor.isDirty, false);
    await owner.capture(38);
    assert.equal(owner.editor.isDirty, true, 'same viewport may be captured again after discard');
  } finally { owner.dispose(); globalThis.window = previousWindow; }
});

test('custom exit confirmation replaces the native warning only after approval', async () => {
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
    editor.draft.name = 'Edited trip';
    let writes = 0;
    globalThis.fetch = async () => { writes++; throw new Error('unexpected dispatch'); };
    await editor.saveAndExit();
    assert.equal(writes, 0);
    assert.equal(navigations.length, 0, 'a draft without available custody stays on the page');
    assert.equal(warns(), true, 'unavailable custody retains unload protection');

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
