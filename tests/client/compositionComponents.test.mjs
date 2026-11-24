import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { build } from 'esbuild';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { createSSRApp, createRenderer, nextTick } from 'vue';
import { renderToString } from '@vue/server-renderer';
import { enableWorkspaceLanguage, hasLanguageDraft, isLanguageNavigationHeld } from '../../wwwroot/js/localization/languageNavigation.js';

// Compile the actual hierarchy/choice components at the existing in-memory Vue seam.
const bundle = await build({ stdin: { contents: `export { default as FieldChoices } from './FieldChoices.vue'; export { default as CopyToTrip } from './CopyToTrip.vue'; export { default as CompositionApp } from './CompositionApp.vue'; export { settleConfirmDialog } from '../composables/useConfirmDialog'; export { default as SourceTree } from './SourceTree.vue'; export { default as ChoiceControls } from './ChoiceControls.vue'; export { default as CompositionStart } from './CompositionStart.vue';`,
  loader: 'ts', resolveDir: 'ClientApps/trip-editor/src/composition' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'vue', setup(builder) {
    builder.onLoad({ filter: /\.css$/ }, () => ({ contents: '', loader: 'js' }));
    // Keep the actual RichNotesEditor adoption owner; only its DOM editing engine is substituted.
    builder.onLoad({ filter: /rich-notes\/editor\.ts$/ }, () => ({ loader: 'js', contents: `
      export const isSafeNotesLink = () => true;
      export const createNotesEditor = (_host, initial) => {
        const handle = { visible: initial, editor: { isActive: () => false, getAttributes: () => ({}), on() {} },
          onChange(fn) { this.change = fn; }, getValue() { return this.visible; }, saveSelection() {},
          adopt(value) { this.visible = value; }, dispose() {}, focus() {} };
        globalThis.compositionNotes = handle; return handle;
      };` }));
    builder.onLoad({ filter: /displayHelpers\.ts$/ }, () => ({ contents: 'export const placeNotesPreviewHtml = html => html;', loader: 'js' }));
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      const name = args.path.split('/').at(-1).replace('.vue', '');
      if (['CompositionMap', 'ConfirmDialog', 'IconSelector'].includes(name))
        return { contents: `export default { name: '${name}', render() { return null; } };`, loader: 'js' };
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      if (args.path.endsWith('CompositionStart.vue')) {
        const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
        return { loader: 'ts', contents: `${script.content}
          const setup = __component.setup;
          __component.setup = (props, context) => { const bindings = setup(props, context); globalThis.captureConstruction(bindings); return bindings; };
          __component.render = () => null; export default __component;` };
      }
      const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: descriptor.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: script.bindings } });
      return { loader: 'ts', contents: `${script.content}\n${template.code}\n__component.render = render;
        const setup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = setup(props, context); globalThis.captureComposition?.('${name}', bindings); return bindings;
        }; export default __component;` };
    });
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { FieldChoices, SourceTree, ChoiceControls, CompositionStart, CompositionApp, CopyToTrip, settleConfirmDialog } = await import(`data:text/javascript;base64,${Buffer.from(code + '\n//# sourceURL=composition-components.mjs').toString('base64')}`);

/** Supplies the same inert locale snapshot as the page before importing the component bundle. */
const localeElement = (locale, messages = {}) => ({
  textContent: JSON.stringify({ locale, fallback: 'en-US', messages, english: {} })
});
const source = { graph: { id: 'source', name: 'Source', regions: [
  { id: 'region', name: 'Segments', places: [{ id: 'tower', name: 'Taipei 101', location: [121, 25] }], areas: [] }
], segments: [{ id: 'route', mode: 'walk', fromPlaceId: 'tower', toPlaceId: null, waypoints: [] }], credits: [] },
  regionOrder: ['region'], placeOrder: { region: ['tower'] }, areaOrder: { region: [] } };

test('rendered foreign tree preserves Regions/Trip-level Segments and has no temporal labels, values or diagnostics', async () => {
  const html = await renderToString(createSSRApp(SourceTree, { snapshot: source, side: 'b', selection: null, mappings: [], expanded: [] }));
  assert.match(html, /Region · Segments/); assert.match(html, /Trip-level Segments/); assert.match(html, /Place · Taipei 101/);
  assert.doesNotMatch(html, /planning|planned|date|zone|diagnostic|Matched/i);
  assert.match(html, /Not reviewed/);
});

test('Segment copy shows complete anchor choices and Trip-level destination with no Add to Region control', async () => {
  const { newChoice, newReview } = await import('../../ClientApps/trip-editor/src/composition/compositionModel.ts');
  const current = structuredClone(source); current.graph.id = 'C';
  const workspace = { tripId: 'C', sourceA: source, sourceB: source, current, mappings: [] };
  const choice = newChoice(workspace, 'b', 'segment', 'route');
  const html = await renderToString(createSSRApp(ChoiceControls, { workspace, review: newReview(workspace, 'b'), choice }));
  assert.match(html, /C · Trip-level Segments/); assert.match(html, /From · Taipei 101/);
  assert.match(html, /Approve a separate Place with the captured location/); assert.match(html, /Missing Place destination Region/);
  assert.doesNotMatch(html, /Add to Region|>Destination Region|No planned date/);
});

// Render the shared field owner used by both full composition and lightweight Copy, with independently remapped anchors.
const renderRoute = async (sourceFacts, reviewedFacts, currentFacts = reviewedFacts, existingTrip = false, component = FieldChoices) => {
  const { newChoice, newReview } = await import('../../ClientApps/trip-editor/src/composition/compositionModel.ts');
  const sourceB = structuredClone(source); Object.assign(sourceB.graph.segments[0], sourceFacts);
  const current = structuredClone(source); current.graph.id = 'C';
  Object.assign(current.graph.segments[0], reviewedFacts, { fromPlaceId: 'copied-tower' });
  const workspace = { tripId: 'C', sourceA: source, sourceB, current, mappings: [] };
  const review = newReview(workspace, 'b');
  Object.assign(current.graph.segments[0], currentFacts);
  const choice = newChoice(workspace, 'b', 'segment', 'route'); choice.decision = 'reuse'; choice.destinationId = 'route';
  return renderToString(createSSRApp(component, { workspace, review, choice, options: null, editorUrl: '#', existingTrip }));
};

for (const existingTrip of [false, true]) test(`complete route exposes source 35 min versus original/current 5 min in ${existingTrip ? 'Copy' : 'composition'}`, async () => {
  const facts = { estimatedDistanceKm: 1.5, estimatedDurationSource: 'Manual' };
  const html = await renderRoute({ ...facts, estimatedDurationTicks: 21000000000 }, { ...facts, estimatedDurationTicks: 3000000000 }, undefined, existingTrip);
  assert.equal((html.match(/35 min/g) ?? []).length, 1); assert.equal((html.match(/(?<!\d)5 min · Manual/g) ?? []).length, 2);
  assert.match(html, /Source B/); assert.match(html, new RegExp(`Reviewed original ${existingTrip ? 'destination' : 'C'}`));
  assert.match(html, new RegExp(`Current ${existingTrip ? 'destination' : 'C'}`));
  assert.match(html, /Different saved route facts: duration/); assert.equal((html.match(/1.5 km/g) ?? []).length, 3);
});

