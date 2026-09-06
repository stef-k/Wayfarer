import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

// Exercise the real adapter and temporary layer, substituting only Leaflet and unrelated map owners.
const paths = [];
const group = () => ({ addTo() { return this; }, clearLayers() { paths.length = 0; } });
const map = { setView() { return this; }, on() {}, off() {}, remove() {},
  attributionControl: { setPrefix() {}, getContainer() { return null; } } };
globalThis.window = {};
globalThis.previewLeaflet = { map: () => map, layerGroup: group,
  polyline: (coordinates, style) => ({ addTo() { paths.push({ coordinates, style }); return this; },
    getElement: () => ({ setAttribute() {} }) }) };
const bundle = await build({ entryPoints: ['ClientApps/trip-editor/src/map/leafletAdapter.ts'],
  bundle: true, format: 'esm', platform: 'node', write: false, plugins: [{ name: 'map-boundaries', setup(builder) {
    builder.onResolve({ filter: /leaflet$/ }, () => ({ path: 'leaflet', namespace: 'mock' }));
    builder.onLoad({ filter: /.*/, namespace: 'mock' }, () => ({ contents: 'export default globalThis.previewLeaflet;' }));
    builder.onLoad({ filter: /\.css$/ }, () => ({ contents: '' }));
    builder.onLoad({ filter: /(?:areaPolygonWorkLayer|mapUtilitiesControl|placeDraftPreviewLayer|searchPreviewLayer|segmentRouteWorkLayer|segmentPresentationLayer|tileRetryLayer)\.ts$/ }, async args => {
      const { readFile } = await import('node:fs/promises');
      const source = await readFile(args.path, 'utf8');
      const exports = [...source.matchAll(/export const (create\w+)/g)].map(match => match[1]);
      return { contents: exports.map(name => `export const ${name} = () => ({ addTo() { return this; }, isActive: () => false, dispose() {}, remove() {} });`).join('\n') };
    });
  } }] });
const { createTripEditorMap } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString('base64')}`);

test('adapter publishes proposal geometry and clears it without duplicating ordinary draft ownership', () => {
  const adapter = createTripEditorMap({}, '/controlled-tiles');
  const preview = { identity: 'segment-provider-proposal', kind: 'proposal', segmentId: 'segment',
    route: { type: 'LineString', coordinates: [[23, 37], [24, 38]] } };
  adapter.setSegmentDraftPreview({}, preview);
  assert.equal(paths.length, 1);
  assert.deepEqual(paths[0].coordinates, [[37, 23], [38, 24]]);
  assert.equal(paths[0].style.dashArray, '8 6');
  adapter.setSegmentDraftPreview({}, { ...preview, kind: undefined });
  assert.equal(paths.length, 0);
  adapter.setSegmentDraftPreview({}, preview);
  adapter.setSegmentDraftPreview({}, null);
  assert.equal(paths.length, 0);
  adapter.setSegmentDraftPreview({}, preview);
  adapter.dispose();
  assert.equal(paths.length, 0);
});
