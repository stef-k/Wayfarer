import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { chromium } from '@playwright/test';

// Use the established SFC compiler seam in a native DOM. All forms, Notes, draft owners,
// normalization and parent scheduling are production code; geography and HTTP use fixture data.
const bundle = await build({ stdin: { contents: `
  import App from './App.vue';
  import RichNotesEditor from './components/RichNotesEditor.vue';
  import { createApp, h, nextTick, proxyRefs, reactive, watch } from 'vue';
  import { confirmDialogState, settleConfirmDialog } from './composables/useConfirmDialog';
  import { metadataAccount } from './recovery/recoverMetadata';
  export { tripReceiptReplies, tripReceipt } from '../../../tests/editor/tripReceiptReplies.mjs';
  export { createTripEditorMap } from './map/leafletAdapter';
  export { createApp, h, nextTick, reactive, RichNotesEditor };
  export function mountApp(state) {
    window.captured = {};
    window.capture = (name, bindings) => (captured[name] ??= []).push(proxyRefs(bindings));
    window.fetch = async () => new Response(JSON.stringify(state));
    // Notes tests approve incidental transitions. Feedback tests exercise the rendered confirmation.
    watch(confirmDialogState, dialog => {
      if (dialog && !window.useMountedConfirmation) settleConfirmDialog(true);
    });
    window.app = createApp(App, { config: { editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
    app.provide(metadataAccount, document.getElementById('trip-editor-app').dataset.accountId);
    app.mount('#app');
  }`, loader: 'ts', resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'iife', globalName: 'Probe',
  plugins: [{ name: 'actual-notes-owners', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
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
    // Match Vite's raw import for the production adapter's vendored drawing plugin.
    builder.onLoad({ filter: /leaflet\.draw\.js$/ }, async args => ({ contents: await readFile(args.path, 'utf8'), loader: 'text' }));
    // Keep geography stubbed for App tests. The map-label case mounts the real adapter directly.
    builder.onResolve({ filter: /^\.\/map\/leafletAdapter$/ }, args =>
      args.importer.endsWith('/App.vue') ? { path: 'app-map', namespace: 'test' } : undefined);
    builder.onLoad({ filter: /^app-map$/, namespace: 'test' }, () => ({ contents: `export const createTripEditorMap = (_element, _tilesUrl, callbacks) => {
      if (window.useRealEditorMap) return Probe.createTripEditorMap(_element, _tilesUrl, callbacks);
      window.selectSegmentFromMap = callbacks.onSegmentSelected;
      return {
        render() {}, dispose() {}, clearSearchPreview() {}, selectPlace() {}, selectSegment() {}, setSegmentPresentation() {},
        setSegmentDraftPreview() {}, setPlaceDraftPreview() {}, setSegmentRouteWorkState() {},
        fitAllGeometry: () => window.mapActionResults?.fitAll ?? 'moved',
        focusSavedTripView: () => window.mapActionResults?.recenter ?? 'moved',
        focusActiveEntity: () => window.mapActionResults?.focus ?? 'moved',
        getMapView: () => ({ center: { latitude: 10, longitude: 20 }, zoom: 5 })
      };
    };
      export const hasAnyGeometry = () => true; export const hasSavedTripView = metadata => Boolean(metadata.center);
      export const canFocusActiveEntity = () => true;` }));
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
  center: null, zoom: null, updatedAt: '2026-11-03T00:00:00Z', contributionPolicy: null, credits: [],
  originalValues: { name: 'Trip', notesHtml: original, isPublic: false, coverImage: null, center: null, zoom: null, contributionPolicy: null, credits: [] } };
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
        await page.goto('http://localhost/');
        await page.addScriptTag({ content: script });
        await page.evaluate(state => Probe.mountApp(state), { ...state, metadata: plain(metadata), regionsById: { region: plain(region) },
          placesById: { place: plain(place) }, areasById: { area: plain(area) }, segmentsById: { segment: plain(segment) } });
        await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
        if (kind === 'metadata') await page.locator('#trip-editor-metadata-form:not([inert])').waitFor();
        const result = await page.evaluate(async ({ kind, component, draft, open, save, entity, refreshed }) => {
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
          const reject = async (url, init) => { call = { method: init.method, body: JSON.parse(init.body) };
            return new Response(JSON.stringify({ code: 'validation', errors: { notesHtml: ['Check Notes'] } }), { status: 400 }); };
          window.fetch = kind === 'metadata' ? Probe.tripReceiptReplies(() => refreshed.data, reject) : reject;
          await owner[save](false);
          app.unmount();
          return { reversal, adopted, typed, call };
        }, { kind, component, draft, open, save, entity: plain(entity), refreshed: envelope(kind, { ...entity,
          notesHtml: '<p><strong>A</strong></p>', originalValues: { ...entity.originalValues, notesHtml: '<p><strong>A</strong></p>' } }) });
        assert.deepEqual(result.reversal, { displayed: '<p>A</p>', draft: '<p>A</p>', undo: true });
        assert.deepEqual(result.adopted, { displayed: '<p><strong>A</strong></p>', draft: '<p><strong>A</strong></p>', undo: false, redo: false });
        assert.deepEqual(result.typed, { displayed: '<p><strong>AC</strong></p>', draft: '<p><strong>AC</strong></p>' });
        assert.deepEqual(result.call, { method: 'PATCH', body: { ...(kind === 'metadata' ? { receiptContract: 'trip-metadata-receipt-2' } : {}),
          expected: { ...(kind === 'place' || kind === 'area' ? { regionId: 'region' } : {}),
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
        await page.goto('http://localhost/');
        await page.addScriptTag({ content: script });
        await page.evaluate(state => Probe.mountApp(state), state);
        await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
        if (kind === 'metadata') await page.locator('#trip-editor-metadata-form:not([inert])').waitFor();
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
          const untouchedCalls = [];
          window.fetch = async (url, init) => { untouchedCalls.push({ url, body: init?.body }); return new Response('{}'); };
          handle.focus();
          await Probe.nextTick();
          const untouched = owner[draft].notesHtml;
          if (kind !== 'segment') await owner[save](false);
          handle.editor.commands.selectAll();
          handle.editor.commands.insertContent('First');
          await Probe.nextTick();
          const emitted = owner[draft].notesHtml;
          const selection = handle.editor.state.selection;
          // A prop echo must leave the native document, range and undo stack live.
          const afterEcho = handle.editor.state.selection.eq(selection);
          let settle;
          let dispatched;
          const dispatch = new Promise(resolve => { dispatched = resolve; });
          const calls = [];
          const write = async (url, init) => {
            calls.push({ url, method: init.method, body: JSON.parse(init.body) });
            return new Promise(resolve => { settle = resolve; dispatched(); });
          };
          window.fetch = kind === 'metadata' ? Probe.tripReceiptReplies(() => savedEnvelope.data,
            async (url, init, permit) => {
              await write(url, init);
              return Response.json(Probe.tripReceipt(permit, 'applied', { fieldDigests: { notes: 'sha256:' + 'a'.repeat(64) } }));
            }) : write;
          const saving = owner[save](false);
          // Metadata can await its publication decision before the request captures the draft.
          await Promise.race([dispatch, saving.then(() => { throw new Error('Save completed without dispatching'); })]);
          let duringRefresh;
          let newerSelection;
          try {
            handle.editor.commands.insertContent(' newer');
            await Probe.nextTick();
            parent.applyMutation(refreshedEnvelope);
            await Probe.nextTick();
            duringRefresh = owner[draft].notesHtml;
            newerSelection = handle.editor.state.selection;
          } finally {
            settle(new Response(JSON.stringify(savedEnvelope)));
          }
          await saving;
          await Probe.nextTick();
          const afterSave = owner[draft].notesHtml;
          const displayed = handle.getValue();
          const historyRetained = handle.editor.can().undo() && handle.editor.state.selection.eq(newerSelection);
          // Explicit reset adopts authoritative saved Notes and discards only this live history.
          await owner[reset]();
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
          await owner[reset]();
          await Probe.nextTick();
          handle = current();
          handle.editor.commands.redo();
          const redoAfterSameValueReset = handle.getValue();
          handle.clear();
          await Probe.nextTick();
          const reject = async (url, init) => { calls.push({ url, method: init.method, body: JSON.parse(init.body) }); return new Response(JSON.stringify({ code: 'validation', errors: { notesHtml: ['Check Notes'] } }), { status: 400 }); };
          window.fetch = kind === 'metadata' ? Probe.tripReceiptReplies(() => savedEnvelope.data, reject) : reject;
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
          return { before, untouched, untouchedCalls, emitted, afterEcho, duringRefresh, afterSave, displayed, adopted, undoAfterReset, empty,
            calls, emptyCall, historyRetained, resetRetired, redoAfterSameValueReset, retired: handle.editor.isDestroyed, remounted, remountRetired: remount.editor.isDestroyed };
        }, { kind, component, draft, open, save, reset, entity, original,
          savedEnvelope: envelope(kind, { ...entity, notesHtml: '<p>First</p>', originalValues: { ...entity.originalValues, notesHtml: '<p>First</p>' } }),
          refreshedEnvelope: envelope(kind, { ...entity, notesHtml: '<p>Other device</p>', originalValues: { ...entity.originalValues, notesHtml: '<p>Other device</p>' } }) });
        assert.equal(result.before, '<p>Original</p>'); assert.equal(result.untouched, '<p>Original</p>');
        assert.deepEqual(result.untouchedCalls, []);
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
        assert.equal(result.remounted, kind === 'metadata' ? '' : '<p>Original</p>');
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

test('Greek Segment feedback keeps the bound waypoint, proposal and stale draft actions', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, { ...state, segmentsById: { segment: {
    ...segment, title: 'Authored <walk> & route', waypointPlaceIds: ['stop'], waypointRouteVertexIndices: [1],
    externalRouting: { available: true, providerDisplayName: 'Geoapify', disclosure: 'Route coordinates are sent to Geoapify.',
      modes: [{ key: 'walk', label: 'Walk' }] }
  } }, placesById: { place, stop: { ...place, id: 'stop', name: 'Authored <stop> & name' } },
  placeOrderByRegionId: { region: ['place', 'stop'] } });
  const hide = page.getByRole('button', { name: 'Απόκρυψη τμήματος διαδρομής' });
  assert.equal(await hide.getAttribute('title'), 'Απόκρυψη τμήματος διαδρομής');
  await hide.click();
  await page.getByRole('button', { name: 'Εμφάνιση τμήματος διαδρομής' }).click();
  await page.evaluate(async () => {
    const manager = captured['SegmentManager.vue'][0];
    await manager.openEdit(captured['App.vue'][0].state.segmentsById.segment);
    manager.draft.estimatedDurationMinutes = '12';
    await Probe.nextTick();
    // Issue the same reply shape as the server's indexed route validator.
    window.fetch = async () => Response.json({ errors: { 'waypointPlaceIds[0]': ['The waypoint anchor requires a location.'] } }, { status: 400 });
    await manager.saveDraft();
  });
  const row = page.locator('[data-waypoint-client-id]');
  await row.waitFor();
  const boundId = await row.getAttribute('aria-errormessage');
  assert.ok(boundId);
  assert.match(await page.locator(`[id="${boundId}"]`).textContent(), /Ορίστε συντεταγμένες για αυτήν την ενδιάμεση στάση/);
  assert.equal(await row.evaluate(node => node === document.activeElement), true);
  assert.equal(await row.locator('option:checked').textContent(), 'Authored <stop> & name');
  await page.getByText('Ενδιάμεση στάση 1: Authored <stop> & name', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Αφαίρεση: Authored <stop> & name', exact: true }).waitFor();
  const summary = page.locator('[data-segment-field="route"] p');
  assert.equal(await summary.textContent(), 'Μη αποθηκευμένη διαδρομή · 2 προσαρμοσμένα σημεία διαδρομής');

  const proposal = { proposalId: 'proposal', segmentId: 'segment', protectedContext: 'original-proof',
    geometry: [{ longitude: 20, latitude: 10 }, { longitude: 22, latitude: 11 }],
    waypointIndices: [0, 1, 2], distanceMetres: 1250, durationSeconds: 120 };
  await page.evaluate(proposal => { window.fetch = async () => Response.json(proposal); }, proposal);
  await page.locator('[id^="provider-mode-"]').selectOption('walk');
  await page.getByRole('button', { name: 'Δημιουργία διαδρομής', exact: true }).click();
  await page.getByText('1,25 χλμ.', { exact: true }).waitFor();

  await page.evaluate(async () => {
    window.fetch = async (_url, request) => {
      window.submittedProposal = JSON.parse(request.body).proposal;
      return Response.json({ code: 'route-proposal-invalid-or-expired',
        errors: { proposal: ['The route proposal cannot be saved. Discard it or explicitly generate a new proposal.'] } }, { status: 400 });
    };
    await captured['SegmentManager.vue'][0].saveDraft();
  });
  await page.getByText(/Η πρόταση διαδρομής δεν είναι έγκυρη ή έχει λήξει/).waitFor();
  assert.equal(await page.evaluate(() => submittedProposal.protectedContext), 'original-proof');
  await page.getByRole('button', { name: /Απόρριψη πρότασης/ }).click();
  await page.getByRole('button', { name: 'Δημιουργία διαδρομής', exact: true }).waitFor();

  await page.evaluate(async () => {
    const manager = captured['SegmentManager.vue'][0];
    window.fetch = async () => Response.json({ code: 'segment-route-stale',
      currentSegment: { ...captured['App.vue'][0].state.segmentsById.segment, mode: 'Historic mode',
        estimatedDistanceKm: 12.5, estimatedDurationMinutes: null, estimatedDurationSource: 'Automatic' },
      warning: 'The Segment changed.' }, { status: 409 });
    await manager.saveDraft();
  });
  await page.getByText(/Το αποθηκευμένο τμήμα ή η διαδρομή του άλλαξε/).waitFor();
  await page.locator('#segment-conflict-heading + p').waitFor();
  const reload = page.locator('.segment-conflict button');
  assert.ok(await reload.isEnabled());
  assert.equal(await page.locator('#segment-conflict-heading').evaluate(node => node === document.activeElement), true);
  assert.equal(await page.evaluate(() => captured['SegmentManager.vue'][0].draft.estimatedDurationMinutes), '12');
  const conflict = page.locator('.segment-conflict');
  assert.match(await conflict.textContent(), /Historic mode.*12,5 χλμ\..*Η διάρκεια δεν είναι διαθέσιμη.*Αυτόματη/s);
  await reload.click();
  await conflict.waitFor({ state: 'hidden' });
  assert.match(await page.locator('[data-segment-field=mode] option:checked').textContent(), /Historic mode \(ανενεργό\)/);
  assert.equal(await summary.textContent(), 'Αποθηκευμένη διαδρομή · 2 προσαρμοσμένα σημεία διαδρομής');

  await page.evaluate(() => {
    captured['SegmentManager.vue'][0].draft.route.coordinates.splice(1);
  });
  assert.equal(await summary.textContent(), 'Μη αποθηκευμένη διαδρομή · 1 προσαρμοσμένο σημείο διαδρομής');
  await page.evaluate(() => { captured['SegmentManager.vue'][0].draft.route = null; });
  assert.equal(await summary.textContent(), 'Διαθέσιμη εναλλακτική διαδρομή μέσω των σημείων με τη σειρά τους · μη αποθηκευμένη');
  await page.locator('[data-segment-field="toPlaceId"]').selectOption({ index: 0 });
  assert.equal(await summary.textContent(), 'Χωρίς διαδρομή');
  await page.getByText(/Δεν έχει επιλεγεί προορισμός/).waitFor();
});

test('Greek Map Search and Place popup localize feedback without changing authored text', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, { ...state, options: { ...state.options, limits: { nominatimSearchLimit: 5 } } });
  const search = page.locator('.trip-editor-map-search').first();
  await search.getByRole('searchbox').fill('Athens');
  await page.evaluate(() => {
    window.fetch = () => new Promise(resolve => { window.completeSearch = resolve; });
  });
  await search.getByRole('button', { name: 'Αναζήτηση' }).click();
  assert.match(await search.getByRole('status').textContent(), /Αναζήτηση στον χάρτη/);
  await page.evaluate(() => completeSearch(new Response(null, { status: 429 })));
  const status = search.getByRole('alert');
  assert.match(await status.textContent(), /Δοκιμάστε ξανά σε λίγο/);
  await page.evaluate(() => { window.fetch = async () => Response.json({ query: 'Athens', results: [] }); });
  await search.getByRole('button', { name: 'Αναζήτηση' }).click();
  await page.getByText('Δεν βρέθηκαν αποτελέσματα στον χάρτη.', { exact: true }).waitFor();

  const popup = await page.evaluate(() => {
    const graph = captured['App.vue'][0].state;
    const authored = { ...graph.regionsById.region, name: 'Authored <region-name> & name', center: { latitude: 11, longitude: 21 } };
    const shadow = { ...authored, id: 'shadow', name: 'Unassigned Places', isShadow: true, center: null };
    const place = { ...graph.placesById.place, regionId: shadow.id, name: 'Authored <stop> & name', address: 'Author <road>',
      resolvedFeatureName: 'Provider <name>', resolvedFeatureType: 'city', visitSummary: { isVisited: true, visitCount: 2 } };
    const mapRoot = document.createElement('div');
    mapRoot.style.cssText = 'width: 500px; height: 400px';
    document.body.append(mapRoot);
    const map = Probe.createTripEditorMap(mapRoot, '/tiles/{z}/{x}/{y}.png');
    const snapshot = { ...graph, regionsById: { region: authored, shadow }, placesById: { place }, areasById: {}, segmentsById: {} };
    try {
      map.render(snapshot);
      const marker = mapRoot.querySelector('[data-place-marker-icon="place"]');
      const regionMarker = mapRoot.querySelector('[data-region-marker-icon="region"]');
      regionMarker.parentElement.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
      const tooltip = mapRoot.querySelector('.leaflet-tooltip')?.textContent;
      map.selectPlace(snapshot, place.id, { openPopup: true });
      const element = mapRoot.querySelector('.trip-editor-place-popup__content');
      const text = element.textContent;
      const parsedAuthoredTag = Boolean(element.querySelector('stop, road, name'));
      const badge = marker.parentElement.querySelector('.trip-editor-map-marker__badge').title;
      map.render({ ...snapshot, placesById: { place: { ...place, regionId: authored.id } } });
      map.selectPlace(snapshot, place.id, { openPopup: true });
      return { text, parsedAuthoredTag, badge, name: marker.alt, title: marker.parentElement.title,
        regionName: regionMarker.alt, regionTitle: regionMarker.parentElement.title, tooltip,
        authored: [...mapRoot.querySelectorAll('.trip-editor-place-popup__header span')].at(-1).textContent, stored: shadow.name };
    } finally {
      map.dispose();
      mapRoot.remove();
    }
  });
  assert.match(popup.text, /Πλάτος: 10,00000 Μήκος: 20,00000.*Διεύθυνση: Author <road>.*Εντοπισμένη τοποθεσία: Provider <name>/s);
  assert.match(popup.text, /κατά προσέγγιση.*2 επισκέψεις.*Σημειώσεις:.*Επιλέξτε τον δείκτη/s);
  assert.match(popup.text, /Authored <stop> & name/);
  assert.equal(popup.parsedAuthoredTag, false);
  assert.match(popup.text, /Τοποθεσίες χωρίς περιοχή/);
  assert.equal(popup.stored, 'Unassigned Places');
  assert.equal(popup.authored, 'Authored <region-name> & name');
  assert.equal(popup.title, 'Authored <stop> & name');
  assert.equal(popup.name, 'Authored <stop> & name, 2 επισκέψεις');
  assert.equal(popup.badge, '2 επισκέψεις');
  assert.equal(popup.regionTitle, 'Κέντρο περιοχής: Authored <region-name> & name');
  assert.equal(popup.regionName, popup.regionTitle);
  assert.equal(popup.tooltip, 'Authored <region-name> & name');
});

test('Greek Region controls preserve collapse, selection and independent deletion choices', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, state);
  const card = page.locator('[data-region-id="region"]');
  const collapse = card.locator('button[aria-expanded][aria-controls]');
  const description = () => collapse.evaluate(button => document.getElementById(button.getAttribute('aria-describedby')).textContent.trim());
  const search = page.locator('input[type="search"][placeholder^="Αναζήτηση περιοχών"]').first();
  assert.equal(await collapse.textContent().then(text => text.trim()), 'Σύμπτυξη');
  await collapse.click();
  assert.equal(await collapse.textContent().then(text => text.trim()), 'Ανάπτυξη');
  await search.fill('Region');
  await card.getByRole('button', { name: 'Σύμπτυξη' }).waitFor();
  assert.equal(await collapse.isDisabled(), true);
  assert.match(await description(), /αναζήτηση/);
  await search.clear();
  await card.getByRole('button', { name: 'Ανάπτυξη' }).waitFor();
  await collapse.click();
  await card.locator('[data-place-id="place"]').click();
  await page.waitForFunction(() => document.querySelector('[data-region-id="region"] button[aria-describedby]')?.disabled);
  assert.match(await description(), /επιλεγμένη τοποθεσία/);
  await card.getByRole('button', { name: 'Επεξεργασία', exact: true }).first().click();
  assert.match(await description(), /επεξεργασίας/);

  await page.evaluate(() => {
    window.useMountedConfirmation = true;
    window.previewRequests = [];
    window.fetch = async (url, request) => {
      const { places, areas } = JSON.parse(request.body);
      const sample = count => ({ count, ids: count ? ['child'] : [], hasMore: false });
      previewRequests.push({ url, places, areas });
      return Response.json({ contractVersion: 1, tripId: 'trip', sourceRegionId: 'region', sourceRegionName: 'Authored <region-name>',
        places, areas, destination: { mode: places === 'keep' ? 'create' : 'existing', regionId: null, name: 'Unassigned Places' },
        consequences: { movedPlaces: sample(Number(places === 'keep')), deletedPlaces: sample(Number(places === 'delete')),
          movedAreas: sample(1), deletedAreas: sample(0), deletedSegments: sample(0), updatedSegments: sample(0), detachedWaypoints: sample(0) },
        confirmation: { token: 'preview', expiresAt: new Date(Date.now() + 60_000).toISOString() } });
    };
  });
  await card.getByRole('button', { name: 'Διαγραφή', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Διαγραφή περιοχής «Authored <region-name>»;' });
  const places = dialog.getByRole('group', { name: 'Τοποθεσίες (1)' });
  const areas = dialog.getByRole('group', { name: 'Ζώνες (1)' });
  await dialog.getByText('Δημιουργείται η περιοχή «Τοποθεσίες χωρίς περιοχή».').waitFor();
  assert.equal(await places.getByRole('radio', { name: /Διατήρηση \(προτείνεται\).*Μεταφορά/ }).isChecked(), true);
  await places.getByRole('radio', { name: /Οριστική διαγραφή.*Δεν μπορεί να αναιρεθεί/ }).check();
  await dialog.getByText('Μεταφορά στην υπάρχουσα περιοχή «Τοποθεσίες χωρίς περιοχή».').waitFor();
  assert.equal(await areas.getByRole('radio', { name: /Διατήρηση/ }).isChecked(), true);
  assert.equal(await dialog.getByRole('button', { name: 'Διαγραφή περιοχής και τοποθεσιών · διατήρηση ζωνών' }).isEnabled(), true);
  await dialog.getByRole('button', { name: 'Ακύρωση' }).click();
  assert.deepEqual(await page.evaluate(() => previewRequests), [
    { url: '/api/trips/trip/editor/regions/region/deletion-preview', places: 'keep', areas: 'keep' },
    { url: '/api/trips/trip/editor/regions/region/deletion-preview', places: 'delete', areas: 'keep' }
  ]);
  assert.deepEqual(await page.evaluate(() => captured['App.vue'][0].state.regionOrder), ['region']);
});

test('Greek Editor shows the canonical Region and announces map navigation for a kept Area', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const canonical = { ...region, id: 'canonical', name: 'Unassigned Places', isShadow: true, displayOrder: 0 };
  const areaName = 'Kept <area>';
  const placeName = 'Kept <place>';
  const keptArea = { ...area, name: areaName, regionId: canonical.id,
    originalValues: { ...area.originalValues, name: areaName, regionId: canonical.id } };
  const visitSummary = { ...place.visitSummary, isVisited: true, visitCount: 1,
    firstVisitAt: metadata.updatedAt, lastVisitAt: metadata.updatedAt };
  const keptPlace = { ...place, name: placeName, regionId: canonical.id, visitSummary,
    originalValues: { ...place.originalValues, name: placeName, regionId: canonical.id } };
  const page = await mountGreekEditor(browser, { ...state,
    metadata: { ...metadata, center: { latitude: 38, longitude: 23 }, zoom: 8 },
    permissions: { ...state.permissions, canReadVisitProgress: true },
    regionsById: { region, canonical }, regionOrder: [canonical.id, region.id],
    areasById: { area: keptArea }, areaOrderByRegionId: { canonical: [area.id] },
    placesById: { place: keptPlace }, placeOrderByRegionId: { canonical: [place.id] },
    visitProgress: { totalPlaces: 1, visitedPlaces: 1, percentVisited: 100, placeSummariesByPlaceId: { place: visitSummary },
      historyRows: [{ visitId: 'visit', placeId: place.id, regionId: canonical.id, startedAt: metadata.updatedAt,
        endedAt: null, durationMinutes: null }] }
  });
  const label = 'Τοποθεσίες χωρίς περιοχή';
  const card = page.locator('[data-region-id="canonical"]');
  const toolbar = page.locator('.trip-editor-toolbar');
  const status = toolbar.getByRole('status');
  await card.locator('[data-area-id="area"]').getByRole('button', { name: 'Επεξεργασία', exact: true }).click();
  const areaHeader = card.locator('.trip-editor-surface__header');
  await areaHeader.getByRole('heading', { name: `Επεξεργασία ζώνης — ${areaName}` }).waitFor({ timeout: 3000 });
  assert.equal(await areaHeader.locator('small').textContent(), label);
  assert.equal(await status.textContent(), label);

  // Replay the kept-Area toolbar sequence through the mounted buttons and live status.
  const focus = toolbar.getByRole('button', { name: 'Εστίαση στο ενεργό στοιχείο', exact: true });
  const fitAll = toolbar.getByRole('button', { name: 'Προβολή όλων στον χάρτη', exact: true });
  const recenter = toolbar.getByRole('button', { name: 'Επαναφορά αποθηκευμένης προβολής', exact: true });
  for (const [button, text] of [[focus, 'Εστίαση στη ζώνη'], [fitAll, 'Προβάλλονται όλα τα στοιχεία στον χάρτη'],
    [recenter, 'Επαναφέρθηκε η αποθηκευμένη προβολή του ταξιδιού']]) {
    await button.click();
    assert.equal(await status.textContent(), text);
  }
  await page.evaluate(() => { window.mapActionResults = { fitAll: 'no-geometry', recenter: 'missing-view' }; });
  await fitAll.click();
  assert.equal(await status.textContent(), 'Δεν υπάρχουν στοιχεία για προβολή στον χάρτη');
  await recenter.click();
  assert.equal(await status.textContent(), 'Η αποθηκευμένη προβολή του ταξιδιού δεν είναι διαθέσιμη');
  for (const [outcome, text] of [['no-geometry', 'Δεν υπάρχουν δεδομένα χάρτη για εστίαση'],
    ['missing-target', 'Δεν υπάρχει ενεργό στοιχείο για εστίαση'], ['unsupported-target', 'Δεν είναι δυνατή η εστίαση στο ενεργό στοιχείο']]) {
    await page.evaluate(outcome => { window.mapActionResults.focus = outcome; }, outcome);
    await focus.click();
    assert.equal(await status.textContent(), text);
  }

  await card.locator('[data-place-id="place"]').getByRole('button', { name: 'Επεξεργασία', exact: true }).click();
  const regionSelect = card.locator('select').filter({ has: page.locator('option[value="canonical"]') });
  await regionSelect.waitFor({ timeout: 3000 });
  assert.equal(await regionSelect.inputValue(), canonical.id);
  assert.equal(await regionSelect.locator('option:checked').textContent(), label);
  assert.equal(await regionSelect.locator('option[value="region"]').textContent(), region.name);
  assert.equal(await card.locator('.trip-editor-surface__header small').textContent(), label);
  assert.equal(await status.textContent(), `Επιλεγμένη τοποθεσία: ${placeName}`);
  assert.equal(await card.getAttribute('data-region-name'), canonical.name);

  await page.getByRole('button', { name: 'Επισκέψεις', exact: true }).click();
  const group = page.getByRole('region', { name: label });
  await group.getByRole('heading', { name: keptPlace.name }).waitFor();
  assert.equal(await group.locator('[data-visit-id="visit"] .trip-editor-visit-history-row__title span').textContent(), label);
});

test('Greek selection confirms dirty Area navigation and announces authored Segment transport', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const transportLabel = 'Local <Shuttle> {0}';
  const page = await mountGreekEditor(browser, { ...state, options: { ...state.options,
    transportModes: [{ value: segment.mode, label: transportLabel }] } });
  await page.evaluate(() => { window.useMountedConfirmation = true; });
  await page.locator('[data-area-id="area"]').getByRole('button', { name: 'Επεξεργασία', exact: true }).click();
  const areaName = page.locator('.trip-editor-surface').getByRole('textbox', { name: 'Όνομα', exact: true });
  await areaName.fill('Unsaved Area');

  const placeRow = page.locator('[data-place-id="place"]');
  await placeRow.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Απόρριψη αλλαγών;' });
  await dialog.getByText('Να απορριφθούν οι μη αποθηκευμένες αλλαγές της ζώνης πριν επιλέξετε τοποθεσία;', { exact: true }).waitFor();
  await dialog.getByRole('button', { name: 'Συνέχεια επεξεργασίας' }).click();
  await page.waitForFunction(() => document.activeElement?.matches('[data-place-id="place"]'));
  assert.equal(await areaName.inputValue(), 'Unsaved Area');
  assert.doesNotMatch(await placeRow.getAttribute('class'), /trip-editor-place-row--active/);

  // The stub hands the persisted key to App through the same callback as a map click.
  await page.evaluate(() => { void selectSegmentFromMap({ kind: 'persisted', id: 'segment' }); });
  await dialog.getByText('Να απορριφθούν οι μη αποθηκευμένες αλλαγές πριν επιλέξετε άλλο τμήμα διαδρομής;', { exact: true }).waitFor();
  await dialog.getByRole('button', { name: 'Απόρριψη', exact: true }).click();
  await areaName.waitFor({ state: 'detached' });
  const status = page.locator('.trip-editor-toolbar').getByRole('status');
  assert.equal(await status.textContent(), `Επιλεγμένο τμήμα διαδρομής: ${transportLabel}`);

  await page.getByRole('button', { name: 'Προσθήκη τμήματος', exact: true }).click();
  await status.getByText('Επιλέχθηκε το νέο πρόχειρο τμήμα διαδρομής', { exact: true }).waitFor();
});

test('Greek tab navigation keeps the dirty Area and restores focus on Escape', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, state);
  await page.setViewportSize({ width: 390, height: 900 });
  await page.evaluate(() => { window.useMountedConfirmation = true; });
  const tabs = page.locator('.trip-editor-mobile-drawer__tabs');
  const regionsTab = tabs.getByRole('button', { name: 'Περιοχές', exact: true });
  const segmentsTab = tabs.getByRole('button', { name: 'Τμήματα διαδρομής', exact: true });
  await regionsTab.click();
  await page.locator('[data-area-id="area"]').getByRole('button', { name: 'Επεξεργασία', exact: true }).click();
  const areaName = page.locator('.trip-editor-surface').getByRole('textbox', { name: 'Όνομα', exact: true });
  await areaName.fill('Unsaved Area');
  await segmentsTab.click();
  const dialog = page.getByRole('dialog', { name: 'Απόρριψη αλλαγών;' });
  await dialog.getByText('Να απορριφθούν οι μη αποθηκευμένες αλλαγές της ζώνης πριν από την αλλαγή καρτέλας;', { exact: true }).waitFor();
  await dialog.getByRole('button', { name: 'Συνέχεια επεξεργασίας' }).press('Escape');
  await page.waitForFunction(() => document.activeElement?.matches('.trip-editor-mobile-drawer__tabs button:last-child'));
  assert.equal(await regionsTab.getAttribute('aria-pressed'), 'true');
  assert.equal(await areaName.inputValue(), 'Unsaved Area');

  await segmentsTab.click();
  await dialog.getByRole('button', { name: 'Απόρριψη', exact: true }).click();
  await areaName.waitFor({ state: 'detached' });
  assert.equal(await segmentsTab.getAttribute('aria-pressed'), 'true');
});