/** Loads the real component bundle with the Greek Editor catalog in its locale bootstrap. */
async function loadGreekComponents() {
  const catalog = await readFile(new URL('../../Resources/Editor.el-GR.resx', import.meta.url), 'utf8');
  const messages = Object.fromEntries(Array.from(catalog.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([^<]+)<\/value>/g),
    ([, key, value]) => [key, value]));
  const priorDocument = globalThis.document;
  try {
    globalThis.document = { getElementById: id => id === 'wayfarer-locale' ? localeElement('el-GR', messages) : null };
    return await import(`data:text/javascript;base64,${Buffer.from(code + '\n//# sourceURL=composition-components-el-GR.mjs').toString('base64')}`);
  } finally { globalThis.document = priorDocument; }
}

test('Greek route reviews translate duration sources and fixed differences while retaining saved values', async () => {
  const { FieldChoices: greekFields } = await loadGreekComponents();
  const sourceFacts = { mode: 'Authored <Trail>', estimatedDistanceKm: 1.5, estimatedDurationSource: 'Manual',
    estimatedDurationTicks: 3000000000, routeAttribution: 'Authored & notice',
    routeInstructionsJson: null, routeProvider: null, routeStorageMode: null };
  const reviewedFacts = { ...sourceFacts, mode: 'Other trail', estimatedDistanceKm: 2.5,
    estimatedDurationSource: 'Automatic', estimatedDurationTicks: 1234567, routeInstructionsJson: '[]',
    routeProvider: 'Saved provider', routeAttribution: 'Other notice', routeStorageMode: 'Saved storage' };
  for (const existingTrip of [false, true]) {
    const html = await renderRoute(sourceFacts, reviewedFacts, sourceFacts, existingTrip, greekFields);
    assert.match(html, /Authored &lt;Trail&gt;.*1,5.*Διάρκεια: Χειροκίνητη/);
    assert.match(html, /Other trail.*2,5.*0,1234567.*Διάρκεια: Αυτόματη/);
    assert.match(html, /Αναφορά δημιουργού: Authored &amp; notice/);
    assert.match(html, /Διαφορές στα αποθηκευμένα στοιχεία διαδρομής: τρόπος μετακίνησης, απόσταση, διάρκεια, πηγή διάρκειας, οδηγίες, πάροχος, αναφορά δημιουργού, αποθήκευση/);
    assert.match(html, /Ίδια αποθηκευμένα στοιχεία διαδρομής/);
  }
});

test('route measurement absence remains distinct from zero and subminute precision is readable', async () => {
  const html = await renderRoute({ estimatedDistanceKm: null, estimatedDurationTicks: null },
    { estimatedDistanceKm: 0, estimatedDurationTicks: 0 }, { estimatedDistanceKm: 0.0001, estimatedDurationTicks: 1234567 });
  assert.match(html, /Distance unavailable · Duration unavailable/);
  assert.match(html, /0 km · 0 min/); assert.match(html, /0.0001 km · 0.1234567 sec/);
  assert.doesNotMatch(html, /Unknown km|unavailable km|NaN|Infinity/);
});

test('route comparison distinguishes saved profile and provenance without treating remapped anchor IDs as different facts', async () => {
  const facts = { estimatedDurationTicks: 3000000000, estimatedDurationSource: 'Manual', transportProfileId: 'profile-one',
    routeProvider: 'geoapify', routeProviderConfigurationId: 'config-one', routeProviderConfigurationVersion: 1,
    routeTransportProfileId: 'routed-profile-one', routeGeneratedAt: '2026-11-01T10:00:00Z', routeAttribution: 'Retain notice',
    routeStorageMode: 'persistent', routeMappingMode: 'walk', routeInstructionsJson: '[]' };
  const same = await renderRoute(facts, facts);
  assert.equal((same.match(/Same saved route facts/g) ?? []).length, 2);
  assert.match(same, /Review From \/ Via \/ To dependencies/);
  const different = await renderRoute(facts, { ...facts, transportProfileId: 'profile-two', routeProviderConfigurationVersion: 2 });
  assert.match(different, /Different saved route facts: transport profile, provider version/);
  assert.match(different, /Provider: geoapify/); assert.match(different, /Attribution: Retain notice/);
  assert.doesNotMatch(different, /profile-one|config-one|routed-profile-one/);
});

test('creation component freezes the submitted pair/name and leaves newer form choices open after success', async () => {
  const priorFetch = globalThis.fetch; const priorWindow = globalThis.window;
  let bindings, resolveSave, started; const requestStarted = new Promise(resolve => { started = resolve; });
  const waiting = new Promise(resolve => { resolveSave = resolve; }); const navigations = []; const requests = [];
  globalThis.window = { location: { assign: url => navigations.push(url) }, dispatchEvent() {} };
  globalThis.captureConstruction = value => { bindings = value; };
  globalThis.fetch = async (url, init) => {
    requests.push({ url, init });
    if (init.method === 'GET') return Response.json([]);
    started(); return waiting;
  };
  // Mount so CompositionStart's language draft registration is released by unmount.
  const { renderer, host } = createWorkspaceRenderer();
  const app = renderer.createApp(CompositionStart, { token: 'csrf' });
  try {
    app.mount(host);
    bindings.sourceA.value = 'A'; bindings.sourceB.value = 'B'; bindings.name.value = 'Submitted C';
    const save = bindings.create(); await requestStarted;
    bindings.name.value = 'Later name'; bindings.sourceB.value = 'Later B';
    resolveSave(Response.json({ tripId: 'C', compositionId: 'comparison', mediaOmitted: false })); await save;
    assert.equal(navigations.length, 0); assert.equal(bindings.name.value, 'Later name'); assert.equal(bindings.sourceB.value, 'Later B');
    assert.equal(bindings.savedResultUrl.value, '/User/Trip/Compose/comparison'); assert.equal(bindings.pending.value, false);
    const submitted = requests.find(r => r.init.method === 'POST');
    assert.equal(JSON.parse(submitted.init.body).name, 'Submitted C'); assert.equal(JSON.parse(submitted.init.body).sourceB, 'B');
  } finally {
    app.unmount();
    globalThis.fetch = priorFetch; globalThis.window = priorWindow; delete globalThis.captureConstruction;
  }
});

const snapshot = id => ({ graph: { id, name: id, regions: [{ id: `${id}R`, name: 'Region', displayOrder: 0,
  places: [{ id: `${id}P`, name: `${id} Place`, displayOrder: 0, notes: '<p>Original</p>', location: [23, 38] }], areas: [] }],
  segments: [], credits: [], tags: [], isPublic: false }, regionOrder: [`${id}R`], placeOrder: { [`${id}R`]: [`${id}P`] },
  areaOrder: { [`${id}R`]: [] }, mediaOmitted: false });
