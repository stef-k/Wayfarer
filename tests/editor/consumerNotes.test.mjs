import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { chromium } from '@playwright/test';

// Use the established SFC compiler seam in a native DOM. All forms, Notes, draft owners,
// normalization and parent scheduling are production code; only geography/confirmation/HTTP are substituted.
const bundle = await build({ stdin: { contents: `
  import App from './App.vue';
  import RichNotesEditor from './components/RichNotesEditor.vue';
  import { createApp, h, nextTick, proxyRefs, reactive } from 'vue';
  export { createApp, h, nextTick, reactive, RichNotesEditor };
  export function mountApp(state) {
    window.captured = {};
    window.capture = (name, bindings) => (captured[name] ??= []).push(proxyRefs(bindings));
    window.fetch = async () => new Response(JSON.stringify(state));
    window.app = createApp(App, { config: { editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
    app.mount('#app');
  }`, loader: 'ts', resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'iife', globalName: 'Probe',
  plugins: [{ name: 'actual-notes-owners', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (/ConfirmDialog\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: descriptor.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: script.bindings } });
      return { loader: 'ts', contents: `${script.content}\n${template.code}\n__component.render = render;
        const setup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = setup(props, context);
          window.capture?.(${JSON.stringify(args.path.split('/').at(-1))}, bindings);
          return bindings;
        }; export default __component;` };
    });
    builder.onLoad({ filter: /\.css$/ }, () => ({ contents: '', loader: 'js' }));
    builder.onLoad({ filter: /leafletAdapter\.ts$/ }, () => ({ contents: `export const createTripEditorMap = () => ({
      render() {}, dispose() {}, clearSearchPreview() {}, selectPlace() {}, selectSegment() {}, setSegmentPresentation() {},
      setSegmentDraftPreview() {}, setPlaceDraftPreview() {}, setSegmentRouteWorkState() {},
      getMapView: () => ({ center: { latitude: 10, longitude: 20 }, zoom: 5 }) });
      export const hasAnyGeometry = () => true; export const hasSavedTripView = () => false;
      export const canFocusActiveEntity = () => true;` }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: `export const confirm = async () => true;
      export const disposeConfirmDialogHost = () => {}; export const setConfirmDialogFocusFallback = () => {};` }));
  } }] });
const script = bundle.outputFiles.find(file => !file.path.endsWith('.css')).text;
const original = '<p class="ql-align-center">Original</p>';
const capabilities = { canEdit: true, canDelete: true, canAddChildren: true, canReorder: true, canMove: true, canRename: true };
const region = { id: 'region', tripId: 'trip', name: 'Region', notesHtml: original, coverImage: null, center: null, displayOrder: 1,
  isShadow: false, capabilities, originalValues: { name: 'Region', notesHtml: original, coverImage: null, center: null } };
