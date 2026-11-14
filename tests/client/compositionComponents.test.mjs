import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { build } from 'esbuild';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { createSSRApp, createRenderer, nextTick } from 'vue';
import { renderToString } from '@vue/server-renderer';

// Compile the actual hierarchy/choice components at the existing in-memory Vue seam.
const bundle = await build({ stdin: { contents: `export { default as CompositionApp } from './CompositionApp.vue'; export { settleConfirmDialog } from '../composables/useConfirmDialog'; export { default as SourceTree } from './SourceTree.vue'; export { default as ChoiceControls } from './ChoiceControls.vue'; export { default as CompositionStart } from './CompositionStart.vue';`,
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
const { SourceTree, ChoiceControls, CompositionStart, CompositionApp, settleConfirmDialog } = await import(`data:text/javascript;base64,${Buffer.from(code + '\n//# sourceURL=composition-components.mjs').toString('base64')}`);
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

test('creation component freezes the submitted pair/name and leaves newer form choices open after success', async () => {
  const priorFetch = globalThis.fetch; const priorWindow = globalThis.window;
  let bindings, resolveSave, started; const requestStarted = new Promise(resolve => { started = resolve; });
  const waiting = new Promise(resolve => { resolveSave = resolve; }); const navigations = []; const requests = [];
  globalThis.window = { location: { assign: url => navigations.push(url) } };
  globalThis.captureConstruction = value => { bindings = value; };
  globalThis.fetch = async (url, init) => {
    requests.push({ url, init });
    if (init.method === 'GET') return Response.json([]);
    started(); return waiting;
  };
  try {
    await renderToString(createSSRApp(CompositionStart, { token: 'csrf' }));
    bindings.sourceA.value = 'A'; bindings.sourceB.value = 'B'; bindings.name.value = 'Submitted C';
    const save = bindings.create(); await requestStarted;
    bindings.name.value = 'Later name'; bindings.sourceB.value = 'Later B';
    resolveSave(Response.json({ tripId: 'C', compositionId: 'comparison', mediaOmitted: false })); await save;
    assert.equal(navigations.length, 0); assert.equal(bindings.name.value, 'Later name'); assert.equal(bindings.sourceB.value, 'Later B');
    assert.equal(bindings.savedResultUrl.value, '/User/Trip/Compose/comparison'); assert.equal(bindings.pending.value, false);
    const submitted = requests.find(r => r.init.method === 'POST');
    assert.equal(JSON.parse(submitted.init.body).name, 'Submitted C'); assert.equal(JSON.parse(submitted.init.body).sourceB, 'B');
  } finally { globalThis.fetch = priorFetch; globalThis.window = priorWindow; delete globalThis.captureConstruction; }
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

// Mount the production workspace/fields/shared Notes at the existing host seam, without a browser environment.
const withWorkspace = async action => {
  const prior = Object.fromEntries(['window', 'document', 'localStorage', 'fetch', 'compositionNotes', 'captureComposition', 'HTMLElement'].map(k => [k, globalThis[k]]));
  const captured = {}; const errors = []; const sent = []; const requests = []; let saved = workspace(); let readReply = null; let finishReply = { tripId: 'C', status: 'finished' };
  globalThis.captureComposition = (name, value) => { captured[name] = value; };
  globalThis.window = { location: { origin: 'https://component.invalid' }, addEventListener() {}, removeEventListener() {} };
  globalThis.HTMLElement = class {};
  globalThis.document = { activeElement: null, contains: () => false };
  globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
  globalThis.fetch = async (url, init) => {
    requests.push(url);
    if (url.endsWith('/finish')) return Response.json(finishReply);
    if (url.endsWith('/actions')) { sent.push(JSON.parse(init.body)); return Response.json(saved); }
    if (url === '/api/trip-compositions/S') return readReply ? await readReply() : Response.json(saved);
    if (url.includes('/candidates')) return Response.json({ candidates: [] });
    if (url.endsWith('/editor')) return Response.json({ tripId: 'C', options: { iconNames: [], markerColorClasses: [] } });
    assert.fail(`Unexpected request ${url}`);
  };
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
  const host = node('root'); const app = renderer.createApp(CompositionApp, { account: 'owner', compositionId: 'S', token: 'csrf' });
  app.config.errorHandler = error => { errors.push(error.message); };
  app.mount(host);
  try {
    await flush();
    await action({ captured, sent, requests, host, get saved() { return saved; }, set saved(w) { saved = w; },
      readWith: fn => { readReply = fn; }, finishWith: value => { finishReply = value; } });
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
    readWith(null); await app.refresh();
  }
}));

test('Finish reports an unconfirmed wrong/incomplete reply and releases inputs only after valid cleanup', () => withWorkspace(async ({ captured, finishWith, host }) => {
  const app = captured.CompositionApp; await app.select({ side: 'b', kind: 'place', id: 'BP' }); await flush();
  const retained = app.draft.value; const current = app.state.workspace;
  app.state.error = 'Earlier action refused.';
  for (const reply of [{ tripId: 'OTHER', status: 'finished' }, { tripId: 'C' }, null]) {
    finishWith(reply); const attempt = app.finish(); settleConfirmDialog(true); await attempt; await nextTick();
    assert.equal(app.finished.value, false); assert.equal(app.state.workspace, current); assert.equal(app.draft.value, retained);
    assert.match(app.lifecycleError.value, /could not be confirmed/i); assert.equal(app.finishing.value, false);
    assert.match(textOf(host), /Finish could not be confirmed/);
  }
  finishWith({ tripId: 'C', status: 'finished' }); const attempt = app.finish(); settleConfirmDialog(true); await attempt;
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
