import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';
import { readFile } from 'node:fs/promises';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { createSSRApp, proxyRefs } from 'vue';
import { renderToString } from '@vue/server-renderer';

// Exercise the real adapter and temporary layer, substituting only Leaflet and unrelated map owners.
const paths = [];
const group = () => ({ owned: [], addTo() { return this; }, remove() {}, getLayers() { return this.owned; },
  clearLayers() { this.owned.forEach(path => { const index = paths.indexOf(path); if (index >= 0) paths.splice(index, 1); }); this.owned = []; } });
const panes = {};
const movements = {};
const map = { setView() { return this; }, on(event, callback) { movements[event] = callback; }, off() {}, remove() {},
  createPane(name) { return panes[name] = { style: {}, setAttribute() {}, remove() {} }; }, getPane(name) { return panes[name]; },
  fitBounds(bounds) { this.bounds = bounds.points; },
  attributionControl: { setPrefix() {}, getContainer() { return null; } } };
globalThis.window = {};
globalThis.previewLeaflet = { map: () => map, layerGroup: group, latLng: (lat, lng) => [lat, lng],
  latLngBounds: () => ({ points: [], extend(point) { this.points.push(point); return this; }, isValid() { return this.points.length > 0; } }),
  polyline: (coordinates, style) => ({ coordinates, style, options: style, attributes: {},
    addTo(owner) { paths.push(this); owner.owned.push(this); return this; },
    setStyle(next) { Object.assign(style, next); return this; }, on() { return this; }, off() {},
    bindTooltip() { return this; }, unbindTooltip() {},
    getElement() { return { setAttribute: (name, value) => { this.attributes[name] = value; } }; } }) };
const bundle = await build({ stdin: { contents: "export { createSegmentPresentationLayer } from './ClientApps/trip-editor/src/map/segmentPresentationLayer'; export { createTripEditorMap } from './ClientApps/trip-editor/src/map/leafletAdapter'; export { createSegmentRouteDraftPreviewLayer } from './ClientApps/trip-editor/src/map/segmentRouteDraftPreviewLayer';", resolveDir: process.cwd() },
  bundle: true, format: 'esm', platform: 'node', write: false, plugins: [{ name: 'map-boundaries', setup(builder) {
    builder.onResolve({ filter: /leaflet$/ }, () => ({ path: 'leaflet', namespace: 'mock' }));
    builder.onLoad({ filter: /.*/, namespace: 'mock' }, () => ({ contents: 'export default globalThis.previewLeaflet;' }));
    builder.onLoad({ filter: /\.css$/ }, () => ({ contents: '' }));
    builder.onLoad({ filter: /(?:areaPolygonWorkLayer|mapUtilitiesControl|placeDraftPreviewLayer|searchPreviewLayer|segmentRouteWorkLayer|tileRetryLayer)\.ts$/ }, async args => {
      const { readFile } = await import('node:fs/promises');
      const source = await readFile(args.path, 'utf8');
      const exports = [...source.matchAll(/export const (create\w+)/g)].map(match => match[1]);
      return { contents: exports.map(name => `export const ${name} = () => ({ addTo() { return this; }, isActive: () => false, dispose() {}, remove() {} });`).join('\n') };
    });
  } }] });