test('Greek map-work confirmation keeps or rolls back its temporary snapshot', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, state);
  await page.evaluate(() => {
    window.useMountedConfirmation = true;
    window.pendingPoint = [10, 20];
    captured['App.vue'][0].editorSurface.enterMapWork({
      modeName: 'Temporary map work', instruction: 'fixture geometry',
      snapshot: () => [11, 21], rollback: point => { window.pendingPoint = point; }, done: () => {}
    });
  });
  const work = page.locator('.trip-editor-map-work-toolbar');
  const cancel = work.getByRole('button', { name: 'Ακύρωση' });
  await cancel.click();
  const dialog = page.getByRole('dialog', { name: 'Απόρριψη αλλαγών στον χάρτη;' });
  await dialog.waitFor({ timeout: 3000 });
  assert.match(await dialog.textContent(), /Οι προσωρινές αλλαγές στον χάρτη θα απορριφθούν/);
  await dialog.getByRole('button', { name: 'Συνέχεια επεξεργασίας' }).click();
  assert.ok(await work.isVisible());
  assert.deepEqual(await page.evaluate(() => pendingPoint), [10, 20]);

  await cancel.click();
  await dialog.getByRole('button', { name: 'Απόρριψη', exact: true }).click();
  await work.waitFor({ state: 'hidden' });
  assert.deepEqual(await page.evaluate(() => pendingPoint), [11, 21]);
});

