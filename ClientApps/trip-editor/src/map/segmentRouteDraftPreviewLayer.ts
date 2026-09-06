import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorSegment, EditorTripState, Guid } from '../types';
import type { EditorTarget } from '../composables/useEditorSurface';

export interface SegmentDraftRoutePreview {
  fromPlaceId: Guid | null;
  identity: string;
  /** Only provider proposals supplement the unified saved/draft/work route owner. */
  kind?: 'proposal';
  route: EditorSegment['route'];
  segmentId: Guid | null;
  toPlaceId: Guid | null;
}

/// Owns only the temporary provider proposal, never an ordinary draft or map-work route.
export const createSegmentRouteDraftPreviewLayer = (map: LeafletMap): {
  dispose: () => void;
  extendBounds: (bounds: L.LatLngBounds) => L.LatLngBounds;
  focus: (target: EditorTarget | null) => 'moved' | null;
  render: (state: EditorTripState, hiddenSegmentIds: ReadonlySet<Guid>, workActive: boolean) => void;
  segmentId: () => Guid | null;
  set: (preview: SegmentDraftRoutePreview | null) => void;
} => {
  const layers = L.layerGroup().addTo(map);
  let activePreview: SegmentDraftRoutePreview | null = null;
  let visibleCoordinates: Array<[number, number]> = [];
  const extendBounds = (bounds: L.LatLngBounds): L.LatLngBounds => {
    visibleCoordinates.forEach(([longitude, latitude]) => bounds.extend([latitude, longitude]));
    return bounds;
  };

  const render = (state: EditorTripState, hiddenSegmentIds: ReadonlySet<Guid>, workActive: boolean): void => {
    layers.clearLayers();
    visibleCoordinates = [];
    const preview = activePreview;
    if (!preview || workActive || (preview.segmentId !== null && hiddenSegmentIds.has(preview.segmentId))) {
      return;
    }

    const coordinates = preview.route?.coordinates;
    if (!coordinates || coordinates.length < 2) {
      return;
    }
    visibleCoordinates = coordinates;

    const polyline = L.polyline(coordinates.map(([longitude, latitude]) => [latitude, longitude]), {
      color: '#38bdf8',
      dashArray: '8 6',
      opacity: 0.9,
      interactive: false,
      weight: 4
    }).addTo(layers);
    const element = polyline.getElement();
    if (element) {
      element.setAttribute('data-segment-id', preview.identity);
      element.setAttribute('data-route-owner', 'proposal');
      element.setAttribute('data-route-kind', 'custom');
    }
  };

  return {
    dispose: () => { activePreview = null; visibleCoordinates = []; layers.clearLayers(); },
    extendBounds,
    focus: target => {
      if (target?.kind !== 'segment' || target.entityId !== activePreview?.segmentId || visibleCoordinates.length < 2) return null;
      map.fitBounds(extendBounds(L.latLngBounds([])), { padding: [32, 32], maxZoom: 12 });
      return 'moved';
    },
    render,
    segmentId: () => activePreview?.segmentId ?? null,
    set: preview => { activePreview = preview?.kind === 'proposal' ? preview : null; }
  };
};