const workspace = () => ({ compositionId: 'S', tripId: 'C', sourceA: snapshot('A'), sourceB: snapshot('B'), current: snapshot('C'), mappings: [] });
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const flush = async () => { for (let tick = 0; tick < 6; tick++) { await new Promise(done => setImmediate(done)); await nextTick(); } };
const descendants = node => [node, ...(node.children ?? []).flatMap(descendants)];
const textOf = node => [node.text ?? '', ...(node.children ?? []).map(textOf)].join('');

/** Mounts Vue components with lifecycle hooks in the existing in-memory host. */
function createWorkspaceRenderer() {
  const node = (tag, text = '') => ({ tagName: tag.toUpperCase(), text, children: [], style: {}, props: {},
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {}, close() {}, focus() {},
    get options() { return this.children; }, get value() { return this._value ?? this.props.value ?? ''; }, set value(v) { this._value = v; } });
  const remove = n => { if (n.parent) n.parent.children.splice(n.parent.children.indexOf(n), 1); n.parent = null; };
  const body = node('body');
  const renderer = createRenderer({ createElement: tag => node(tag), createText: text => node('#text', text), createComment: text => node('#comment', text),
    setText: (n, text) => { n.text = text; }, setElementText: (n, text) => { n.text = text; n.children = []; },
    parentNode: n => n.parent, nextSibling: n => n.parent?.children[n.parent.children.indexOf(n) + 1] ?? null,
    patchProp: (n, key, _old, value) => { n.props[key] = value; }, remove,
    insert(n, p, anchor) { remove(n); n.parent = p; const index = anchor ? p.children.indexOf(anchor) : -1; p.children.splice(index < 0 ? p.children.length : index, 0, n); },
    querySelector: () => body });
  return { renderer, host: node('root') };
}

// Mount the production workspace/fields/shared Notes at the existing host seam, without a browser environment.
const withWorkspace = async (action, lightweight = false, component = lightweight ? CopyToTrip : CompositionApp) => {
  const prior = Object.fromEntries(['window', 'document', 'localStorage', 'fetch', 'compositionNotes', 'captureComposition', 'HTMLElement'].map(k => [k, globalThis[k]]));
  const captured = {}; const errors = []; const sent = []; const requests = []; let saved = workspace(); let readReply = null; let finishReply = { tripId: 'C', status: 'finished' }; let copyReply = null; let previewReply = null;
  let prepareReply = null;
  globalThis.captureComposition = (name, value) => { captured[name] = value; };
  globalThis.window = { location: { origin: 'https://component.invalid' }, addEventListener() {}, removeEventListener() {}, dispatchEvent() {} };
  globalThis.HTMLElement = class {};
  globalThis.document = { activeElement: null, contains: () => false };
  globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
  globalThis.fetch = async (url, init) => {
    requests.push(url);
    if (url.includes('/sources?')) return Response.json([]);
    if (url.includes('/copy-preview?')) return previewReply ? await previewReply() : Response.json({ sourceTripId: 'B', tripId: 'C', source: saved.sourceB, current: saved.current, context: 'reviewed', originals: [] });
    if (url.endsWith('/copy')) { sent.push(JSON.parse(init.body)); return copyReply ? await copyReply() : Response.json({ ...saved, status: 'copied' }); }
    if (url.endsWith('/finish')) return Response.json(finishReply);
    if (url.endsWith('/actions')) { sent.push(JSON.parse(init.body)); return Response.json(saved); }
    if (url.endsWith('/coordinates/prepare')) return prepareReply ? await prepareReply() : Response.json({ tripId: 'C', targetId: 'CP', operation: 'coordinates', conditionToken: 'reviewed', affectedSegmentIds: [] });
    if (url === '/api/trip-compositions/S') return readReply ? await readReply() : Response.json(saved);
    if (url.includes('/candidates')) return Response.json({ candidates: [] });
    if (url.endsWith('/editor')) return Response.json({ tripId: 'C', options: { iconNames: [], markerColorClasses: [], transportModes: [] } });
    assert.fail(`Unexpected request ${url}`);
  };
  const { renderer, host } = createWorkspaceRenderer();
  const app = renderer.createApp(component, { account: 'owner', token: 'csrf', ...(lightweight ? { source: 'B', destination: 'C' } : { compositionId: 'S' }) });
  app.config.errorHandler = error => { errors.push(error.message); };
  app.mount(host);
  try {
    await flush();
    await action({ captured, sent, requests, host, get saved() { return saved; }, set saved(w) { saved = w; },
      prepareWith: fn => { prepareReply = fn; }, previewWith: fn => { previewReply = fn; },
      copyWith: fn => { copyReply = fn; }, readWith: fn => { readReply = fn; }, finishWith: value => { finishReply = value; } });
  } finally {
    app.unmount(); await nextTick(); Object.assign(globalThis, prior);
    assert.deepEqual(errors, [], 'component exceptions must fail the regression');
  }
};

test('item to block to Apply adopts the owning Notes and preserves the independent item draft', () => withWorkspace(async ({ captured, sent }) => {
  const app = captured.CompositionApp;
  await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const selected = app.draft.value; const choice = selected.items[0]; choice.decision = 'reuse'; choice.destinationId = 'CP'; await nextTick();
  captured.FieldChoices.select('notes', 'edit'); await flush();
  globalThis.compositionNotes.visible = '<p>Block Notes</p>'; globalThis.compositionNotes.change(); await nextTick();
  app.addToBlock();
  globalThis.compositionNotes.visible = '<p>Item Notes</p>'; globalThis.compositionNotes.change(); await nextTick();
  const block = app.state.view.block;
  app.state.view.blockMode = true; await flush();
  assert.equal(globalThis.compositionNotes.visible, '<p>Block Notes</p>');
  globalThis.compositionNotes.visible = '<p>Edited block Notes</p>'; globalThis.compositionNotes.change(); await nextTick();
  app.state.view.blockMode = false; await flush();
  assert.equal(globalThis.compositionNotes.visible, '<p>Item Notes</p>');
  assert.equal(block.items[0].fields.notes.value, '<p>Edited block Notes</p>');
  app.state.view.blockMode = true; await flush();
  const visibleAtApply = globalThis.compositionNotes.visible;
  await app.apply(); await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].items[0].fields.notes.value, visibleAtApply);
  assert.equal(app.selectedDraft.value, selected); assert.equal(selected.items[0].fields.notes.value, '<p>Item Notes</p>');
  assert.equal(app.state.view.block, null); assert.equal(globalThis.compositionNotes.visible, '<p>Item Notes</p>');
}));

test('deferred Refresh retired by Apply settles loading, keeps submitted input and permits the next Refresh', () => withWorkspace(async ({ captured, sent, readWith, saved }) => {
  const app = captured.CompositionApp; await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  captured.FieldChoices.select('notes', 'edit'); await flush();
  globalThis.compositionNotes.visible = '<p>Submitted</p>'; globalThis.compositionNotes.change(); await nextTick();
  const oldRead = deferred(); const preparation = deferred(); let reads = 0;
  readWith(() => (++reads === 1 ? oldRead.promise : preparation.promise));
  const refresh = app.refresh(); const apply = app.apply(); await flush();
  globalThis.compositionNotes.visible = '<p>Later typing</p>'; globalThis.compositionNotes.change(); await nextTick();
  preparation.resolve(Response.json(saved)); await apply;
  const stale = structuredClone(saved); stale.current.graph.name = 'Stale read'; oldRead.resolve(Response.json(stale)); await refresh; await flush();
  assert.equal(sent[0].items[0].fields.notes.value, '<p>Submitted</p>');
  assert.equal(app.draft.value.items[0].fields.notes.value, '<p>Later typing</p>');
  assert.equal(app.state.loading, false); assert.equal(app.state.pending, false); assert.equal(app.state.workspace.current.graph.name, 'C');
  readWith(null); assert.equal(await app.refresh(), true); assert.equal(app.state.loading, false);
}));

