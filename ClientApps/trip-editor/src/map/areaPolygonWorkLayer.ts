import L, { type Map as LeafletMap } from 'leaflet';
import { ensureLeafletDraw } from './leafletDrawPlugin';
import type { GeoJsonPolygon } from '../types';

export interface AreaPolygonWorkOptions {
  initialGeometry: GeoJsonPolygon | null;
  fillHex: string;
  onChanged: (geometry: GeoJsonPolygon | null) => void;
}

type DrawnLayerEvent = { layer: L.Layer };
type LeafletDrawHandler = { disable: () => void; enable: () => void; enabled?: () => boolean };
type LatLngRing = L.LatLng[];

/// Owns the single temporary Leaflet.Draw polygon used by Trip Editor area map-work.
export const createAreaPolygonWorkLayer = (map: LeafletMap): {
  dispose: () => void;
  start: (options: AreaPolygonWorkOptions) => () => void;
  stop: () => void;
} => {
  ensureLeafletDraw();
  const featureGroup = L.featureGroup().addTo(map);
  let drawHandler: LeafletDrawHandler | null = null;
  let editHandler: LeafletDrawHandler | null = null;
  let polygon: L.Polygon | null = null;
  let options: AreaPolygonWorkOptions | null = null;

  const publish = (): void => {
    options?.onChanged(polygon ? latLngRingsToPolygon(polygonLatLngRings(polygon)) : null);
  };

  const stopHandlers = (): void => {
    drawHandler?.disable();
    editHandler?.disable();
    drawHandler = null;
    editHandler = null;
    map.off(drawCreatedEvent(), onDrawCreated);
    map.off(drawEditedEvent(), publish);
    map.off(drawEditVertexEvent(), publish);
    map.off(drawEditMoveEvent(), publish);
    polygon?.off('edit drag dragend move', publish);
  };

  const stop = (): void => {
    stopHandlers();
    featureGroup.clearLayers();
    polygon = null;
    options = null;
  };

  const start = (workOptions: AreaPolygonWorkOptions): (() => void) => {
    stop();
    options = workOptions;
    const initialRings = polygonToLatLngRings(workOptions.initialGeometry);
    if (initialRings[0]?.length >= 3) {
      polygon = createPolygon(initialRings, workOptions.fillHex);
      featureGroup.addLayer(polygon);
      startEditMode();
      if (polygon.getBounds().isValid()) {
        map.fitBounds(polygon.getBounds(), { padding: [32, 32], maxZoom: 14 });
      }
    } else {
      startDrawMode();
    }

    publish();
    return stop;
  };

  const startDrawMode = (): void => {
    drawHandler = new (drawPolygonHandler())(map, {
      allowIntersection: false,
      repeatMode: false,
      shapeOptions: polygonStyle(options?.fillHex ?? '#ff6600'),
      showArea: false
    }) as LeafletDrawHandler;
    map.on(drawCreatedEvent(), onDrawCreated);
    drawHandler.enable();
  };

  const startEditMode = (): void => {
    editHandler = new (editToolbarHandler())(map, {
      featureGroup,
      poly: { allowIntersection: false },
      selectedPathOptions: { maintainColor: true }
    }) as LeafletDrawHandler;
    map.on(drawEditedEvent(), publish);
    map.on(drawEditVertexEvent(), publish);
    map.on(drawEditMoveEvent(), publish);
    polygon?.on('edit drag dragend move', publish);
    editHandler.enable();
  };

  function onDrawCreated(event: DrawnLayerEvent): void {
    if (!(event.layer instanceof L.Polygon)) {
      return;
    }

    featureGroup.clearLayers();
    polygon = event.layer;
    polygon.setStyle(polygonStyle(options?.fillHex ?? '#ff6600'));
    featureGroup.addLayer(polygon);
    drawHandler?.disable();
    drawHandler = null;
    startEditMode();
    publish();
  }

  return {
    dispose: stop,
    start,
    stop
  };
};

const polygonStyle = (fillHex: string): L.PathOptions => ({
  color: fillHex,
  fillColor: fillHex,
  fillOpacity: 0.25,
  weight: 2
});

const polygonToLatLngRings = (geometry: GeoJsonPolygon | null): LatLngRing[] =>
  (geometry?.coordinates ?? [])
    .map(ring => ring.slice(0, -1).map(([longitude, latitude]) => L.latLng(latitude, longitude)))
    .filter(ring => ring.length >= 3);

const createPolygon = (rings: LatLngRing[], fillHex: string): L.Polygon =>
  L.polygon(rings, polygonStyle(fillHex));

const polygonLatLngRings = (polygon: L.Polygon): LatLngRing[] => {
  const latLngs = polygon.getLatLngs();
  if (latLngs.length === 0) {
    return [];
  }

  if (latLngs[0] instanceof L.LatLng) {
    return [latLngs as LatLngRing];
  }

  const rings = latLngs as LatLngRing[] | LatLngRing[][];
  return Array.isArray(rings[0]?.[0]) ? (rings[0] as LatLngRing[]) : rings as LatLngRing[];
};

const latLngRingsToPolygon = (rings: LatLngRing[]): GeoJsonPolygon | null => {
  if (!rings[0] || rings[0].length < 3) {
    return null;
  }

  const coordinates = rings
    .filter(ring => ring.length >= 3)
    .map(ring => {
      const coordinatesRing = ring.map(point => [point.lng, point.lat] as [number, number]);
      coordinatesRing.push([ring[0].lng, ring[0].lat]);
      return coordinatesRing;
    });
  return { type: 'Polygon', coordinates };
};

const drawPolygonHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { Draw: { Polygon: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).Draw.Polygon;

const editToolbarHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { EditToolbar: { Edit: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).EditToolbar.Edit;

const drawEvent = (name: 'CREATED' | 'EDITED' | 'EDITMOVE' | 'EDITVERTEX'): string =>
  (L as unknown as { Draw: { Event: Record<string, string> } }).Draw.Event[name];

const drawCreatedEvent = (): string => drawEvent('CREATED');
const drawEditedEvent = (): string => drawEvent('EDITED');
const drawEditMoveEvent = (): string => drawEvent('EDITMOVE');
const drawEditVertexEvent = (): string => drawEvent('EDITVERTEX');