const { createTripEditorMap, createSegmentRouteDraftPreviewLayer, createSegmentPresentationLayer } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString('base64')}`);

test('adapter publishes proposal geometry and clears it without duplicating ordinary draft ownership', () => {
  const adapter = createTripEditorMap({}, '/controlled-tiles');
  const preview = { identity: 'segment-provider-proposal', kind: 'proposal', segmentId: 'segment',
    route: { type: 'LineString', coordinates: [[23, 37], [24, 38]] } };
  adapter.setSegmentDraftPreview({}, preview);
  assert.equal(paths.length, 2);
  assert.deepEqual(paths[0].coordinates, [[37, 23], [38, 24]]);
  assert.equal(paths[0].style.dashArray, '8 6');
  assert.equal(adapter.focusActiveEntity({}, { kind: 'segment', entityId: 'segment' }), 'moved');
  assert.deepEqual(map.bounds, [[37, 23], [38, 24]]);
  assert.equal(adapter.fitAllGeometry({ regionsById: {}, placesById: {}, areasById: {}, segmentsById: {} }), 'moved');
  assert.deepEqual(map.bounds, [[37, 23], [38, 24]]);
  adapter.setSegmentDraftPreview({}, { ...preview, kind: undefined });
  assert.equal(paths.length, 0);
  adapter.setSegmentDraftPreview({}, preview);
  adapter.setSegmentDraftPreview({}, null);
  assert.equal(paths.length, 0);
  adapter.setSegmentDraftPreview({}, preview);
  adapter.dispose();
  assert.equal(paths.length, 0);
});

test('temporary route yields to map work and hidden Segments, with no fallback or stale bounds', () => {
  const layer = createSegmentRouteDraftPreviewLayer(map, () => {});
  layer.set({ kind: 'proposal', segmentId: 'segment', route: { coordinates: [[23, 37], [24, 38]] } });
  layer.render({}, new Set(), false);
  assert.equal(paths.length, 2);
  for (const [hidden, work] of [[new Set(), true], [new Set(['segment']), false]]) {
    layer.render({}, hidden, work);
    assert.equal(paths.length, 0);
    assert.equal(layer.extendBounds(globalThis.previewLeaflet.latLngBounds()).isValid(), false);
  }
  layer.set({ kind: 'proposal', segmentId: 'segment', route: null });
  layer.render({}, new Set(), false);
  assert.equal(paths.length, 0);
  layer.dispose();
});

// Exercise both production owners with overlapping and nonoverlapping ordinary geometry.
test('proposal casing and whole-route emphasis retain ownership through redraw and cleanup', () => {
  const ordinary = createSegmentPresentationLayer(map, () => true);
  const proposal = createSegmentRouteDraftPreviewLayer(map, ordinary.setProposalEmphasis);
  const coordinates = [[23, 37], [24, 38], [25, 39]];
  const presentations = ['current', 'other'].map(id => ({ segmentId: id, key: { kind: 'persisted', id },
    coordinates, source: 'S', hasCustomRoute: true, directionTrustworthy: false }));
  ordinary.render(presentations, null);
  const line = id => paths.find(path => path.attributes['data-segment-id'] === id);
  const normal = { ...line('current').style };
  const other = { ...line('other').style };
  proposal.set({ kind: 'proposal', identity: 'pending', segmentId: 'current', route: { coordinates: coordinates.slice(0, 2) } });
  proposal.render({}, new Set(), false);
  assert.deepEqual(line('current').coordinates, [[37, 23], [38, 24], [39, 25]]);
  assert.deepEqual(line('current').style, { ...normal, opacity: 0.22 });
  assert.deepEqual(line('other').style, other);
  const preview = line('pending');
  assert.deepEqual(preview.style, { pane: 'segment-route-proposal', color: '#a21caf', dashArray: '8 6', opacity: 1, interactive: false, weight: 4 });
  const casing = paths[paths.indexOf(preview) - 1];
  assert.deepEqual(casing.style, { color: '#ffffff', dashArray: '8 6', opacity: 1, interactive: false, weight: 8, pane: 'segment-route-proposal' });
  assert.deepEqual(casing.coordinates, preview.coordinates);
  assert.equal(panes['segment-route-proposal'].style.pointerEvents, 'none');
  assert.ok(Number(panes['segment-route-proposal'].style.zIndex) > 400);
  assert.ok(Number(panes['segment-route-proposal'].style.zIndex) < Number(panes['segment-route-role'].style.zIndex));
  assert.ok(Number(panes['segment-route-proposal'].style.zIndex) < 600);
  movements['zoomend moveend']();
  assert.deepEqual(line('current').style, { ...normal, opacity: 0.22 });
  assert.deepEqual(line('other').style, other);
  assert.equal(paths.filter(path => path.attributes['data-segment-hit-owner']).length, 2);
  for (const [hidden, work] of [[new Set(['current']), false], [new Set(), true]]) {
    proposal.render({}, hidden, work);
    assert.deepEqual(line('current').style, normal);
    assert.equal(line('pending'), undefined);
    proposal.render({}, new Set(), false);
  }
  proposal.set(null);
  proposal.render({}, new Set(), false);
  assert.deepEqual(line('current').style, normal);
  proposal.set({ kind: 'proposal', identity: 'pending', segmentId: 'current', route: { coordinates } });
  proposal.render({}, new Set(), false);
  proposal.dispose();
  assert.deepEqual(line('current').style, normal);
  ordinary.dispose();
});

// Render the production proposal template with real proposal state and the existing SFC compiler.
async function loadProposalComponent() {
  const filename = 'ClientApps/trip-editor/src/components/SegmentRouteProposal.vue';
  const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
  const script = compileScript(descriptor, { id: 'proposal-test' });
  const template = compileTemplate({ source: descriptor.template.content, filename, id: 'proposal-test',
    compilerOptions: { bindingMetadata: script.bindings } });
  const compiled = await build({ stdin: { contents: `${script.content}\n${template.code}`, loader: 'ts',
    resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
    external: ['vue'], plugins: [{ name: 'confirmation-boundary', setup(builder) {
      builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = async () => false;' }));
    } }] });
  // Resolve Vue against this repository rather than a data URL.
  const code = compiled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
  return import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
}

test('one Generate with existing geometry requests a preview without confirmation', async () => {
  const { default: component, render } = await loadProposalComponent();
  let requests = 0;
  const previousFetch = globalThis.fetch;
  globalThis.fetch = async () => {
    requests++;
    return { ok: true, json: async () => ({ segmentId: 'segment', geometry: [
      { longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }] }) };
  };
  let bindings;
  const props = { segment: { id: 'segment', mode: 'Fish', externalRouting: { available: true, modes: [] } },
    draftMode: 'Fish', draftHasRoute: true, draftContextKey: 'context' };
  try {
    const html = await renderToString(createSSRApp({ setup() {
      bindings = proxyRefs(component.setup(props, { expose() {}, emit() {} }));
      bindings.selectedProviderMode = 'drive';
      return () => render({}, [], props, bindings, {}, {});
    } }));
    const pending = bindings.generate();
    // A confirmation would suspend generation before the production API boundary.
    assert.equal(requests, 1);
    await pending;
    assert.match(html, /Generate routed path/);
    assert.ok(bindings.state.proposal);
    assert.equal(props.draftMode, 'Fish');
  } finally { globalThis.fetch = previousFetch; }
});

test('pending template labels present, absent and zero estimates and explains Manual precedence', async () => {
  const { default: component, render } = await loadProposalComponent();
  const props = { segment: { id: 'segment', mode: 'walk', externalRouting: { available: true, modes: [] } },
    draftMode: 'walk', draftContextKey: 'context', manualDurationOverride: true };
  for (const [distance, duration, expected] of [[1250, 360, ['1.25 km', '6 minutes']],
    [null, null, ['Unavailable', 'Unavailable']], [undefined, undefined, ['Unavailable', 'Unavailable']],
    [0, 0, ['0 km', '0 minutes']]]) {
    let bindings;
    const app = createSSRApp({ setup() {
      bindings = proxyRefs(component.setup(props, { expose() {}, emit() {} }));
      const request = bindings.proposalStore.begin('segment', 'walk', new AbortController());
      bindings.proposalStore.complete('segment', request, { segmentId: 'segment',
        geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], distanceMetres: distance, durationSeconds: duration });
      return () => render({}, [], props, bindings, {}, {});
    } });
    const html = await renderToString(app);
    assert.match(html, /Proposed distance/);
    assert.match(html, /Estimated travel time/);
    for (const value of expected) assert.ok(html.includes(`<strong>${value}</strong>`), html);
    assert.match(html, /Save keeps your manual duration/);
    bindings.proposalStore.discard('segment');
    const discarded = await renderToString(createSSRApp({ render: () => render({}, [], props, bindings, {}, {}) }));
    assert.equal(discarded.includes('Proposed distance'), false);
  }
});