test('Greek credit validation opens the controls and associates the server error before retry', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, state);
  const credits = page.locator('details').filter({ hasText: 'Πηγές και αναφορές' }).filter({
    has: page.getByRole('button', { name: 'Προσθήκη αναφοράς', includeHidden: true })
  });
  const summary = credits.locator('summary').first();
  await summary.click();
  await credits.getByRole('button', { name: 'Προσθήκη αναφοράς' }).click();
  await summary.click();
  await page.evaluate(() => {
    let current = captured['App.vue'][0].state.metadata;
    window.fetch = Probe.tripReceiptReplies(() => current, (_url, request, permit) => {
      const credits = JSON.parse(request.body).changes.credits;
      if (!credits[0].contributor) return Response.json(Probe.tripReceipt(permit, 'terminalUnapplied', {
        code: 'mutation-request-invalid', errors: { credits: ['Check the contribution policy and complete readable credits.'] }
      }), { status: 400 });
      window.retriedCredits = credits;
      current = { ...current, credits, originalValues: { ...current.originalValues, credits } };
      return Response.json(Probe.tripReceipt(permit, 'applied', { fieldDigests: { credits: 'sha256:' + 'a'.repeat(64) } }));
    });
  });
  const form = page.locator('#trip-editor-metadata-form');
  await form.evaluate(node => node.requestSubmit());
  await form.getByRole('alert').filter({ hasText: /Ελέγξτε τα επισημασμένα πεδία/ }).waitFor();
  assert.ok(await credits.evaluate(node => node.open));
  const group = credits.locator('[aria-invalid=true]');
  const errorId = await group.getAttribute('aria-describedby');
  assert.ok(errorId);
  assert.match(await credits.locator(`[id="${errorId}"]`).textContent(), /Ελέγξτε την πολιτική συνεισφοράς και συμπληρώστε ευανάγνωστες αναφορές/);

  await credits.getByRole('textbox', { name: 'Συντελεστής', exact: true }).fill('Author');
  await form.evaluate(node => node.requestSubmit());
  await page.waitForFunction(() => window.retriedCredits?.[0]?.contributor === 'Author');
  await page.waitForFunction(() => captured['MetadataEditor.vue'][0].isMetadataDirty === false, null, { timeout: 3000 });
  await group.waitFor({ state: 'detached' });
});

