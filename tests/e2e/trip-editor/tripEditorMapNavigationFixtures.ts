import { test } from '@playwright/test';

/** Synthetic read models vary navigation geometry without changing the shared runbook Trip. */
type MutableEditorState = Record<string, any>;

export function clearNavigationGeometry(state: MutableEditorState): void {
  Object.values(state.regionsById).forEach((region: any) => {
    region.center = null;
  });
  Object.values(state.placesById).forEach((place: any) => {
    place.location = null;
  });
  Object.values(state.segmentsById).forEach((segment: any) => {
    segment.route = null;
  });
  state.areasById = {};
  state.areaOrderByRegionId = Object.fromEntries(Object.keys(state.areaOrderByRegionId ?? {}).map(regionId => [regionId, []]));
}

export function prepareNavigationFocusState(state: MutableEditorState): { placeName: string; regionName: string } {
  clearNavigationGeometry(state);
  state.metadata.center = null;
  state.metadata.zoom = null;

  const region = normalRegion(state);
  if (!region) {
    throw new Error('Configured Trip Editor fixture must contain a normal region for map navigation coverage.');
  }

  addAreaGeometry(state, region.id, '00000000-0000-0000-0000-000000260001', 'PW navigation area');

  const placeId = ensurePlace(state, region.id, '00000000-0000-0000-0000-000000260002', 'PW navigation place');
  state.placesById[placeId].location = null;
  return { placeName: state.placesById[placeId].name, regionName: region.name };
}

export function preparePlaceLocationFocusState(state: MutableEditorState): { placeName: string; regionName: string } {
  clearNavigationGeometry(state);
  state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
  state.metadata.zoom = 4;

  const region = normalRegion(state);
  test.skip(!region, 'Configured Trip Editor fixture has no normal region for place focus coverage.');

  const placeId = ensurePlace(state, region!.id, '00000000-0000-0000-0000-000000260201', 'PW located place');
  state.placesById[placeId].location = { latitude: 48.8566, longitude: 2.3522 };
  return { placeName: state.placesById[placeId].name, regionName: region!.name };
}

export function prepareSegmentGeometryState(state: MutableEditorState, useRoute: boolean): void {
  clearNavigationGeometry(state);
  state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
  state.metadata.zoom = 4;

  const region = normalRegion(state);
  test.skip(!region, 'Configured Trip Editor fixture has no normal region for segment geometry coverage.');

  const fromId = ensurePlace(state, region!.id, '00000000-0000-0000-0000-000000260301', 'PW segment from');
  const toId = ensurePlace(state, region!.id, '00000000-0000-0000-0000-000000260302', 'PW segment to');
  state.placesById[fromId].location = { latitude: 40.7128, longitude: -74.006 };
  state.placesById[toId].location = { latitude: 42.3601, longitude: -71.0589 };

  const segmentId = '00000000-0000-0000-0000-000000260303';
  state.segmentsById = {
    [segmentId]: {
      id: segmentId,
      tripId: state.tripId,
      fromPlaceId: fromId,
      toPlaceId: toId,
      mode: 'car',
      estimatedDistanceKm: null,
      estimatedDurationMinutes: null,
      estimatedDurationSource: 'Automatic',
      notesHtml: '',
      route: useRoute
        ? { type: 'LineString', coordinates: [[-74.006, 40.7128], [-73, 41.25], [-71.0589, 42.3601]] }
        : null,
      displayOrder: 1,
      capabilities: editableCapabilities()
    }
  };
  state.segmentOrder = [segmentId];
}

export function prepareRegionCenterOnlyState(state: MutableEditorState): { regionName: string } {
  clearNavigationGeometry(state);
  state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
  state.metadata.zoom = 4;

  const region = normalRegion(state);
  test.skip(!region, 'Configured Trip Editor fixture has no normal region for region center coverage.');
  region!.center = { latitude: 64.1466, longitude: -21.9426 };
  return { regionName: region!.name };
}

export function normalRegion(state: MutableEditorState): any | null {
  return Object.values(state.regionsById).find((item: any) => !item.isShadow) ?? null;
}

export function addAreaGeometry(state: MutableEditorState, regionId: string, areaId: string, name: string): void {
  state.areasById[areaId] = {
    id: areaId,
    tripId: state.tripId,
    regionId,
    name,
    notesHtml: '',
    fillHex: '#22c55e',
    geometry: { type: 'Polygon', coordinates: [[[23, 37], [24, 37], [24, 38], [23, 38], [23, 37]]] },
    displayOrder: 1,
    capabilities: editableCapabilities()
  };
  state.areaOrderByRegionId[regionId] = [areaId];
}

function ensurePlace(state: MutableEditorState, regionId: string, placeId: string, name: string): string {
  const existingId = state.placeOrderByRegionId[regionId]?.find((id: string) => state.placesById[id]) ?? placeId;
  if (!state.placesById[existingId]) {
    state.placesById[existingId] = {
      id: existingId,
      tripId: state.tripId,
      regionId,
      name,
      notesHtml: '',
      address: '',
      location: null,
      iconName: state.options.iconNames[0] ?? 'marker',
      markerColor: state.options.markerColorClasses[0] ?? 'bg-blue',
      displayOrder: 1,
      visitSummary: { placeId: existingId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
      capabilities: editableCapabilities()
    };
    state.placeOrderByRegionId[regionId] = [...(state.placeOrderByRegionId[regionId] ?? []), existingId];
  }

  return existingId;
}

function editableCapabilities(): Record<string, boolean> {
  return {
    canEdit: true,
    canRename: true,
    canDelete: true,
    canReorder: true,
    canMove: true,
    canAddChildren: true,
    canTargetForSearchAdd: false
  };
}

