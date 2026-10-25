import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs } from 'vue';

// Mount the actual App -> TripSidebar -> RegionManager queue; substitute only map, DOM/Quill and unrelated leaves.
const bundle = await build({ stdin: { contents: 'export { default } from "./App.vue";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'place-parent-tree', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (/SegmentEditorSurface\.vue$/.test(args.path)) return { contents: `export default { setup(_, context) {
        context.expose({ focusNotes() {}, focusRouteAction: () => false }); return () => null;
      } };` };
      if (!/(?:App|TripSidebar|RegionManager|PlaceEditorSurface|EditorSurface)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: descriptor.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: script.bindings } });
      return { loader: 'ts', contents: `${script.content}\n${template.code}\n__component.render = render;
        const setup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = setup(props, context);
          globalThis.capturePlaceComponent(${JSON.stringify(args.path.split('/').at(-1))}, bindings);
          return bindings;
        }; export default __component;` };
    });
    builder.onLoad({ filter: /leafletAdapter\.ts$/ }, () => ({ contents: `export const createTripEditorMap = () => ({
      render() {}, dispose() {}, clearSearchPreview() {}, selectPlace() {}, selectSegment() {}, setSegmentPresentation() {}, setSegmentDraftPreview() {},
      setPlaceDraftPreview() {}, setSegmentRouteWorkState() {},
      getMapView: () => ({ center: { latitude: 10, longitude: 20 }, zoom: 5 }),
      startCoordinatePick: options => { globalThis.placeMapWork = options; return () => {}; } });
      export const hasAnyGeometry = () => true; export const hasSavedTripView = () => false;
      export const canFocusActiveEntity = () => true;` }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: `export const confirm = options => globalThis.placeConfirm(options);
      export const disposeConfirmDialogHost = () => {}; export const setConfirmDialogFocusFallback = () => {};` }));
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const actualApp = (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
const region = { id: 'region', tripId: 'trip', displayOrder: 1, name: 'Region', notesHtml: '', isShadow: false, capabilities: { canEdit: true, canAddChildren: true, canTargetForSearchAdd: true } };
const place = (overrides = {}) => {
  const value = { id: 'place', tripId: 'trip', regionId: 'region', name: 'Original', notesHtml: '', address: 'Original address',
    location: { latitude: 10, longitude: 20 }, iconName: 'marker', markerColor: 'bg-blue', displayOrder: 1,
    visitSummary: { placeId: 'place', visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
    capabilities: { canEdit: true, canDelete: true }, ...overrides };
  value.originalValues ??= { address: value.address, displayOrder: value.displayOrder, regionId: value.regionId, name: value.name, notesHtml: value.notesHtml || null, iconName: null, markerColor: null };
  if (overrides.iconName && !overrides.originalValues) value.originalValues.iconName = value.iconName;
  if (overrides.markerColor && !overrides.originalValues) value.originalValues.markerColor = value.markerColor;
  return value;
};
const envelope = data => ({ contract: 'bd2-1', success: true, data, affected: { metadata: null, regions: [], places: data ? [data] : [], areas: [],
  segments: [], tags: [], placeOrdersByRegionId: {}, areaOrdersByRegionId: {},
  regionOrder: null, segmentOrder: null, tagOrder: null, visitProgress: null, options: null },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
const saved = (data, coordinates = false) => {
  const result = envelope(data);
  if (coordinates) {
    result.affected.segmentOrder = [];
    result.affected.visitProgress = { totalPlaces: 1, visitedPlaces: 0, percentVisited: 0,
      placeSummariesByPlaceId: { [data.id]: data.visitSummary }, historyRows: [] };
  }
  return new Response(JSON.stringify(result));
};
const createdId = '10000000-0000-0000-0000-000000000001';
/** Complete creation response at the mounted production owner seam. */
const creationReply = (overrides = {}) => {
  const next = place({ id: createdId, name: 'New Place', displayOrder: 2, ...overrides });
  next.visitSummary = { ...next.visitSummary, placeId: next.id };
  next.capabilities = { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: true, canAddChildren: false, canTargetForSearchAdd: false };
  const result = envelope(next);
  result.contract = 'bd2-1';
  result.affected.visitProgress = { totalPlaces: 2, visitedPlaces: 0, percentVisited: 0,
    placeSummariesByPlaceId: { place: place().visitSummary, [next.id]: next.visitSummary }, historyRows: [] };
  result.affected.placeOrdersByRegionId.region = ['place', createdId];
  return result;
};
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const withApp = async action => {
  const previous = { window: globalThis.window, document: globalThis.document, fetch: globalThis.fetch, CSS: globalThis.CSS,
    placeConfirm: globalThis.placeConfirm, capturePlaceComponent: globalThis.capturePlaceComponent };
  const captured = {};
  globalThis.capturePlaceComponent = (name, bindings) => { captured[name] = proxyRefs(bindings); };
  globalThis.placeConfirm = async () => true;
  globalThis.CSS = { escape: value => value };
  globalThis.document = { getElementById: () => null, querySelector: () => null };
  globalThis.window = { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
    Sortable: { create: () => ({ destroy() {} }) } };
  const initial = { tripId: 'trip', metadata: { name: 'Trip', notesHtml: '', updatedAt: '2026-10-21T00:00:00Z' },
    regionsById: { region }, regionOrder: ['region'], areasById: {}, areaOrderByRegionId: {},
    placesById: { place: place() }, placeOrderByRegionId: { region: ['place'] }, segmentsById: {}, segmentOrder: [], tagsBySlug: {}, tagOrder: [],
    options: { transportModes: [], iconNames: ['marker', 'star'], markerColorClasses: ['bg-blue', 'bg-red'], areaDefaults: { fillHex: '#ff6600' } },
    permissions: { canEditPlaces: true, canEditRegions: true, canEditAreas: true }, visitProgress: {} };
  globalThis.fetch = async () => new Response(JSON.stringify(initial));
  // Real surface v-if transitions need host sibling identity so Vue can remove a fragment's exact range.
  const remove = node => {
    if (node?.parent) node.parent.children.splice(node.parent.children.indexOf(node), 1);
    if (node) node.parent = null;
  };
  const renderer = createRenderer({ createElement: tag => ({ tagName: tag.toUpperCase(), style: {}, children: [],
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {} }), createText: () => ({}), createComment: () => ({}),
    insert(node, parent, anchor) {
      remove(node);
      parent.children ??= [];
      const index = anchor ? parent.children.indexOf(anchor) : -1;
      parent.children.splice(index < 0 ? parent.children.length : index, 0, node);
      node.parent = parent;
    }, remove, setText() {}, setElementText() {}, patchProp() {}, parentNode: node => node?.parent,
    nextSibling: node => node?.parent?.children[node.parent.children.indexOf(node) + 1] ?? null });
  const app = renderer.createApp(actualApp, { config: { editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
  app.mount({});
  try {
    for (let tick = 0; tick < 20 && !captured['RegionManager.vue']; tick++) await new Promise(done => setImmediate(done));
    const parent = captured['App.vue'];
    const editor = captured['RegionManager.vue'];
    assert.ok(editor, 'actual production Place owner mounted through App and TripSidebar');
    parent.editorSurface.clearActiveTarget();
    const prepared = (apply, segments = []) => async (url, init) => {
      if (url.endsWith('/prepare')) {
        const request = JSON.parse(init.body);
        const order = url.includes('/places/order/');
        return new Response(JSON.stringify({ contract: 'bd2-1', operation: order ? 'order' : 'web-update', tripId: 'trip',
          targetId: order ? url.split('/regions/')[1].split('/')[0] : 'place', requiresReview: true, conditionToken: 'conditions',
          place: order ? null : { id: 'place', regionId: 'region', name: 'Original', notes: null, address: 'Original address', location: { latitude: 10, longitude: 20 }, iconName: null, markerColor: null, displayOrder: 1, resolvedFeatureName: null, resolvedFeatureType: null }, placesByRegion: {}, affectedSegmentIds: segments }));
      }
      return apply(url, init);
    };
    await action({ prepared, parent, editor, sidebar: captured['TripSidebar.vue'] });
  } finally {
    await nextTick();
    await nextTick();
    captured['App.vue']?.editorSurface.clearActiveTarget();
    app.unmount();
    await nextTick();
    Object.assign(globalThis, previous);
    delete globalThis.placeMapWork;
  }
};


export { withApp, place, envelope, deferred, saved, creationReply, createdId, region };