test('Greek Planning keeps uncertain drafts held and renders actionable Check findings', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const authored = 'Authored <route> & clock';
  const planning = { format: 1, entries: [{ id: 'one', label: authored, kind: 'presence',
    when: { form: 'at', value: { precision: 'dateTime', local: '2026-10-25T02:30', zone: 'Europe/Paris' } } }] };
  const page = await mountGreekEditor(browser, { ...state, ownerId: 'account', metadata: { ...metadata, planning } });
  await page.evaluate(async () => {
    const parent = captured['App.vue'][0];
    parent.editorSurface.clearActiveTarget();
    const review = captured['PlanCheck.vue'][0].review;
    const item = { key: 'trip/trip/one', name: parent.state.metadata.name, entry: parent.state.metadata.planning.entries[0],
      selected: true, parentWindows: [] };
    const other = { ...item, key: 'place/place/two', name: 'Authored <place> & clock' };
    window.fetch = async (_url, init) => Response.json({ contract: 'planning-check-1', ownerId: 'account', tripId: 'trip',
      inventory: [item, other], setup: JSON.parse(init.body), estimateSources: [], movementEvidence: [], reused: [],
      invalidated: [item.entry.label + ': changed clock/zone reopened occurrence choice.'],
      findings: [{ id: 'physical/0', check: 'physical', entries: [item.key, other.key],
        result: { status: 'unknown', message: 'Occupied overlap needs an explicit single-traveller premise and assessed incompatible locations.' } },
      { id: 'movement/1', check: 'movement', entries: [item.key, other.key],
        result: { status: 'unknown', message: 'Intended arrival: Travel gap depends on an unresolved fold; choose the occurrence.' },
        movement: { comparison: 'Travel gap depends on an unresolved fold; choose the occurrence.', relevant: false, allowance: 'unknown',
          connection: 'unknown', estimate: { routeUnavailable: false, manualEstimate: 12.5 } } }] });
    await review.run();
  });
  await page.getByText(/12,5 λεπ\./).waitFor();
  const check = page.locator('.plan-check');
  await check.locator('.plan-check__inputs > summary').click();
  await check.locator('.plan-check__entry summary').first().click();
  assert.ok(await check.locator('.plan-check__entry input[step="1"]').first().isVisible());
  assert.ok(await check.getByRole('button', { name: 'Αυτές οι τοποθεσίες απαιτούν χωριστή παρουσία' }).isEnabled());
  assert.match(await check.textContent(), /Authored <route> & clock/);
  assert.equal(await check.locator('route').count(), 0);

  await page.evaluate(async () => {
    const owner = captured['MetadataEditor.vue'][0];
    await owner.openMetadata();
    await Probe.nextTick();
    const editor = captured['PlanningEditor.vue'].findLast(item => item.draft?.kind === 'trip');
    editor.draft.entries[0].label += ' kept';
    window.fetch = async () => { throw new TypeError('diagnostic-only transport failure'); };
    await editor.workspace.save(editor.draft);
  });
  const planningPanel = page.locator('[data-planning]');
  await planningPanel.locator('summary').first().click();
  await planningPanel.getByRole('alert').filter({ hasText: /Η αποθήκευση του προγραμματισμού δεν επιβεβαιώθηκε/ }).waitFor();
  assert.ok(await planningPanel.locator('.btn-primary').isDisabled());
  assert.match(await planningPanel.textContent(), /προηγούμενη αλλαγή μπορεί ακόμη να ολοκληρωθεί/);
  assert.equal(await page.evaluate(() => captured['PlanningEditor.vue'].findLast(item => item.draft?.kind === 'trip').draft.entries[0].label),
    `${authored} kept`);
});

