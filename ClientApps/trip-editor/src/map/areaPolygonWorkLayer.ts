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
    options?.onChanged(polygon ? latLngsToPolygon(flattenPolygonLatLngs(polygon)) : null);
  };

  const stopHandlers = (): void => {
    drawHandler?.disable();
    editHandler?.disable();
    drawHandler = null;
    editHandler = null;
    map.off(drawCreatedEvent(), onDrawCreated);
    map.off(drawEditedEvent(), publish);
    map.off(drawEditVertexEvent(), publish);
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
    const initialPoints = polygonToLatLngs(workOptions.initialGeometry);
    if (initialPoints.length >= 3) {
      polygon = createPolygon(initialPoints, workOptions.fillHex);
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

const polygonToLatLngs = (geometry: GeoJsonPolygon | null): L.LatLng[] => {
  const exterior = geometry?.coordinates?.[0] ?? [];
  return exterior.slice(0, -1).map(([longitude, latitude]) => L.latLng(latitude, longitude));
};

const createPolygon = (points: L.LatLng[], fillHex: string): L.Polygon =>
  L.polygon(points, polygonStyle(fillHex));

const flattenPolygonLatLngs = (polygon: L.Polygon): L.LatLng[] => {
  const latLngs = polygon.getLatLngs();
  const exterior = latLngs[0] ?? [];
  return (Array.isArray(exterior[0]) ? exterior[0] : exterior) as L.LatLng[];
};

const latLngsToPolygon = (latLngs: L.LatLng[]): GeoJsonPolygon | null => {
  if (latLngs.length < 3) {
    return null;
  }

  const ring = latLngs.map(point => [point.lng, point.lat] as [number, number]);
  ring.push([latLngs[0].lng, latLngs[0].lat]);
  return { type: 'Polygon', coordinates: [ring] };
};

const drawPolygonHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { Draw: { Polygon: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).Draw.Polygon;

const editToolbarHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { EditToolbar: { Edit: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).EditToolbar.Edit;

const drawEvent = (name: 'CREATED' | 'EDITED' | 'EDITVERTEX'): string =>
  (L as unknown as { Draw: { Event: Record<string, string> } }).Draw.Event[name];

const drawCreatedEvent = (): string => drawEvent('CREATED');
const drawEditedEvent = (): string => drawEvent('EDITED');
const drawEditVertexEvent = (): string => drawEvent('EDITVERTEX');