const place = { id: 'place', tripId: 'trip', regionId: 'region', name: 'Place', notesHtml: original, address: '',
  location: { latitude: 10, longitude: 20 }, iconName: 'marker', markerColor: 'bg-blue', displayOrder: 1, capabilities,
  visitSummary: { placeId: 'place', visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
  originalValues: { regionId: 'region', name: 'Place', notesHtml: original, address: '', iconName: 'marker', markerColor: 'bg-blue', displayOrder: 1 } };
const area = { id: 'area', tripId: 'trip', regionId: 'region', name: 'Area', notesHtml: original, fillHex: '#ff6600', displayOrder: 1,
  geometry: { type: 'Polygon', coordinates: [[[0,0],[1,0],[1,1],[0,0]]] }, capabilities,
  originalValues: { regionId: 'region', name: 'Area', notesHtml: original, fillHex: '#ff6600', geometry: 'area-polygon-1:proof' } };
const route = { type: 'LineString', coordinates: [[20,10],[21,11]] };
const segment = { id: 'segment', tripId: 'trip', fromPlaceId: 'place', toPlaceId: 'place', waypointPlaceIds: [], waypointRouteVertexIndices: [],
  mode: 'walk', transportProfileId: null, hasCustomRoute: true, estimatedDistanceKm: 1, estimatedDurationMinutes: 10,
  estimatedDurationSource: 'Manual', notesHtml: original, route, effectiveRoute: route, originalRouteToken: 'route',
  aggregateConcurrencyToken: 'aggregate', displayOrder: 1, capabilities, originalValues: { notesHtml: original } };
const metadata = { id: 'trip', name: 'Trip', notesHtml: original, isPublic: false, shareProgressEnabled: false, coverImage: null,
  center: null, zoom: null, updatedAt: '2026-11-03T00:00:00Z',
  originalValues: { name: 'Trip', notesHtml: original, isPublic: false, coverImage: null, center: null, zoom: null } };
const state = { tripId: 'trip', metadata, regionsById: { region }, regionOrder: ['region'], placesById: { place }, placeOrderByRegionId: { region: ['place'] },
  areasById: { area }, areaOrderByRegionId: { region: ['area'] }, segmentsById: { segment }, segmentOrder: ['segment'], tagsBySlug: {}, tagOrder: [],
  options: { transportModes: [{ value: 'walk', label: 'Walk' }], iconNames: ['marker'], markerColorClasses: ['bg-blue'],
    areaDefaults: { name: 'Area', fillHex: '#ff6600' }, tag: { maxCount: 10, maxNameLength: 20, suggestions: [] } },
  permissions: { canEditMetadata: true, canEditRegions: true, canEditPlaces: true, canEditAreas: true, canEditSegments: true },
  visitProgress: { totalPlaces: 1, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: { place: place.visitSummary }, historyRows: [] } };

/** Refresh only this consumer's affected slice, through the actual App mutation owner. */
const envelope = (kind, data) => ({ success: true, data, affected: { metadata: kind === 'metadata' ? data : null,
  regions: kind === 'region' ? [data] : [], places: kind === 'place' ? [data] : [], areas: kind === 'area' ? [data] : [],
  segments: kind === 'segment' ? [data] : [], tags: [], placeOrdersByRegionId: {}, areaOrdersByRegionId: {}, regionOrder: null,
  segmentOrder: null, tagOrder: null, visitProgress: null, options: null },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });

const owners = [
  ['metadata', 'MetadataEditor.vue', 'draft', 'openMetadata', 'save', 'resetDraft', metadata],
  ['region', 'RegionManager.vue', 'draft', 'openEdit', 'saveDraft', 'resetDraft', region],
  ['place', 'RegionManager.vue', 'placeDraft', 'openPlaceEdit', 'savePlaceDraft', 'resetPlaceDraft', place],
  ['area', 'RegionManager.vue', 'areaDraft', 'openAreaEdit', 'saveAreaDraft', 'resetAreaDraft', area],
  ['segment', 'SegmentManager.vue', 'draft', 'openEdit', 'saveDraft', 'resetDraft', segment]
];

// Replay local emissions through each actual parent draft on separate ticks, including repeated HTML.
test('repeated FIFO Vue echoes preserve newer typing, selection and undo in every Trip owner', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const emptyNotes = entity => ({ ...entity, notesHtml: '', originalValues: { ...entity.originalValues, notesHtml: '' } });
  for (const echoBeforeTyping of [false, true]) {
    for (const [kind, component, draft, open, , , entity] of owners) {
      await t.test(`${kind}, first echo before final typing: ${echoBeforeTyping}`, async () => {
        const page = await browser.newPage();
        try {
          await page.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<div id="trip-editor-app" data-account-id="account" data-trip-id="trip"><div id="app"></div></div>' }));
          await page.goto('http://notes.test/');
          await page.addScriptTag({ content: script });
          await page.evaluate(state => Probe.mountApp(state), { ...state, metadata: emptyNotes(metadata), regionsById: { region: emptyNotes(region) },
            placesById: { place: emptyNotes(place) }, areasById: { area: emptyNotes(area) }, segmentsById: { segment: emptyNotes(segment) } });
          await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
          const result = await page.evaluate(async ({ echoBeforeTyping, component, draft, open, entity }) => {
            captured['App.vue'][0].editorSurface.clearActiveTarget();
            const owner = captured[component][0];
            await owner[open](entity); await Probe.nextTick();
            const handle = captured['RichNotesEditor.vue'].findLast(value => value.handle && !value.handle.editor.isDestroyed).handle;
            const emitted = [];
            handle.onChange(() => emitted.push(handle.getValue()));
            const editor = handle.editor;
            editor.commands.insertContent('A');
            editor.commands.selectAll(); editor.commands.toggleBold(); editor.commands.toggleBold();
            if (echoBeforeTyping) { owner[draft].notesHtml = emitted[0]; await Probe.nextTick(); }
            editor.commands.focus('end'); editor.commands.insertContent('C');
            const selection = editor.state.selection;
            const deliveries = [];
            for (const value of emitted.slice(echoBeforeTyping ? 1 : 0)) {
              owner[draft].notesHtml = value;
              await Probe.nextTick();
              deliveries.push({ html: handle.getValue(), undo: editor.can().undo(), selection: editor.state.selection.eq(selection) });
            }
            const sequence = [...emitted], finalDraft = owner[draft].notesHtml;
            editor.commands.undo(); const undone = handle.getValue();
            editor.commands.redo(); const redone = handle.getValue();
            app.unmount();
            return { sequence, finalDraft, deliveries, undone, redone };
          }, { echoBeforeTyping, component, draft, open, entity: emptyNotes(entity) });
          assert.deepEqual(result.sequence, ['<p>A</p>', '<p><strong>A</strong></p>', '<p>A</p>', '<p>AC</p>']);
          for (const delivery of result.deliveries) assert.deepEqual(delivery, { html: '<p>AC</p>', undo: true, selection: true });
          assert.equal(result.finalDraft, '<p>AC</p>');
          assert.notEqual(result.undone, '<p>AC</p>'); assert.equal(result.redone, '<p>AC</p>');
        } finally { await page.close(); }
      });
    }
  }
});