test('Greek expanded Close and entity Cancel keep focus on Escape and discard only their draft', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const planning = { format: 1, entries: [{ id: 'one', kind: 'presence', label: 'Saved plan',
    when: { form: 'at', value: { precision: 'date', local: '2026-11-21' } } }] };
  const page = await mountGreekEditor(browser, { ...state, metadata: { ...metadata, planning } });
  await page.evaluate(() => {
    window.useMountedConfirmation = true;
    const planner = captured['PlanningEditor.vue'].find(item => item.draft?.kind === 'trip');
    planner.draft.entries[0].label = 'Separate planning draft';
  });
  const editArea = page.locator('[data-area-id="area"]').getByRole('button', { name: 'Επεξεργασία', exact: true });
  const addPlace = page.getByRole('button', { name: 'Προσθήκη τοποθεσίας', exact: true }).first();
  const cases = [
    { open: editArea, original: area.name, expand: true, trigger: 'Κλείσιμο',
      prompt: 'Να απορριφθούν οι μη αποθηκευμένες αλλαγές και να κλείσει ο επεξεργαστής;' },
    { open: editArea, original: area.name, trigger: 'Ακύρωση',
      prompt: 'Να απορριφθούν οι μη αποθηκευμένες αλλαγές της ζώνης;' },
    { open: addPlace, original: 'Νέα τοποθεσία', trigger: 'Ακύρωση',
      prompt: 'Να απορριφθούν οι μη αποθηκευμένες αλλαγές της τοποθεσίας;' }
  ];
  for (const { open, original, expand, trigger, prompt } of cases) {
    await open.click();
    if (expand) await page.getByRole('button', { name: 'Ανάπτυξη επεξεργαστή' }).click();
    const surface = page.locator(expand ? '.trip-editor-expanded__dialog' : '.trip-editor-surface--docked');
    const draft = surface.getByRole('textbox', { name: 'Όνομα', exact: true });
    await draft.fill('Only this draft');
    const close = surface.getByRole('button', { name: trigger, exact: true });
    await close.click();
    const dialog = page.getByRole('dialog', { name: 'Απόρριψη αλλαγών;' });
    await dialog.getByText(prompt, { exact: true }).waitFor({ timeout: 3000 });
    await dialog.getByRole('button', { name: 'Συνέχεια επεξεργασίας' }).press('Escape');
    await close.evaluate(node => {
      if (node !== document.activeElement) throw new Error('Discard cancellation did not restore the initiating control');
    });
    assert.equal(await draft.inputValue(), 'Only this draft');
    await close.click();
    await dialog.getByRole('button', { name: 'Απόρριψη', exact: true }).click();
    await surface.waitFor({ state: 'detached', timeout: 3000 });
    await open.click();
    const reopened = page.locator('.trip-editor-surface--docked').getByRole('textbox', { name: 'Όνομα', exact: true });
    assert.equal(await reopened.inputValue(), original);
    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Κλείσιμο', exact: true }).click();
  }
  await page.getByRole('button', { name: 'Επεξεργασία ταξιδιού', exact: true }).click();
  assert.equal(await page.evaluate(() => captured['PlanningEditor.vue'].findLast(item => item.draft?.kind === 'trip')
    .draft.entries[0].label), 'Separate planning draft');
});