test('mounted Region scope and Segment block submit each approved nested Place once before its route', () => withWorkspace(async ({ captured, sent, saved }) => {
  const app = captured.CompositionApp;
  saved.sourceB.graph.segments = [{ id: 'BS', title: 'Route', fromPlaceId: 'BP', toPlaceId: null, waypoints: [] }];
  await app.refresh(); await app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  const region = app.draft.value.items[0]; region.decision = 'reuse'; region.destinationId = 'CR';
  captured.ChoiceControls.scope({ target: { checked: true } }); app.addToBlock();
  await app.select({ side: 'b', kind: 'segment', id: 'BS' }); await flush();
  const route = app.draft.value.items[0]; route.anchors[0].approveCopy = true; route.anchors[0].regionId = 'CR';
  app.addToBlock(); app.state.view.blockMode = true; await flush(); await app.apply();
  assert.equal(sent.length, 1); assert.deepEqual(sent[0].items.map(i => i.kind), ['region', 'segment']);
  assert.deepEqual(sent[0].items[0].children.map(i => i.sourceId), ['BP']);
  assert.deepEqual(sent[0].items[1].anchors, [{ sourceId: 'BP', destinationId: null, regionId: null, location: null }]);
}));

test('Apply preparation rejects wrong and incomplete workspaces before publication or any preview/write', () => withWorkspace(async ({ captured, sent, requests, readWith, saved }) => {
  const app = captured.CompositionApp; await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const retained = app.draft.value;
  retained.items[0].decision = 'reuse'; retained.items[0].destinationId = 'CP';
  retained.items[0].fields.location.operation = 'useSource';
  for (const invalid of [{ ...saved, current: null }, { ...saved, sourceB: null }, { ...saved, mappings: null },
    { ...saved, compositionId: 'OTHER' }, { ...saved, tripId: 'OTHER' }]) {
    const lastWorkspace = app.state.workspace;
    const before = requests.length;
    readWith(() => Response.json(invalid)); await app.apply(); await flush();
    assert.equal(sent.length, 0); assert.equal(app.state.workspace, lastWorkspace); assert.equal(app.draft.value, retained);
    assert.deepEqual(requests.slice(before), ['/api/trip-compositions/S']);
    assert.match(app.state.error, /incomplete|context|match/i); assert.match(app.state.error, /refresh|inspect/i);
    readWith(null); const inspection = app.reviewCurrent(); await flush(); settleConfirmDialog(true); await inspection;
  }
}));

test('Finish reports an unconfirmed wrong/incomplete reply and releases inputs only after valid cleanup', () => withWorkspace(async ({ captured, finishWith, host, readWith }) => {
  const app = captured.CompositionApp; await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const retained = app.draft.value;
  app.state.error = 'Earlier action refused.';
  for (const reply of [{ tripId: 'OTHER', status: 'finished' }, { tripId: 'C' }, null]) {
    const current = app.state.workspace;
    finishWith(reply); const attempt = app.finish(); settleConfirmDialog(true); await attempt; await nextTick();
    assert.equal(app.finished.value, false); assert.equal(app.state.workspace, current); assert.equal(app.draft.value, retained);
    assert.match(app.lifecycleError.value, /could not be confirmed/i); assert.equal(app.finishing.value, false);
    assert.match(textOf(host), /Finish could not be confirmed/);
    assert.equal(isLanguageNavigationHeld(), true);
    const inspection = app.reviewCurrent(); await flush(); settleConfirmDialog(true); await inspection;
  }
  // A lost final reply can be resolved by the authoritative closed response to explicit inspection.
  finishWith(null); const attempt = app.finish(); settleConfirmDialog(true); await attempt;
  readWith(() => Response.json({ code: 'composition-closed' }, { status: 409 }));
  await app.refresh(); assert.equal(isLanguageNavigationHeld(), true);
  await app.reviewCurrent();
  assert.equal(app.finished.value, true); assert.equal(app.finishedTripId.value, 'C'); assert.equal(app.state.workspace, null);
  assert.deepEqual({ ...app.state.view.drafts }, {}); assert.equal(app.state.view.block, null);
}));

test('refresh/edit-return shows current C separately from retained originals and retains explicit originals review', () => withWorkspace(async ({ captured, host, saved }) => {
  const app = captured.CompositionApp; await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const retained = app.draft.value; const choice = retained.items[0]; choice.decision = 'reuse'; choice.destinationId = 'CP'; await nextTick();
  captured.FieldChoices.select('notes', 'edit'); await flush();
  globalThis.compositionNotes.visible = '<p>Typed choice</p>'; globalThis.compositionNotes.change(); await nextTick();
  const serialized = app.store.retain(); app.store.restore(serialized);
  saved.current.graph.regions[0].places[0].notes = '<p>Saved elsewhere</p>'; await app.refresh(); await flush();
  assert.equal(app.draft.value.baseline.graph.regions[0].places[0].notes, '<p>Original</p>');
  const notesField = descendants(host).find(n => n.tagName === 'FIELDSET' && textOf(n).startsWith('Notes'));
  assert.match(textOf(notesField), /Reviewed original C/); assert.match(textOf(notesField), /Current C.*Different value/);
  assert.match(textOf(notesField), /changed since.*review/i);
  assert.deepEqual(descendants(notesField).filter(n => n.props.innerHTML).map(n => n.props.innerHTML),
    ['<p>Original</p>', '<p>Original</p>', '<p>Saved elsewhere</p>']);
  const reviewing = app.reviewCurrent(); await flush();
  assert.equal(app.draft.value.baseline.graph.regions[0].places[0].notes, '<p>Original</p>');
  settleConfirmDialog(true); await reviewing;
  assert.equal(app.draft.value.baseline.graph.regions[0].places[0].notes, '<p>Saved elsewhere</p>');
  assert.equal(app.draft.value.items[0].fields.notes.value, '<p>Typed choice</p>');
}));