// Two native edits can coalesce to the old prop; a later owner refresh is still an adoption.
test('coalesced reversal then authoritative refresh adopts Notes in every Trip owner', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const plain = entity => ({ ...entity, notesHtml: '<p>A</p>', originalValues: { ...entity.originalValues, notesHtml: '<p>A</p>' } });
  for (const [kind, component, draft, open, save, , entity] of owners) {
    await t.test(kind, async () => {
      const page = await browser.newPage();
      try {
        await page.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<div id="trip-editor-app" data-account-id="account" data-trip-id="trip"><div id="app"></div></div>' }));
        await page.goto('http://notes.test/');
        await page.addScriptTag({ content: script });
        await page.evaluate(state => Probe.mountApp(state), { ...state, metadata: plain(metadata), regionsById: { region: plain(region) },
          placesById: { place: plain(place) }, areasById: { area: plain(area) }, segmentsById: { segment: plain(segment) } });
        await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
        const result = await page.evaluate(async ({ component, draft, open, save, entity, refreshed }) => {
          const parent = captured['App.vue'][0];
          parent.editorSurface.clearActiveTarget();
          const owner = captured[component][0];
          await owner[open](entity); await Probe.nextTick();
          const handle = captured['RichNotesEditor.vue'].findLast(value => value.handle && !value.handle.editor.isDestroyed).handle;
          handle.editor.commands.selectAll(); handle.editor.commands.toggleBold(); handle.editor.commands.toggleBold();
          await Probe.nextTick();
          const reversal = { displayed: handle.getValue(), draft: owner[draft].notesHtml, undo: handle.editor.can().undo() };
          parent.applyMutation(refreshed); await Probe.nextTick();
          const adopted = { displayed: handle.getValue(), draft: owner[draft].notesHtml, undo: handle.editor.can().undo(), redo: handle.editor.can().redo() };
          handle.editor.commands.focus('end'); handle.editor.commands.insertContent('C'); await Probe.nextTick();
          const typed = { displayed: handle.getValue(), draft: owner[draft].notesHtml };
          let call;
          window.fetch = async (url, init) => { call = { method: init.method, body: JSON.parse(init.body) };
            return new Response(JSON.stringify({ code: 'validation', errors: { notesHtml: ['Check Notes'] } }), { status: 400 }); };
          await owner[save](false);
          app.unmount();
          return { reversal, adopted, typed, call };
        }, { component, draft, open, save, entity: plain(entity), refreshed: envelope(kind, { ...entity,
          notesHtml: '<p><strong>A</strong></p>', originalValues: { ...entity.originalValues, notesHtml: '<p><strong>A</strong></p>' } }) });
        assert.deepEqual(result.reversal, { displayed: '<p>A</p>', draft: '<p>A</p>', undo: true });
        assert.deepEqual(result.adopted, { displayed: '<p><strong>A</strong></p>', draft: '<p><strong>A</strong></p>', undo: false, redo: false });
        assert.deepEqual(result.typed, { displayed: '<p><strong>AC</strong></p>', draft: '<p><strong>AC</strong></p>' });
        assert.deepEqual(result.call, { method: 'PATCH', body: { expected: { ...(kind === 'place' || kind === 'area' ? { regionId: 'region' } : {}),
          notesHtml: '<p><strong>A</strong></p>' }, changes: { notesHtml: '<p><strong>AC</strong></p>' } } });
      } finally { await page.close(); }
    });
  }
});

