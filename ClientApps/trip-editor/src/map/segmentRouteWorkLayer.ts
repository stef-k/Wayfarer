import L, { type LeafletMouseEvent, type Map as LeafletMap } from 'leaflet';
import { ensureLeafletDraw } from './leafletDrawPlugin';
import {
  cloneSegmentRouteWorkState,
  insertAnonymousNode,
  moveAnonymousNode,
  workStateGeometry,
  type SegmentRouteAnchorNode,
  type SegmentRouteWorkState
} from '../components/segmentRouteWorkState';

export interface SegmentRouteWorkOptions {
  identity: string;
  initialState: SegmentRouteWorkState;
  onChanged: (state: SegmentRouteWorkState) => void;
}

/** Owns replace-only route-work lines, fixed role indicators, anonymous handles, and listeners. */
export const createSegmentRouteWorkLayer = (map: LeafletMap): {
  dispose: () => void;
  isActive: () => boolean;
  setState: (state: SegmentRouteWorkState) => void;
  start: (options: SegmentRouteWorkOptions) => () => void;
  stop: () => void;
} => {
  ensureLeafletDraw();
  const group = L.featureGroup().addTo(map);
  let options: SegmentRouteWorkOptions | null = null;
  let state: SegmentRouteWorkState | null = null;
  let drawHandler: { disable: () => void; enable: () => void } | null = null;

  const publish = (): void => {
    if (state) options?.onChanged(cloneSegmentRouteWorkState(state));
  };

  const render = (): void => {
    stopDraw();
    group.clearLayers();
    if (!state || !options) return;

    if (state.nodes.length < 2) {
      state.nodes.filter(node => node.kind === 'anchor').forEach(node => {
        L.marker([node.coordinate[1], node.coordinate[0]], { interactive: false, icon: anchorIcon(node) }).addTo(group);
      });
      startDraw();
      return;
    }

    const geometry = workStateGeometry(state);
    const line = L.polyline(geometry.coordinates.map(([longitude, latitude]) => [latitude, longitude]), routeStyle())
      .on('click', insertAtNearestInterval)
      .addTo(group);
    const element = line.getElement() as SVGElement | null;
    if (element) {
      element.dataset.segmentId = options.identity;
      element.dataset.routeOwner = 'work';
      element.dataset.routeKind = state.cleared || state.origin === 'fallback' && !state.changedCustom ? 'fallback' : 'custom';
    }

    state.nodes.forEach((node, index) => {
      if (node.kind === 'anchor') {
        L.marker([node.coordinate[1], node.coordinate[0]], {
          interactive: false,
          icon: anchorIcon(node)
        }).addTo(group);
        return;
      }

      const marker = L.marker([node.coordinate[1], node.coordinate[0]], {
        draggable: true,
        icon: anonymousIcon(),
        title: `Route point ${state.nodes.slice(0, index + 1).filter(candidate => candidate.kind === 'anonymous').length}`
      });
      marker.addTo(group);
      marker.getElement()?.setAttribute('data-route-point-key', node.key);
      marker.on('drag', () => {
        const point = marker.getLatLng();
        if (state && moveAnonymousNode(state, node.key, [point.lng, point.lat])) publish();
      });
      marker.on('dragend', () => {
        const point = marker.getLatLng();
        if (state && moveAnonymousNode(state, node.key, [point.lng, point.lat])) {
          publish();
          render();
        }
      });
    });
  };

  const setState = (next: SegmentRouteWorkState): void => {
    state = cloneSegmentRouteWorkState(next);
    render();
  };

  const start = (workOptions: SegmentRouteWorkOptions): (() => void) => {
    stop();
    options = workOptions;
    state = cloneSegmentRouteWorkState(workOptions.initialState);
    render();
    const bounds = group.getBounds();
    if (bounds.isValid()) map.fitBounds(bounds, { padding: [32, 32], maxZoom: 14 });
    return stop;
  };

  const stop = (): void => {
    stopDraw();
    group.clearLayers();
    options = null;
    state = null;
  };

  function insertAtNearestInterval(event: LeafletMouseEvent): void {
    if (!state || state.nodes.length < 2) return;
    const click = map.latLngToLayerPoint(event.latlng);
    let nearestIndex = 0;
    let nearestDistance = Number.POSITIVE_INFINITY;
    for (let index = 0; index < state.nodes.length - 1; index += 1) {
      const left = map.latLngToLayerPoint(L.latLng(state.nodes[index].coordinate[1], state.nodes[index].coordinate[0]));
      const right = map.latLngToLayerPoint(L.latLng(state.nodes[index + 1].coordinate[1], state.nodes[index + 1].coordinate[0]));
      const distance = L.LineUtil.pointToSegmentDistance(click, left, right);
      if (distance < nearestDistance) {
        nearestDistance = distance;
        nearestIndex = index;
      }
    }
    if (insertAnonymousNode(state, state.nodes[nearestIndex].key)) {
      publish();
      render();
    }
  }

  /** Retains the legacy map-draw entry only when W has no complete LineString yet. */
  function startDraw(): void {
    drawHandler = new (drawPolylineHandler())(map, { repeatMode: false, shapeOptions: routeStyle() }) as { disable: () => void; enable: () => void };
    map.on(drawCreatedEvent(), onDrawCreated);
    drawHandler.enable();
  }

  function stopDraw(): void {
    drawHandler?.disable();
    drawHandler = null;
    map.off(drawCreatedEvent(), onDrawCreated);
  }

  function onDrawCreated(event: { layer: L.Layer }): void {
    if (!state || !(event.layer instanceof L.Polyline) || event.layer instanceof L.Polygon) return;
    const coordinates = (event.layer.getLatLngs() as L.LatLng[]).map(point => [point.lng, point.lat] as [number, number]);
    const from = state.nodes.find(node => node.kind === 'anchor' && node.role === 'from');
    const to = state.nodes.find(node => node.kind === 'anchor' && node.role === 'to');
    let anonymousId = state.nextAnonymousId;
    state.nodes = [
      ...(from ? [from] : []),
      ...coordinates.map(coordinate => ({ kind: 'anonymous' as const, key: `anonymous:${anonymousId++}`, coordinate })),
      ...(to ? [to] : [])
    ];
    state.nextAnonymousId = anonymousId;
    state.changedCustom = true;
    state.cleared = false;
    publish();
    render();
  }

  return { dispose: stop, isActive: () => options !== null, setState, start, stop };
};