test('lightweight copy owns immutable submitted Notes while newer choices and selection survive the acknowledgement', () => withWorkspace(async ({ captured, sent, copyWith, saved, host }) => {
  const app = captured.CopyToTrip;
  app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const choice = app.draft.value.items[0]; choice.decision = 'reuse'; choice.destinationId = 'CP';
  await flush(); captured.FieldChoices.select('notes', 'edit'); await flush();
  globalThis.compositionNotes.visible = '<p>Submitted Notes</p>'; globalThis.compositionNotes.change(); await nextTick();
  const wait = deferred(); copyWith(() => wait.promise); const apply = app.apply(); await flush();
  globalThis.compositionNotes.visible = '<p>Later Notes</p>'; globalThis.compositionNotes.change();
  app.select({ side: 'b', kind: 'region', id: 'BR' }); await nextTick();
  wait.resolve(Response.json({ ...saved, status: 'copied' })); await apply; await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].sourceTripId, 'B'); assert.equal(sent[0].destinationTripId, 'C');
  assert.equal(sent[0].items[0].fields.notes.value, '<p>Submitted Notes</p>');
  assert.equal(app.draft.value.items[0].fields.notes.value, '<p>Later Notes</p>');
  assert.equal(app.draft.value.items.length, 2); assert.equal(app.state.value.view.selection.id, 'BR');
  assert.match(app.state.value.status, /Earlier action saved.*Newer choices remain unsaved/);
  assert.equal(descendants(host).filter(n => n.tagName === 'BUTTON' && /^(Finish comparison|Start New|Trip details)/.test(textOf(n))).length, 0);
}, true));

test('lightweight refusal retains destination, placement and choices without replay; changed pair cannot save', () => withWorkspace(async ({ captured, sent, copyWith }) => {
  const app = captured.CopyToTrip; app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const choice = app.draft.value.items[0]; choice.regionId = 'CR'; choice.position = 'after'; choice.afterId = 'CP';
  copyWith(async () => Response.json({ detail: 'Reviewed source changed.' }, { status: 409 }));
  await app.apply(); await flush();
  assert.equal(sent.length, 1); assert.equal(app.draft.value.items[0], choice); assert.equal(choice.afterId, 'CP');
  assert.equal(app.destinationId.value, 'C'); assert.equal(app.state.value.needsInspection, true);
  assert.match(app.state.value.error, /saved content changed/);
  await app.apply(); assert.equal(sent.length, 1);
  app.destinationId.value = 'Another Trip'; await app.apply(); assert.equal(sent.length, 1);
  assert.equal(app.preview.value.tripId, 'C');
}, true));


for (const lightweight of [false, true]) test(`${lightweight ? 'Copy' : 'Compose'} Keep choices retains coordinate edits and restores the language draft guard`,
  () => withWorkspace(async ({ captured, saved, requests, sent }) => {
    saved.sourceB.graph.regions[0].places[0].location = [24, 37];
    const app = lightweight ? captured.CopyToTrip : captured.CompositionApp;
    if (lightweight) await app.load();
    await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
    const draft = app.draft.value;
    const choice = draft.items[0];
    Object.assign(choice, { decision: 'reuse', destinationId: 'CP' });
    choice.fields.location.operation = 'useSource';
    const applying = app.apply(); await flush();
    assert.equal(requests.filter(url => url.endsWith('/coordinates/prepare')).length, 1);
    assert.equal(isLanguageNavigationHeld(), true);
    settleConfirmDialog(false); await applying; await flush();
    assert.equal(sent.length, 0);
    assert.equal(app.draft.value, draft);
    assert.equal(choice.fields.location.operation, 'useSource');
    assert.equal(hasLanguageDraft(), true);
    assert.equal(isLanguageNavigationHeld(), false);
  }, lightweight));

// Lose the nonmutating request in each Greek mount before it can ask for save consent.
for (const [mount, lightweight] of [['CompositionApp', false], ['CopyToTrip', true]]) {
  test(`${lightweight ? 'Copy' : 'Compose'} lost coordinate preview reply restores language draft consent and displays Greek feedback`, async () => {
    const components = await loadGreekComponents();
    await withWorkspace(async ({ captured, saved, prepareWith, requests, sent, host }) => {
      saved.sourceB.graph.regions[0].places[0].location = [24, 37];
      const app = captured[mount];
      if (lightweight) await app.load();
      await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
      const draft = app.draft.value;
      const choice = draft.items[0];
      Object.assign(choice, { decision: 'reuse', destinationId: 'CP' });
      choice.fields.location.operation = 'useSource';
      prepareWith(() => Promise.reject(new Error('lost preview response')));

      await app.apply(); await flush();

      assert.equal(requests.filter(url => url.endsWith('/coordinates/prepare')).length, 1);
      assert.equal(sent.length, 0);
      assert.equal(app.draft.value, draft);
      assert.equal(choice.fields.location.operation, 'useSource');
      assert.equal(hasLanguageDraft(), true);
      assert.equal(isLanguageNavigationHeld(), false, 'a lost preview leaves only the ordinary draft guard');
      const state = lightweight ? app.state.value : app.state;
      assert.equal(state.needsInspection, false);
      assert.equal(state.error, 'Η προεπισκόπηση δεν είναι διαθέσιμη.');
      assert.ok(descendants(host).some(node => node.props.role === 'alert' && textOf(node).includes(state.error)));
    }, lightweight, components[mount]);
    assert.equal(hasLanguageDraft(), false, 'unmount clears the draft owner');
    assert.equal(isLanguageNavigationHeld(), false, 'unmount clears the workspace hold');
  });
}

/** Drives the production selector beside the mounted Copy state using its own English locale bootstrap. */
async function inspectLanguageControls(action) {
  const names = ['window', 'document', 'FormData', 'location'];
  const previous = Object.fromEntries(names.map(name => [name, globalThis[name]]));
  const events = new Map();
  const controls = new Map();
  let focused;
  const get = id => {
    if (!controls.has(id)) controls.set(id, { dataset: {}, hidden: true, open: false, reset() {}, setAttribute() {},
      addEventListener: (name, callback) => events.set(`${id}:${name}`, callback),
      focus: () => { focused = id; },
      showModal() { this.open = true; }, close() { this.open = false; events.get(`${id}:close`)(); }
    });
    return controls.get(id);
  };
  get('language-dialog').dataset.workspace = 'true';
  get('language-form').dataset.url = '/Language/Set';
  globalThis.document = { ...document, getElementById: id => id === 'wayfarer-locale' ? localeElement('en-US') : get(id),
    querySelectorAll: () => [], addEventListener() {} };
  globalThis.window = {
    ...window, addEventListener: (name, callback) => events.set(name, callback),
    dispatchEvent: event => events.get(event.type)?.()
  };
  globalThis.location = window.location = { href: 'https://component.invalid/User/Trip/Copy' };
  globalThis.FormData = class { get() { return 'el-GR'; } };
  try {
    await import('../../wwwroot/js/localization/languageSelector.js');
    await action({
      get, event: (id, type) => events.get(`${id}:${type}`)({ preventDefault() {} }),
      focus: () => focused
    });
  } finally { Object.assign(globalThis, previous); }
}