// Exercise Reset through the actual creation form and owner, including an unchanged empty Notes value.
test('Region, Place and Area creation Reset retire Notes redo and dialogs', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  for (const [kind, draft, open, reset] of [
    ['region', 'draft', 'openCreate', 'resetDraft'],
    ['place', 'placeDraft', 'openPlaceCreate', 'resetPlaceDraft'],
    ['area', 'areaDraft', 'openAreaCreate', 'resetAreaDraft']
  ]) {
    await t.test(kind, async () => {
      const page = await browser.newPage();
      try {
        await page.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<div id="trip-editor-app" data-account-id="account" data-trip-id="trip"><div id="app"></div></div>' }));
        await page.goto('http://notes.test/');
        await page.addScriptTag({ content: script });
        await page.evaluate(state => Probe.mountApp(state), {
          ...state, options: { ...state.options, areaDefaults: { name: 'Custom Area', fillHex: '#123456' } }
        });
        await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
        const results = await page.evaluate(async ({ kind, draft, open, reset, region }) => {
          captured['App.vue'][0].editorSurface.clearActiveTarget();
          const owner = captured['RegionManager.vue'][0];
          await owner[open](region); await Probe.nextTick();
          const current = () => captured['RichNotesEditor.vue'].findLast(value => value.handle && !value.handle.editor.isDestroyed);
          const defaults = { name: owner[draft].name, parent: owner[draft].regionId, fill: owner[draft].fillHex };
          const results = [];
          for (const withDialog of [false, true]) {
            const notes = current(), handle = notes.handle;
            handle.editor.commands.insertContent('discarded note');
            handle.editor.commands.undo(); await Probe.nextTick();
            const beforeReset = handle.getValue();
            owner[draft].name = 'Changed Name'; await Probe.nextTick();
            const button = [...document.querySelectorAll('button')].find(button => button.textContent.trim() === 'Reset');
            const enabled = button && !button.disabled;
            if (withDialog) { notes.openDialog('link'); await Probe.nextTick(); }
            // Existing busy Place/Area resets must keep the live session and draft intact.
            let busyPreserved = true;
            if (kind !== 'region') {
              owner.isSaving = true; owner[reset](); await Probe.nextTick();
              busyPreserved = !handle.editor.isDestroyed && owner[draft].name === 'Changed Name';
              owner.isSaving = false; await Probe.nextTick();
            }
            if (withDialog) owner[reset](); else button.click();
            await Probe.nextTick();
            current().handle.editor.commands.redo(); await Probe.nextTick();
            results.push({ beforeReset, enabled, busyPreserved, retired: handle.editor.isDestroyed,
              dialogs: document.querySelectorAll('dialog[open]').length, html: current().handle.getValue(), draft: owner[draft].notesHtml,
              defaultsRetained: defaults.name === owner[draft].name && defaults.parent === owner[draft].regionId && defaults.fill === owner[draft].fillHex });
          }
          app.unmount();
          return results;
        }, { kind, draft, open, reset, region });
        for (const result of results) assert.deepEqual(result, { beforeReset: '', enabled: true, busyPreserved: true,
          retired: true, dialogs: 0, html: '', draft: '', defaultsRetained: true });
      } finally { await page.close(); }
    });
  }
});