test('Greek and English coordinate picking keeps the marker and status on the pick while renaming', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const cases = [
    { locale: 'en-US', add: 'Add Place', pick: 'Pick on map', name: 'Name', latitude: 'Latitude', longitude: 'Longitude',
      status: 'Selected', done: 'Done', cancel: 'Cancel', discard: 'Discard' },
    { locale: 'el-GR', add: 'Προσθήκη τοποθεσίας', pick: 'Επιλογή στον χάρτη', name: 'Όνομα', latitude: 'Γεωγραφικό πλάτος',
      longitude: 'Γεωγραφικό μήκος', status: 'Επιλεγμένες συντεταγμένες:', done: 'Τέλος', cancel: 'Ακύρωση', discard: 'Απόρριψη' }
  ];
  for (const labels of cases) {
    await t.test(labels.locale, async () => {
      const page = await mountEditor(browser, state, labels.locale, { realMap: true });
      try {
        await checkPlaceCoordinateWork(page, labels);
      } finally {
        await page.close();
      }
    });
  }
});

/** Clicks the real Leaflet map and checks the draft, marker and rendered toolbar through Done and Cancel. */
async function checkPlaceCoordinateWork(page, labels) {
  await page.getByRole('button', { name: labels.add, exact: true }).click();
  const draft = page.locator('.trip-editor-surface--docked');
  const name = draft.getByRole('textbox', { name: labels.name, exact: true });
  const latitude = draft.getByRole('spinbutton', { name: labels.latitude, exact: true });
  const longitude = draft.getByRole('spinbutton', { name: labels.longitude, exact: true });
  const pick = draft.getByRole('button', { name: labels.pick });
  const work = page.locator('.trip-editor-map-work-toolbar');
  const map = page.locator('.trip-editor-map.leaflet-container');
  const marker = map.locator('[data-place-draft-latitude]');
  const markerCoordinate = () => marker.evaluate(node => [node.dataset.placeDraftLatitude, node.dataset.placeDraftLongitude]);
  const draftCoordinate = async () => [await latitude.inputValue(), await longitude.inputValue()];
  const original = await draftCoordinate();
  await name.fill('Earlier name');
  await pick.click();
  await map.click({ position: { x: 390, y: 125 } });
  const chosen = await markerCoordinate();
  assert.notDeepEqual(chosen, original);
  const format = new Intl.NumberFormat(labels.locale, { maximumFractionDigits: 6, useGrouping: false });
  const selectedStatus = `${labels.status} ${chosen.map(value => format.format(Number(value))).join(', ')}`;
  await work.getByText(selectedStatus, { exact: true }).waitFor();
  await name.fill('New Place');
  assert.deepEqual(await markerCoordinate(), chosen);
  assert.equal(await work.locator('small').textContent(), selectedStatus);
  assert.deepEqual(await draftCoordinate(), original);

  await work.getByRole('button', { name: labels.done, exact: true }).click();
  await work.waitFor({ state: 'detached' });
  assert.deepEqual(await draftCoordinate(), chosen);
  await pick.click();
  await map.click({ position: { x: 175, y: 225 } });
  assert.notDeepEqual(await markerCoordinate(), chosen);
  await work.getByRole('button', { name: labels.cancel, exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: labels.discard, exact: true }).click();
  await work.waitFor({ state: 'detached' });
  assert.deepEqual(await draftCoordinate(), chosen);
  assert.deepEqual(await markerCoordinate(), chosen);
  assert.equal(await name.inputValue(), 'New Place');
}