test('Copy language cancellation keeps the review and only explicit successful inspection releases an unconfirmed save hold',
  () => withWorkspace(async ({ captured, requests, copyWith }) => {
    const app = captured.CopyToTrip;
    await inspectLanguageControls(async ({ get, event, focus }) => {
      assert.equal(get('language-open').hidden, true, 'the workspace must register its owners before being shown');
      enableWorkspaceLanguage();
      app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
      const choice = app.draft.value.items[0];
      Object.assign(choice, { regionId: 'CR', position: 'after', afterId: 'CP', customName: true, name: 'Μονοπάτι <Trail>' });
      assert.equal(hasLanguageDraft(), true);
      event('language-open', 'click');
      await event('language-form', 'submit');
      assert.equal(focus(), 'language-keep');
      event('language-keep', 'click');
      assert.equal(focus(), 'language-open');
      assert.equal(app.draft.value.items[0], choice);
      assert.equal(choice.name, 'Μονοπάτι <Trail>');

      copyWith(() => Promise.reject(new Error('lost response')));
      await app.apply(); await flush();
      assert.match(app.state.value.error, /could not be confirmed/);
      assert.equal(get('language-open').hidden, true);
      await event('language-form', 'submit');
      const cancelInspection = app.load(); settleConfirmDialog(false); await cancelInspection;
      assert.equal(get('language-open').hidden, true);
      const inspect = app.load(); settleConfirmDialog(true); await inspect;
      assert.equal(get('language-open').hidden, false);
      assert.equal(app.draft.value.items[0].afterId, 'CP');
      assert.equal(app.draft.value.items[0].name, 'Μονοπάτι <Trail>');
      event('language-open', 'click');
      await event('language-form', 'submit');
      assert.equal(focus(), 'language-keep', 'ordinary draft consent still applies after inspection');
      assert.equal(requests.filter(url => url === '/Language/Set').length, 0);
    });
  }, true));

test('changing the destination while copy waits keeps that newer context and never labels its choices saved', () => withWorkspace(async ({ captured, sent, copyWith, saved }) => {
  const app = captured.CopyToTrip; app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  app.draft.value.items[0].regionId = 'CR'; const retained = app.draft.value;
  const wait = deferred(); copyWith(() => wait.promise); const apply = app.apply(); await flush();
  app.destinationId.value = 'Later destination';
  wait.resolve(Response.json({ ...saved, status: 'copied' })); await apply;
  assert.equal(sent[0].destinationTripId, 'C'); assert.equal(app.destinationId.value, 'Later destination');
  assert.equal(app.draft.value, retained); assert.equal(app.draft.value.items.length, 1);
  assert.match(app.state.value.status, /Earlier action saved.*Newer choices remain unsaved/);
}, true));


test('explicit same-pair refresh retains newer selection, expansion and local choices while its preview waits', () => withWorkspace(async ({ captured, previewWith, saved }) => {
  const app = captured.CopyToTrip; app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const wait = deferred(); previewWith(() => wait.promise);
  const read = app.load(); settleConfirmDialog(true); await flush();
  app.draft.value.items[0].customName = true; app.draft.value.items[0].name = 'Later name';
  app.select({ side: 'b', kind: 'region', id: 'BR' }); app.expanded.value = ['b/segments'];
  wait.resolve(Response.json({ sourceTripId: 'B', tripId: 'C', source: saved.sourceB, current: saved.current, context: 'refreshed', originals: [] }));
  await read; await flush();
  assert.equal(app.state.value.view.selection.id, 'BR'); assert.equal(app.draft.value.items.length, 2);
  assert.equal(app.draft.value.items[0].name, 'Later name'); assert.deepEqual([...app.expanded.value], ['b/segments']);
}, true));


// A changed pair may replace only the exact prior review the user agreed to discard.
test('pair-changing preview retains post-confirmation names, fields, selection and expansion until renewed consent', () => withWorkspace(async ({ captured, previewWith, saved, sent }) => {
  const app = captured.CopyToTrip; app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const oldReview = app.draft.value; const choice = oldReview.items[0];
  choice.decision = 'reuse'; choice.destinationId = 'CP';
  const oldPreview = app.preview.value;
  app.destinationId.value = 'D';
  const wait = deferred(); previewWith(() => wait.promise);
  const read = app.load(); settleConfirmDialog(true); await flush();
  choice.customName = true; choice.name = 'Typed after pair confirmation';
  choice.fields.notes = { operation: 'edit', value: '<p>Later Notes in C</p>' };
  app.select({ side: 'b', kind: 'region', id: 'BR' }); app.expanded.value = ['b/segments'];
  const destination = snapshot('D');
  const response = () => Response.json({ sourceTripId: 'B', tripId: 'D', source: saved.sourceB, current: destination, context: 'new-pair', originals: [] });
  wait.resolve(response()); await read; await flush();
  assert.equal(app.draft.value, oldReview); assert.equal(app.preview.value, oldPreview);
  assert.equal(choice.name, 'Typed after pair confirmation'); assert.equal(choice.fields.notes.value, '<p>Later Notes in C</p>');
  assert.equal(app.state.value.view.selection.id, 'BR'); assert.deepEqual([...app.expanded.value], ['b/segments']);
  assert.equal(app.draft.value.baseline.graph.id, 'C'); assert.match(app.error.value, /Preview again to confirm replacement/);
  await app.apply(); assert.equal(sent.length, 0);
  const cancelled = app.load(); settleConfirmDialog(false); await cancelled;
  assert.equal(app.draft.value, oldReview); assert.equal(app.preview.value, oldPreview);
  previewWith(response);
  const renewed = app.load(); settleConfirmDialog(true); await renewed; await flush();
  assert.equal(app.preview.value.tripId, 'D'); assert.equal(app.draft.value.baseline.graph.id, 'D');
  assert.equal(app.draft.value.items.length, 0); assert.equal(app.state.value.view.selection, null);
  assert.equal(choice.destinationId, 'CP'); // The old object was never repurposed as D authority.
}, true));

test('pair and draft changes during confirmation and a refused pair preview retain the prior review', () => withWorkspace(async ({ captured, requests, previewWith }) => {
  const app = captured.CopyToTrip; app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const review = app.draft.value; const before = requests.length;
  app.destinationId.value = 'D';
  const moved = app.load(); app.destinationId.value = 'E'; settleConfirmDialog(true); await moved;
  assert.equal(requests.length, before); assert.equal(app.preview.value.tripId, 'C');
  const changed = app.load(); review.items[0].name = 'Changed during confirmation'; settleConfirmDialog(true); await changed;
  assert.equal(requests.length, before); assert.equal(app.draft.value, review);
  previewWith(async () => Response.json({ detail: 'Destination unavailable' }, { status: 409 }));
  const refused = app.load(); settleConfirmDialog(true); await refused;
  assert.equal(app.draft.value, review); assert.equal(app.preview.value.tripId, 'C'); assert.match(app.error.value, /saved content changed/);
}, true));