test('actual Vue Notes emissions retain all five Trip owner contracts', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  for (const [kind, component, draft, open, save, reset, entity] of owners) {
    await t.test(kind, async () => {
      const page = await browser.newPage();
      try {
        await page.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<div id="trip-editor-app" data-account-id="account" data-trip-id="trip"><div id="app"></div></div>' }));
        await page.goto('http://notes.test/');
        await page.addScriptTag({ content: script });
        await page.evaluate(state => Probe.mountApp(state), state);
        await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
        const result = await page.evaluate(async ({ kind, component, draft, open, save, reset, entity, original, savedEnvelope, refreshedEnvelope }) => {
          const parent = captured['App.vue'][0];
          parent.editorSurface.clearActiveTarget();
          const owner = captured[component][0];
          await owner[open](entity);
          await Probe.nextTick();
          const notes = captured['RichNotesEditor.vue'].findLast(value => value.handle && !value.handle.editor.isDestroyed);
          let handle = notes.handle;
          const current = () => captured['RichNotesEditor.vue'].findLast(value => value.handle && !value.handle.editor.isDestroyed).handle;
          const before = owner[draft].notesHtml;
          handle.focus();
          await Probe.nextTick();
          const untouched = owner[draft].notesHtml;
          handle.editor.commands.selectAll();
          handle.editor.commands.insertContent('First');
          await Probe.nextTick();
          const emitted = owner[draft].notesHtml;
          const selection = handle.editor.state.selection;
          // A prop echo must leave the native document, range and undo stack live.
          const afterEcho = handle.editor.state.selection.eq(selection);
          let settle;
          const calls = [];
          window.fetch = async (url, init) => { calls.push({ url, method: init.method, body: JSON.parse(init.body) }); return new Promise(resolve => { settle = resolve; }); };
          const saving = owner[save](false);
          handle.editor.commands.insertContent(' newer');
          await Probe.nextTick();
          parent.applyMutation(refreshedEnvelope);
          await Probe.nextTick();
          const duringRefresh = owner[draft].notesHtml;
          const newerSelection = handle.editor.state.selection;
          settle(new Response(JSON.stringify(savedEnvelope)));
          await saving;
          await Probe.nextTick();
          const afterSave = owner[draft].notesHtml;
          const displayed = handle.getValue();
          const historyRetained = handle.editor.can().undo() && handle.editor.state.selection.eq(newerSelection);
          // Explicit reset adopts authoritative saved Notes and discards only this live history.
          owner[reset]();
          await Probe.nextTick();
          const resetRetired = handle.editor.isDestroyed;
          handle = current();
          const adopted = handle.getValue();
          handle.editor.commands.undo();
          const undoAfterReset = handle.getValue();
          // Reset must retire discarded redo even when undo already returned Notes to the saved value.
          handle.editor.commands.focus('end');
          handle.editor.commands.insertContent(' discarded');
          handle.editor.commands.undo();
          await Probe.nextTick();
          owner[reset]();
          await Probe.nextTick();
          handle = current();
          handle.editor.commands.redo();
          const redoAfterSameValueReset = handle.getValue();
          handle.clear();
          await Probe.nextTick();
          window.fetch = async (url, init) => { calls.push({ url, method: init.method, body: JSON.parse(init.body) }); return new Response(JSON.stringify({ code: 'validation', errors: { notesHtml: ['Check Notes'] } }), { status: 400 }); };
          await owner[save](false);
          const empty = owner[draft].notesHtml;
          const emptyCall = calls.at(-1);
          // A remount is a fresh native history with the feature-owned draft, never another owner snapshot.
          parent.editorSurface.clearActiveTarget();
          await Probe.nextTick();
          await owner[open](entity);
          await Probe.nextTick();
          const remount = captured['RichNotesEditor.vue'].findLast(value => value.handle && !value.handle.editor.isDestroyed).handle;
          const remounted = remount.getValue();
          app.unmount();
          return { before, untouched, emitted, afterEcho, duringRefresh, afterSave, displayed, adopted, undoAfterReset, empty,
            calls, emptyCall, historyRetained, resetRetired, redoAfterSameValueReset, retired: handle.editor.isDestroyed, remounted, remountRetired: remount.editor.isDestroyed };
        }, { kind, component, draft, open, save, reset, entity, original,
          savedEnvelope: envelope(kind, { ...entity, notesHtml: '<p>First</p>', originalValues: { ...entity.originalValues, notesHtml: '<p>First</p>' } }),
          refreshedEnvelope: envelope(kind, { ...entity, notesHtml: '<p>Other device</p>', originalValues: { ...entity.originalValues, notesHtml: '<p>Other device</p>' } }) });
        assert.equal(result.before, original); assert.equal(result.untouched, original);
        assert.equal(result.emitted, '<p>First</p>'); assert.equal(result.afterEcho, true);
        assert.equal(result.duringRefresh, '<p>First newer</p>'); assert.equal(result.afterSave, '<p>First newer</p>');
        assert.equal(result.historyRetained, true);
        assert.equal(result.displayed, '<p>First newer</p>'); assert.equal(result.adopted, '<p>First</p>');
        assert.equal(result.undoAfterReset, '<p>First</p>'); assert.equal(result.resetRetired, true);
        assert.equal(result.redoAfterSameValueReset, '<p>First</p>'); assert.equal(result.empty, '');
        assert.deepEqual(result.calls[0].body.expected, ['place', 'area'].includes(kind) ? { regionId: 'region', notesHtml: original } : { notesHtml: original });
        assert.deepEqual(result.calls[0].body.changes, { notesHtml: '<p>First</p>' });
        assert.deepEqual(result.emptyCall.body.changes, { notesHtml: '' });
        assert.equal(result.retired, true); assert.equal(result.remountRetired, true);
        assert.equal(result.remounted, kind === 'metadata' ? '' : original);
      } finally { await page.close(); }
    });
  }
});

