import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorSegment, EditorTripState, Guid } from '../types';

export interface SegmentDraftRoutePreview {
  fromPlaceId: Guid | null;
  identity: string;
  route: EditorSegment['route'];
  segmentId: Guid | null;
  toPlaceId: Guid | null;
}

/// Owns the single non-editable segment draft route rendered outside map-work.
export const createSegmentRouteDraftPreviewLayer = (map: LeafletMap): {
  dispose: () => void;
  render: (state: EditorTripState, hiddenSegmentIds: ReadonlySet<Guid>, workActive: boolean) => void;
  segmentId: () => Guid | null;
  set: (preview: SegmentDraftRoutePreview | null) => void;
} => {
  const layers = L.layerGroup().addTo(map);
  let activePreview: SegmentDraftRoutePreview | null = null;

  const render = (state: EditorTripState, hiddenSegmentIds: ReadonlySet<Guid>, workActive: boolean): void => {
    layers.clearLayers();
    const preview = activePreview;
    if (!preview || workActive || (preview.segmentId !== null && hiddenSegmentIds.has(preview.segmentId))) {
      return;
    }

    const coordinates = preview.route?.coordinates ?? fallbackCoordinates(preview, state);
    if (!coordinates || coordinates.length < 2) {
      return;
    }

    const polyline = L.polyline(coordinates.map(([longitude, latitude]) => [latitude, longitude]), {
      color: '#38bdf8',
      dashArray: '8 6',
      opacity: 0.9,
      weight: 4
    }).addTo(layers);
    const element = polyline.getElement();
    if (element) {
      element.setAttribute('data-segment-id', preview.identity);
      element.setAttribute('data-route-owner', 'draft');
      element.setAttribute('data-route-kind', preview.route === null ? 'fallback' : 'custom');
    }
  };

  return {
    dispose: () => layers.clearLayers(),
    render,
    segmentId: () => activePreview?.segmentId ?? null,
    set: preview => { activePreview = preview; }
  };
};

const fallbackCoordinates = (preview: SegmentDraftRoutePreview, state: EditorTripState): Array<[number, number]> | null => {
  const from = preview.fromPlaceId ? state.placesById[preview.fromPlaceId]?.location : null;
  const to = preview.toPlaceId ? state.placesById[preview.toPlaceId]?.location : null;
  return from && to ? [[from.longitude, from.latitude], [to.longitude, to.latitude]] : null;
};