test('Greek creation and Reset localize only app-generated Region and Place names', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const savedRegion = { ...region, name: 'New Region', originalValues: { ...region.originalValues, name: 'New Region' } };
  const savedPlace = { ...place, name: 'New Place', originalValues: { ...place.originalValues, name: 'New Place' } };
  const page = await mountGreekEditor(browser, { ...state, regionsById: { region: savedRegion }, placesById: { place: savedPlace } });
  const form = page.locator('.trip-editor-surface--docked');
  const name = form.getByRole('textbox', { name: 'Όνομα', exact: true });
  const reset = form.getByRole('button', { name: 'Επαναφορά', exact: true });
  const cases = [
    { action: 'openCreate', args: [], expected: 'Νέα περιοχή' },
    { action: 'openEdit', args: [savedRegion], expected: 'New Region' },
    { action: 'openPlaceCreate', args: [savedRegion], expected: 'Νέα τοποθεσία' },
    { action: 'openPlaceEdit', args: [savedPlace], expected: 'New Place' }
  ];
  const result = { id: 'provider-place', provider: 'fixture', address: '', latitude: 11, longitude: 21 };
  for (const [name, displayName, expected] of [
    ['Provider name', '', 'Provider name'], ['New Place', '', 'New Place'],
    ['', 'Provider display name, Province', 'Provider display name'], ['', '', 'Νέα τοποθεσία']
  ]) {
    cases.push({ action: 'openPlaceCreateFromSearch', args: [savedRegion, { ...result, name, displayName }], expected });
  }
  for (const { action, args, expected } of cases) {
    await page.evaluate(({ action, args }) => captured['RegionManager.vue'][0][action](...args), { action, args });
    assert.equal(await name.inputValue(), expected);
    await name.fill('User-entered name');
    assert.equal(await name.inputValue(), 'User-entered name');
    await reset.click();
    assert.equal(await name.inputValue(), expected);
    await form.getByRole('button', { name: 'Κλείσιμο', exact: true }).click();
    await form.waitFor({ state: 'detached' });
  }
});

test('Greek metadata and sharing recovery retain the earlier attempt and localize the exit prompt', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const held = 'Μια προηγούμενη αποθήκευση ταξιδιού δεν έχει επιβεβαιωθεί.';
  for (const field of ['name', 'shareProgressEnabled']) {
    const publication = { isPublic: true, contributionPolicy: 'CC-BY-4.0' };
    const page = await mountGreekEditor(browser, { ...state, metadata: { ...metadata, ...publication,
      originalValues: { ...metadata.originalValues, ...publication } } });
    await page.evaluate(() => {
      window.useMountedConfirmation = true;
      window.writes = [];
      const current = captured['App.vue'][0].state.metadata;
      window.fetch = Probe.tripReceiptReplies(() => current, (_url, _init, permit) => {
        window.writes.push(permit.kind);
        throw new TypeError('diagnostic-only lost reply');
      }, { lookup: permit => Response.json({ ...permit, state: 'unobserved', historicalOutcome: 'unknown',
        futureEffects: true, observedAtUtc: '2026-11-28T09:56:00Z' }) });
    });
    const form = page.locator('#trip-editor-metadata-form');
    const draft = field === 'name' ? form.getByRole('textbox', { name: 'Όνομα', exact: true })
      : form.getByRole('checkbox', { name: 'Εμφάνιση προόδου επισκέψεων στο δημόσιο ταξίδι' });
    if (field === 'name') await draft.fill('Retained title');
    else await draft.check();
    const save = page.getByRole('button', { name: 'Αποθήκευση ρυθμίσεων και συνέχεια' });
    await save.click();
    const warning = page.getByRole('status').filter({ hasText: held });
    await warning.waitFor({ timeout: 3000 });
    await warning.getByRole('button', { name: 'Ανάκτηση αποθήκευσης ταξιδιού' }).click();
    await save.click();
    await form.getByRole('alert').filter({ hasText: 'Η αλλαγή δεν επιβεβαιώθηκε.' }).waitFor({ timeout: 3000 });
    await warning.waitFor({ timeout: 3000 });
    assert.deepEqual(await page.evaluate(() => window.writes), [field === 'name' ? 'metadata' : 'editor-sharing']);

    const leave = page.getByRole('button', { name: 'Έξοδος χωρίς αποθήκευση', exact: true });
    await leave.click();
    const dialog = page.getByRole('dialog', { name: 'Έξοδος χωρίς αποθήκευση;' });
    await dialog.getByText(held, { exact: false }).waitFor({ timeout: 3000 });
    await dialog.getByRole('button', { name: 'Συνέχεια επεξεργασίας' }).press('Escape');
    await warning.waitFor({ timeout: 3000 });
    if (field === 'name') assert.equal(await draft.inputValue(), 'Retained title');
    else assert.ok(await draft.isChecked());
    await page.close();
  }
});

test('Greek Region save status and first publication use the current locale', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await mountGreekEditor(browser, state);
  await page.evaluate(() => {
    window.useMountedConfirmation = true;
    window.publications = [];
    let current = captured['App.vue'][0].state.metadata;
    window.fetch = Probe.tripReceiptReplies(() => current, (_url, request, permit) => {
      const { expected, changes } = JSON.parse(request.body);
      window.publications.push({ expected, changes });
      current = { ...current, ...changes, originalValues: { ...current.originalValues, ...changes } };
      const digest = 'sha256:' + 'a'.repeat(64);
      return Response.json(Probe.tripReceipt(permit, 'applied', { fieldDigests: { isPublic: digest, contributionPolicy: digest } }));
    }, { next: async () => Response.json({ errors: { name: ['Name is required.'] } }, { status: 400 }) });
  });
  const save = page.getByRole('button', { name: 'Αποθήκευση ρυθμίσεων και συνέχεια' });
  await page.getByRole('checkbox', { name: 'Δημόσιο ταξίδι', exact: true }).check();
  const policy = page.getByRole('combobox', { name: 'Πολιτική δημοσίευσης' });
  await policy.selectOption('view-only');
  await save.click();
  const publication = page.getByRole('dialog', { name: 'Να δημοσιευτεί το ταξίδι;' });
  await publication.getByText(/Συνεισφορές ιδιοκτήτη: Δημόσια προβολή μόνο/).waitFor({ timeout: 3000 });
  await publication.getByRole('button', { name: 'Συνέχεια επεξεργασίας' }).press('Escape');
  assert.equal(await policy.inputValue(), 'view-only');
  assert.deepEqual(await page.evaluate(() => window.publications), []);
  await save.click();
  await publication.getByRole('button', { name: 'Δημοσίευση', exact: true }).click();
  await page.waitForFunction(() => captured['App.vue'][0].state.metadata.isPublic);
  assert.deepEqual(await page.evaluate(() => window.publications), [{
    expected: { isPublic: false, contributionPolicy: null }, changes: { isPublic: true, contributionPolicy: 'view-only' }
  }]);

  // Region Manager's status is a separate live region from the inline form header.
  await page.evaluate(() => captured['MetadataEditor.vue'][0].resetDraft());
  await page.locator('[data-region-id="region"] .trip-editor-region-card__header').getByRole('button', { name: 'Επεξεργασία', exact: true }).click();
  const name = page.locator('.trip-editor-region-form input[type="text"]');
  await name.fill('Retained region');
  await page.getByRole('button', { name: 'Αποθήκευση περιοχής' }).click();
  await page.locator('.trip-editor-regions > .trip-editor-panel__line').getByRole('status')
    .getByText('Η αποθήκευση απέτυχε', { exact: true }).waitFor({ timeout: 3000 });
  assert.equal(await name.inputValue(), 'Retained region');
});