for (const mode of ['copy', 'reuse', 'foreign']) test(`nested Region ${mode} uses the same selected fields and one submitted child identity`, () => withWorkspace(async ({ captured, host, saved, sent }) => {
  const app = captured.CopyToTrip; const own = mode !== 'foreign';
  const planning = label => ({ format: 1, entries: [{ id: 'entry', when: { form: 'unknown' }, label }] });
  saved.sourceB.graph.regions[0].areas = [{ id: 'BA', name: 'Source Area', notes: '<p>Area source</p>', fillHex: '#abcdef', geometryGeoJson: '{"type":"Polygon","coordinates":[[[23,38],[24,38],[24,39],[23,38]]]}' }];
  saved.sourceB.areaOrder.BR = ['BA'];
  saved.current.graph.regions[0].areas = [{ ...saved.sourceB.graph.regions[0].areas[0], id: 'CA', name: 'Current Area', notes: '<p>Area current</p>' }];
  saved.current.areaOrder.CR = ['CA'];
  saved.current.graph.regions[0].places[0].notes = '<p>Current child</p>';
  if (own) saved.sourceB.planning = { trip: null, regions: { BR: null }, places: { BP: planning('Source intention') }, areas: { BA: null }, segments: {} };
  saved.current.planning = { trip: null, regions: { CR: null }, places: { CP: planning('Destination intention') }, areas: { CA: null }, segments: {} };
  await app.load(); await flush();
  // A child chosen first must become the same nested choice when its Region scope expands.
  app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const first = app.draft.value.items[0]; first.customName = true; first.name = 'Retained child name';
  app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  captured.ChoiceControls.scope({ target: { checked: true } }); await flush();
  const region = app.draft.value.items[0]; const child = region.children[0]; const area = region.children[1];
  assert.equal(child, first); assert.equal(app.draft.value.items.length, 1);
  if (mode === 'reuse') { child.decision = 'reuse'; child.destinationId = 'CP'; area.decision = 'reuse'; area.destinationId = 'CA'; }
  await flush();
  app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  assert.equal(app.selected.value, child); assert.equal(app.draft.value.items.length, 1);
  const childNode = kind => descendants(host).find(n => n.tagName === 'FIELDSET' && n.props.class?.includes('composition-choice') && textOf(n).toLowerCase().startsWith(`${kind} ·`));
  const action = (node, title, operation) => {
    const select = descendants(node).find(n => n.tagName === 'SELECT' && n.props['aria-label'] === `${title} action`);
    assert.ok(select, `${title} must be reachable for the nested child`); select.props.onChange({ target: { value: operation } });
  };
  const placeNode = childNode('place'); const areaNode = childNode('area');
  for (const title of ['Notes', 'Icon', 'Marker colour', 'Address & enrichment', 'Location'])
    assert.ok(descendants(placeNode).some(n => n.props['aria-label'] === `${title} action`));
  action(placeNode, 'Notes', 'edit'); await flush();
  assert.equal(globalThis.compositionNotes.visible, mode === 'reuse' ? '<p>Current child</p>' : '<p>Original</p>');
  globalThis.compositionNotes.visible = '<p>Authored nested Notes</p>'; globalThis.compositionNotes.change(); await nextTick();
  action(placeNode, 'Icon', 'useSource'); action(placeNode, 'Address & enrichment', 'useSource');
  action(areaNode, 'Notes', 'useSource'); action(areaNode, 'Area fill', 'useSource');
  if (own) {
    assert.equal(child.fields.planning.operation, 'keep');
    action(placeNode, 'Personal planning', 'useSource'); action(areaNode, 'Personal planning', 'clear');
    assert.match(textOf(placeNode), /Source intention/);
  } else assert.doesNotMatch(textOf(placeNode) + textOf(areaNode), /Personal planning|Source intention|Destination intention|No personal planning|No planned/i);
  if (mode === 'reuse') {
    const note = descendants(placeNode).find(n => n.tagName === 'FIELDSET' && textOf(n).startsWith('Notes'));
    assert.match(textOf(note), /Reviewed original destination.*Current destination/s);
    assert.deepEqual(descendants(note).filter(n => n.props.innerHTML).map(n => n.props.innerHTML), ['<p>Original</p>', '<p>Current child</p>', '<p>Current child</p>']);
  }
  await app.apply(); await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].items.length, 1); assert.equal(sent[0].items[0].children.length, 2);
  const item = sent[0].items[0].children[0];
  assert.equal(item.sourceId, 'BP'); assert.equal(item.fields.notes.value, '<p>Authored nested Notes</p>');
  assert.equal(item.fields.notes.expected, mode === 'reuse' ? '<p>Current child</p>' : null);
  if (mode === 'reuse') {
    assert.equal(item.destinationId, 'CP'); assert.equal(item.expectedParentId, 'CR');
    assert.equal(item.placement, undefined); assert.equal(item.regionId, undefined);
  }
  if (own) { assert.equal(item.fields.planning.operation, 'useSource'); assert.equal(sent[0].items[0].children[1].fields.planning.operation, 'clear'); }
  else assert.equal(item.fields.planning, undefined);
}, true));

// Scope changes keep the existing child owner; Apply must encode the retained authored intent.
test('scope off, child reselection and scope on retain authored names, fields, Notes and decisions', () => withWorkspace(async ({ captured, sent }) => {
  const app = captured.CopyToTrip;
  app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  const owner = captured.ChoiceControls;
  owner.scope({ target: { checked: true } }); await flush();
  const region = app.draft.value.items[0]; const child = region.children[0];
  child.customName = true; child.name = 'Authored nested name'; child.separate = true;
  child.fields.notes = { operation: 'edit', value: '<p>Authored nested Notes</p>' };
  child.fields.iconName.operation = 'useSource'; child.decision = 'skip';
  owner.scope({ target: { checked: false } }); await flush();
  app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  assert.equal(app.selected.value, child); assert.equal(app.selected.value.decision, 'skip');
  child.decision = 'copy';
  owner.scope({ target: { checked: true } }); await flush();
  assert.equal(region.children[0], child); assert.equal(app.draft.value.items.length, 1);
  await app.apply(); await flush();
  assert.equal(sent.length, 1); const submitted = sent[0].items[0].children;
  assert.equal(submitted.length, 1); assert.equal(submitted[0].name, 'Authored nested name');
  assert.equal(submitted[0].separate, true); assert.equal(submitted[0].fields.iconName.operation, 'useSource');
  assert.equal(submitted[0].fields.notes.value, '<p>Authored nested Notes</p>');
}, true));

// The production template must expose the effective parent and an actionable inherited After conflict.
test('folded child shows effective placement and accepts deliberate resolution without losing fields', () => withWorkspace(async ({ captured, host, sent }) => {
  const app = captured.CopyToTrip;
  app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const first = app.draft.value.items[0]; first.regionId = 'CR'; first.position = 'after'; first.afterId = 'CP';
  first.fields.notes = { operation: 'edit', value: '<p>Retained placement Notes</p>' };
  app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  const owner = captured.ChoiceControls;
  owner.scope({ target: { checked: true } }); await flush();
  const region = app.draft.value.items[0]; region.customName = true; region.name = 'New container'; await flush();
  const childNode = () => descendants(host).find(n => n.tagName === 'FIELDSET' && n.props.class?.includes('composition-choice') && textOf(n).startsWith('Place ·'));
  assert.match(textOf(childNode()), /Result: New normal Region “New container”.*After C Place/s);
  assert.match(textOf(childNode()), /Choose Append or At start.*turn off Region children/s);
  await app.apply(); assert.equal(sent.length, 0); assert.equal(first.position, 'after'); assert.equal(first.afterId, 'CP');
  // Cancelling the fold restores the authored standalone placement; refolding still requires explicit resolution.
  owner.scope({ target: { checked: false } }); app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  assert.equal(app.selected.value, first); assert.equal(first.afterId, 'CP');
  owner.scope({ target: { checked: true } }); await flush();
  const position = descendants(childNode()).find(n => n.tagName === 'SELECT' && n.props['aria-label'] === 'Insertion position');
  assert.ok(position); position.props['onUpdate:modelValue']('start'); await flush();
  assert.match(textOf(childNode()), /Result: New normal Region “New container”.*At start/s);
  await app.apply(); await flush();
  assert.equal(sent.length, 1); const submitted = sent[0].items[0].children[0];
  assert.deepEqual(submitted.placement, { expected: [], position: 'start' });
  assert.equal(submitted.regionId, undefined); assert.equal(submitted.fields.notes.value, '<p>Retained placement Notes</p>');
}, true));

