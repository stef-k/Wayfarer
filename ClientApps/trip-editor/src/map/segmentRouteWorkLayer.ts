import L, { type Map as LeafletMap } from 'leaflet';
import { ensureLeafletDraw } from './leafletDrawPlugin';
import type { GeoJsonLineString } from '../types';

export interface SegmentRouteWorkOptions {
  initialRoute: GeoJsonLineString | null;
  onChanged: (route: GeoJsonLineString | null) => void;
}

type DrawnLayerEvent = { layer: L.Layer };
type LeafletDrawHandler = { disable: () => void; enable: () => void };
type LatLngRoute = L.LatLng[];

/// Owns the single temporary Leaflet.Draw polyline used by Trip Editor segment route map-work.
export const createSegmentRouteWorkLayer = (map: LeafletMap): {
  dispose: () => void;
  setRoute: (route: GeoJsonLineString | null) => void;
  start: (options: SegmentRouteWorkOptions) => () => void;
  stop: () => void;
} => {
  ensureLeafletDraw();
  const featureGroup = L.featureGroup().addTo(map);
  let drawHandler: LeafletDrawHandler | null = null;
  let editHandler: LeafletDrawHandler | null = null;
  let polyline: L.Polyline | null = null;
  let options: SegmentRouteWorkOptions | null = null;

  const publish = (): void => {
    options?.onChanged(polyline ? latLngsToLineString(routeLatLngs(polyline)) : null);
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
    polyline = null;
    options = null;
  };

  const setRoute = (route: GeoJsonLineString | null): void => {
    featureGroup.clearLayers();
    polyline = routeToLatLngs(route).length >= 2 ? createPolyline(routeToLatLngs(route)) : null;
    if (polyline) {
      featureGroup.addLayer(polyline);
      startEditMode();
    } else {
      startDrawMode();
    }

    publish();
  };

  const start = (workOptions: SegmentRouteWorkOptions): (() => void) => {
    stop();
    options = workOptions;
    setRoute(workOptions.initialRoute);
    if (polyline?.getBounds().isValid()) {
      map.fitBounds(polyline.getBounds(), { padding: [32, 32], maxZoom: 14 });
    }

    return stop;
  };

  const startDrawMode = (): void => {
    stopHandlers();
    drawHandler = new (drawPolylineHandler())(map, {
      repeatMode: false,
      shapeOptions: routeStyle()
    }) as LeafletDrawHandler;
    map.on(drawCreatedEvent(), onDrawCreated);
    drawHandler.enable();
  };

  const startEditMode = (): void => {
    stopHandlers();
    editHandler = new (editToolbarHandler())(map, {
      featureGroup,
      selectedPathOptions: { maintainColor: true }
    }) as LeafletDrawHandler;
    map.on(drawEditedEvent(), publish);
    map.on(drawEditVertexEvent(), publish);
    editHandler.enable();
  };

  function onDrawCreated(event: DrawnLayerEvent): void {
    if (!(event.layer instanceof L.Polyline) || event.layer instanceof L.Polygon) {
      return;
    }

    featureGroup.clearLayers();
    polyline = event.layer;
    polyline.setStyle(routeStyle());
    featureGroup.addLayer(polyline);
    startEditMode();
    publish();
  }

  return {
    dispose: stop,
    setRoute,
    start,
    stop
  };
};

const routeStyle = (): L.PathOptions => ({
  color: '#f97316',
  opacity: 0.9,
  weight: 4
});

const routeToLatLngs = (route: GeoJsonLineString | null): LatLngRoute =>
  (route?.coordinates ?? []).map(([longitude, latitude]) => L.latLng(latitude, longitude));

const createPolyline = (latLngs: LatLngRoute): L.Polyline =>
  L.polyline(latLngs, routeStyle());

const routeLatLngs = (polyline: L.Polyline): LatLngRoute => {
  const latLngs = polyline.getLatLngs();
  return latLngs.filter(point => point instanceof L.LatLng) as LatLngRoute;
};

const latLngsToLineString = (latLngs: LatLngRoute): GeoJsonLineString | null =>
  latLngs.length >= 2
    ? { type: 'LineString', coordinates: latLngs.map(point => [point.lng, point.lat] as [number, number]) }
    : null;

const drawPolylineHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { Draw: { Polyline: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).Draw.Polyline;

const editToolbarHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { EditToolbar: { Edit: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).EditToolbar.Edit;

const drawEvent = (name: 'CREATED' | 'EDITED' | 'EDITVERTEX'): string =>
  (L as unknown as { Draw: { Event: Record<string, string> } }).Draw.Event[name];

const drawCreatedEvent = (): string => drawEvent('CREATED');
const drawEditedEvent = (): string => drawEvent('EDITED');
const drawEditVertexEvent = (): string => drawEvent('EDITVERTEX');