test('Greek Place deletion preview renders its consequences and preserves the name as text', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const placeName = 'Coast <mark> & pier';
  const page = await mountGreekEditor(browser, { ...state, placesById: { place: {
    ...place, name: placeName, originalValues: { ...place.originalValues, name: placeName }
  } } });
  await page.evaluate(placeName => {
    window.useMountedConfirmation = true;
    window.deleteRequests = [];
    window.fetch = async url => {
      deleteRequests.push(url);
      const sample = ids => ({ count: ids.length, ids, hasMore: false });
      return Response.json({ contractVersion: 1, tripId: 'trip', placeId: 'place', regionId: 'region', placeName,
        deletedSegments: sample(['endpoint']), updatedSegments: sample(['survivor1', 'survivor2']),
        detachedWaypoints: sample(['survivor1', 'survivor2'].map(segmentId => ({ segmentId, placeId: 'place' }))),
        unlinkedVisits: 0, deletedCandidates: 0, placeOrderIds: [], segmentOrderIds: ['survivor1', 'survivor2'],
        confirmation: { token: 'preview', expiresAt: new Date(Date.now() + 60000).toISOString() } });
    };
  }, placeName);
  await page.locator('[data-place-id="place"]').getByRole('button', { name: 'Επεξεργασία', exact: true }).click();
  const editor = page.locator('.trip-editor-surface--docked');
  const name = editor.getByRole('textbox', { name: 'Όνομα', exact: true });
  await name.fill('Retained Place');
  const deleteButton = editor.getByRole('button', { name: 'Διαγραφή', exact: true });
  await deleteButton.click();
  const dialog = page.getByRole('dialog', { name: 'Να διαγραφεί η τοποθεσία;' });
  await dialog.getByText(/Θα διαγραφεί 1 τμήμα διαδρομής/).waitFor({ timeout: 3000 });
  const consequences = await dialog.textContent();
  assert.ok(consequences.includes(placeName));
  assert.match(consequences, /Θα ενημερωθούν 2 διαδρομές/);
  assert.match(consequences, /Θα διατηρηθούν 0 καταγραφές επισκέψεων/);
  assert.equal(await dialog.locator('mark').count(), 0);
  await dialog.getByRole('button', { name: 'Διατήρηση τοποθεσίας και προσχεδίων' }).press('Escape');
  assert.equal(await name.inputValue(), 'Retained Place');
  assert.deepEqual(await page.evaluate(() => window.deleteRequests), ['/api/trips/trip/editor/places/place/deletion-preview']);
});

test('wheel zoom updates the Trip view draft after a resize during a pan', { timeout: 30_000 }, async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const saved = { center: { latitude: 37.98, longitude: 23.73 }, zoom: 8 };
  const page = await mountEditor(browser, {
    ...state, metadata: { ...metadata, ...saved, originalValues: { ...metadata.originalValues, ...saved } }
  }, 'en-US', { realMap: true });
  await page.emulateMedia({ reducedMotion: 'no-preference' });

  const map = page.locator('.trip-editor-map.leaflet-container');
  const zoomField = page.getByRole('spinbutton', { name: 'Zoom', exact: true });
  assert.equal(await zoomField.inputValue(), String(saved.zoom));
  await map.scrollIntoViewIfNeeded();
  await map.focus();
  const box = await map.boundingBox();
  await page.mouse.move(box.x + 180, box.y + 100);
  await page.keyboard.press('ArrowRight');

  await map.evaluate(async element => {
    // Wait for the actual layout change before sending wheel input during the pan.
    const originalHeight = element.clientHeight;
    await new Promise(resolve => {
      const observer = new ResizeObserver(() => {
        if (element.clientHeight === originalHeight) return;
        observer.disconnect();
        resolve();
      });
      observer.observe(element);
      element.style.height = `${originalHeight - 81}px`;
    });
    element.addEventListener('wheel', event => {
      element.dataset.wheelDuringPan = String(
        event.isTrusted && element.querySelector('.leaflet-map-pane').classList.contains('leaflet-pan-anim'));
    }, { capture: true, once: true });
  });
  await page.mouse.wheel(0, -80);
  assert.equal(await map.getAttribute('data-wheel-during-pan'), 'true');

  await page.waitForFunction(initialZoom => {
    const element = document.querySelector('.trip-editor-map.leaflet-container');
    return Number(element.dataset.tripEditorMapZoom) > initialZoom
      && !element.querySelector('.leaflet-map-pane').classList.contains('leaflet-zoom-anim');
  }, saved.zoom);
  await map.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await zoomField.inputValue(), await map.getAttribute('data-trip-editor-map-zoom'),
    'the Trip view draft matches the actual wheel zoom');
});

/** Loads the shipped RESX values through the document bootstrap used by the mounted Vue owners. */
async function mountGreekEditor(browser, graph) {
  return mountEditor(browser, graph, 'el-GR');
}

/** Mounts the actual Editor in the requested locale, with Leaflet enabled only for map integration cases. */
async function mountEditor(browser, graph, locale, { realMap = false } = {}) {
  const page = await browser.newPage();
  await page.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<div id="trip-editor-app" data-account-id="account" data-trip-id="trip"><div id="app"></div></div>' }));
  await page.goto('http://localhost/');
  const catalogs = suffix => Promise.all(['Editor', 'Shell'].map(name => readFile(`Resources/${name}${suffix}.resx`, 'utf8')));
  const english = await catalogs('');
  const localized = locale === 'el-GR' ? await catalogs('.el-GR') : english;
  if (realMap) {
    await page.addStyleTag({ path: 'node_modules/leaflet/dist/leaflet.css' });
    await page.addStyleTag({ content: '.trip-editor-map { width: 640px; height: 360px; }' });
  }
  await page.evaluate(({ localized, english, locale, realMap }) => {
    const catalog = sources => Object.fromEntries(sources.flatMap(source => Array.from(new DOMParser().parseFromString(source, 'application/xml')
      .querySelectorAll('data'), item => [item.getAttribute('name'), item.querySelector('value').textContent])));
    window.useRealEditorMap = realMap;
    window.useMountedConfirmation = realMap;
    const element = document.createElement('script');
    element.type = 'application/json';
    element.id = 'wayfarer-locale';
    element.textContent = JSON.stringify({ locale, fallback: 'en-US', messages: catalog(localized), english: catalog(english) });
    document.body.append(element);
  }, { localized, english, locale, realMap });
  await page.addScriptTag({ content: script });
  await page.evaluate(graph => Probe.mountApp(graph), graph);
  await page.waitForFunction(() => window.captured?.['RegionManager.vue']);
  await page.locator('#trip-editor-metadata-form:not([inert])').waitFor();
  return page;
}