// A block reconciles only its own copies. Independent item drafts keep their names/Notes and owners.
for (const kind of ['place', 'area']) test(`shared block folds one ${kind} identity and keeps independent item Notes`, () => withWorkspace(async ({ captured, host, saved, sent }) => {
  const app = captured.CompositionApp;
  if (kind === 'area') {
    saved.sourceB.graph.regions[0].areas = [{ id: 'BA', name: 'Source Area', notes: '<p>Area source</p>' }];
    saved.sourceB.areaOrder.BR = ['BA']; await app.refresh();
  }
  const id = kind === 'place' ? 'BP' : 'BA';
  await app.select({ side: 'b', kind, id }); await flush();
  const itemDraft = app.draft.value; const first = itemDraft.items[0];
  first.customName = true; first.name = 'Reviewed child'; first.fields.notes = { operation: 'edit', value: '<p>Block child Notes</p>' };
  app.addToBlock(); first.fields.notes.value = '<p>Independent item Notes</p>';
  await app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  captured.ChoiceControls.scope({ target: { checked: true } }); await flush();
  const regionDraft = app.draft.value; const defaultChild = regionDraft.items[0].children.find(c => c.sourceId === id);
  app.addToBlock(); app.state.view.blockMode = true; await flush();
  assert.equal(app.draft.value.items.length, 1);
  const nested = app.draft.value.items[0].children.find(c => c.sourceId === id);
  assert.equal(nested.name, 'Reviewed child'); assert.notEqual(nested, first); assert.notEqual(nested, defaultChild);
  const childNode = descendants(host).find(n => n.tagName === 'FIELDSET' && n.props.class?.includes('composition-choice') && textOf(n).toLowerCase().startsWith(`${kind} ·`));
  assert.ok(descendants(childNode).some(n => n.props['aria-label'] === 'Notes action'));
  await app.apply(); await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].items.length, 1);
  const submitted = sent[0].items[0].children.filter(c => c.sourceId === id);
  assert.equal(submitted.length, 1); assert.equal(submitted[0].name, 'Reviewed child');
  assert.equal(submitted[0].fields.notes.value, '<p>Block child Notes</p>');
  assert.equal(first.fields.notes.value, '<p>Independent item Notes</p>');
  assert.equal(defaultChild.fields.notes.operation, 'keep');
}));

test('conflicting authored block and Region choices refuse addition and retain both drafts until deliberate resolution', () => withWorkspace(async ({ captured, host, sent }) => {
  const app = captured.CompositionApp;
  await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  app.draft.value.items[0].fields.notes = { operation: 'edit', value: '<p>Block choice</p>' }; app.addToBlock();
  const block = app.state.view.block;
  await app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  const owner = captured.ChoiceControls; owner.scope({ target: { checked: true } }); await flush();
  const item = app.draft.value; const child = item.items[0].children[0];
  child.fields.notes = { operation: 'edit', value: '<p>Independent Region choice</p>' };
  app.addToBlock(); await flush();
  assert.equal(app.state.view.block, block); assert.equal(block.items.length, 1);
  assert.equal(block.items[0].fields.notes.value, '<p>Block choice</p>');
  assert.equal(child.fields.notes.value, '<p>Independent Region choice</p>');
  assert.match(textOf(host), /Conflicting choices.*Keep Region only.*remove.*local block/s);
  assert.equal(sent.length, 0);
  owner.scope({ target: { checked: false } }); app.addToBlock(); app.state.view.blockMode = true; await flush();
  await app.apply(); await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].items.filter(c => c.sourceId === 'BP').length, 1);
  assert.equal(sent[0].items.find(c => c.sourceId === 'BP').fields.notes.value, '<p>Block choice</p>');
  assert.equal(child.fields.notes.value, '<p>Independent Region choice</p>');
}));

test('scope refuses conflicting retained child choices without enabling scope or overwriting either owner', () => withWorkspace(async ({ captured, host, sent }) => {
  const app = captured.CopyToTrip;
  app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush();
  const owner = captured.ChoiceControls; owner.scope({ target: { checked: true } }); await flush();
  const region = app.draft.value.items[0]; const cached = region.children[0];
  cached.fields.notes = { operation: 'edit', value: '<p>Retained nested choice</p>' };
  owner.scope({ target: { checked: false } }); await flush();
  // Previously retained reviews can contain a distinct direct choice for the cached child.
  const standalone = JSON.parse(JSON.stringify(cached)); standalone.fields.notes.value = '<p>Retained direct choice</p>';
  app.draft.value.items.push(standalone); await flush();
  const input = { checked: true }; owner.scope({ target: input }); await flush();
  assert.equal(input.checked, false); assert.equal(region.withChildren, false);
  assert.equal(app.draft.value.items[1].fields.notes.value, '<p>Retained direct choice</p>');
  assert.equal(cached.fields.notes.value, '<p>Retained nested choice</p>');
  assert.match(textOf(host), /Conflicting choices.*remove.*local block\/review/s);
  app.draft.value.items.splice(1, 1); owner.scope({ target: { checked: true } }); await flush();
  await app.apply(); await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].items[0].children.length, 1);
  assert.equal(sent[0].items[0].children[0].fields.notes.value, '<p>Retained nested choice</p>');
}, true));

// Adding another reviewed item during Apply must retain the block owner used by submitted-draft comparison.
test('block additions during Apply remain newer unsaved choices after the original acknowledgement', () => withWorkspace(async ({ captured, sent, saved, readWith }) => {
  const app = captured.CompositionApp;
  await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush(); app.addToBlock();
  const block = app.state.view.block; const first = block.items[0];
  app.state.view.blockMode = true;
  const wait = deferred(); readWith(() => wait.promise);
  const applying = app.apply(); await flush();
  app.state.view.blockMode = false; await app.select({ side: 'b', kind: 'region', id: 'BR' }); await flush(); app.addToBlock();
  app.state.view.blockMode = true;
  assert.equal(app.state.view.block, block); assert.equal(block.items[0], first);
  wait.resolve(Response.json(saved)); await applying; await flush();
  assert.equal(sent.length, 1); assert.equal(sent[0].items.length, 1);
  assert.equal(app.state.view.block, block); assert.equal(block.items.length, 2);
  assert.match(app.state.status, /Earlier action saved.*Newer choices remain unsaved/);
}));