// This is the component synchronization seam, including batched/delayed parent echoes and two independent mounts.
test('Vue echo, external adoption, dialogs and instance cleanup stay isolated', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  await page.setContent('<div id="trip-editor-app" data-account-id="account" data-trip-id="trip"><div id="app"></div></div>');
  await page.addScriptTag({ content: script });
  const result = await page.evaluate(async () => {
    window.captured = {};
    window.capture = (name, value) => (captured[name] ??= []).push(value);
    const values = Probe.reactive({ left: '<p>Left</p>', right: '<p>Right</p>' });
    const adoptions = Probe.reactive({ left: 0, right: 0 });
    const emitted = [];
    const app = Probe.createApp({ render: () => Probe.h('div', ['left', 'right'].map(key => Probe.h(Probe.RichNotesEditor, {
      editorId: 'same-key', label: key, adoptionRevision: adoptions[key], modelValue: values[key], validationMessages: ['Check this field'],
      'onUpdate:modelValue': value => emitted.push([key, value])
    }))) });
    app.mount('#app');
    const [left, right] = captured['RichNotesEditor.vue'];
    left.handle.editor.commands.focus('end'); left.handle.editor.commands.insertContent(' one');
    const earlier = emitted.at(-1)[1];
    left.handle.editor.commands.insertContent(' two');
    values.left = earlier;
    await Probe.nextTick();
    const newer = left.handle.getValue();
    values.left = emitted.at(-1)[1];
    await Probe.nextTick();
    left.handle.editor.commands.undo();
    const undone = left.handle.getValue();
    values.left = '<p>External</p>'; adoptions.left++;
    await Probe.nextTick();
    left.handle.editor.commands.undo();
    const adopted = left.handle.getValue();
    const ids = [...document.querySelectorAll('[id]')].map(element => element.id);
    const leftHandle = left.handle, rightHandle = right.handle;
    left.openDialog('link');
    await Probe.nextTick();
    const leftOpen = document.querySelectorAll('dialog[open]').length;
    left.dialog.value.close();
    right.openDialog('image');
    await Probe.nextTick();
    right.url.value = 'data:image/png;base64,AA';
    right.applyUrl();
    await Probe.nextTick();
    const rightError = document.querySelector('dialog[open]').textContent;
    const rightValue = rightHandle.getValue();
    app.unmount();
    return { newer, undone, adopted, idsUnique: new Set(ids).size === ids.length, leftOpen, rightError, rightValue,
      dialogsAfterUnmount: document.querySelectorAll('dialog').length, disposed: leftHandle.editor.isDestroyed && rightHandle.editor.isDestroyed };
  });
  assert.equal(result.newer, '<p>Left one two</p>'); assert.equal(result.undone, '<p>Left</p>');
  assert.equal(result.adopted, '<p>External</p>'); assert.equal(result.idsUnique, true);
  assert.equal(result.leftOpen, 1); assert.match(result.rightError, /absolute HTTP\(S\) image URL/);
  assert.equal(result.rightValue, '<p>Right</p>'); assert.equal(result.dialogsAfterUnmount, 0); assert.equal(result.disposed, true);
});