const routeStyle = (): L.PathOptions => ({ color: '#f97316', dashArray: '2 7', opacity: 0.9, weight: 4 });
/** Renders one pointer-draggable anonymous handle without duplicating saved-Place markers. */
const anonymousIcon = (): L.DivIcon => L.divIcon({
  className: 'segment-route-work-handle',
  html: '<span aria-hidden="true"></span>',
  iconSize: [18, 18],
  iconAnchor: [9, 9],
  tooltipAnchor: [0, -12]
});

/** Names fixed roles by position without turning them into editable marker handles. */
function anchorIcon(node: SegmentRouteAnchorNode): L.DivIcon {
  return L.divIcon({
    className: 'segment-route-work-anchor',
    html: `<span aria-hidden="true">${node.role === 'from' ? 'S' : node.role === 'to' ? 'E' : 'V'}</span>`,
    iconSize: [24, 24],
    iconAnchor: [12, 12],
    tooltipAnchor: [0, -14]
  });
}

const drawPolylineHandler = (): new (map: LeafletMap, options: Record<string, unknown>) => unknown =>
  (L as unknown as { Draw: { Polyline: new (map: LeafletMap, options: Record<string, unknown>) => unknown } }).Draw.Polyline;

const drawCreatedEvent = (): string =>
  (L as unknown as { Draw: { Event: Record<string, string> } }).Draw.Event.CREATED;
